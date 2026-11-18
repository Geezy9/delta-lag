# Pro User Guide: Extending `delta-lag`

`delta-lag` is intentionally minimal: it has no plugin interfaces, no virtual dispatch in the hot loop, and no built-in dependency injection. Instead, it gives you a deterministic, lock-free execution engine where **nodes are plain `Action<int>` delegates** and **edges are whatever data structures those delegates close over**.

Because `Ring<T>` and `Slot<T>` are just convenience types—not privileged primitives—you can replace them with your own arena allocators, broadcast buffers, SoA column stores, or typed graph DSLs from outside the library with zero modifications to `delta-lag`.

---

## 1. The Execution & Memory Contract

Before building custom primitives, you need to know the exact guarantees `Scheduler` provides. Every custom buffer relies on these four invariants:

1. **Sequential execution per node:**
   For any node `i`, its `Action<int>` is invoked for `workUnit = 0, 1, ..., Cycles - 1` **strictly in order** and **never concurrently with itself**. Even when a partition strategy allows multiple threads, `TryClaimWork` + `PublishWorkDone` ensures at most one thread executes `workUnit` for node `i`.

2. **Happens-before memory visibility:**
   When producer `u` completes `workUnit = w`, `Scheduler` calls `PublishWorkDone(w + 1)` (`Interlocked.Exchange`, a full memory fence). Before consumer `v` can run `workUnit = w`, `CanFire` reads `u.WorkDone` (`Volatile.Read`, acquire semantics) and verifies `u.WorkDone >= w + 1`.
   - **Result:** Any memory written by `u` during `w` is guaranteed to be visible to `v` during `w` **without locks, atomics, or extra fences in your buffer**.

3. **Bounded in-flight window (`Slack + 1`):**
   Along any direct edge `u -> v`, `CanFire` prevents `u` from starting `w` unless `v.WorkDone >= w - Slack`.
   - When `v` is at `WorkDone = 0` and `Slack = 4`, `u` can execute `w = 0, 1, 2, 3, 4` (`5` work units = `Slack + 1`) before stalling.
   - **Result:** At most `Slack + 1` work units are live on an edge at any instant. Any cyclic buffer with capacity `>= Slack + 1` will never be overwritten by `u` while `v` is still reading an older slot.

4. **Stack-only compiled graph (`WorkContext`):**
   `GraphBuilder.Build(config)` returns `Scheduler.WorkContext`, a `ref struct`. It cannot be stored in class fields, boxed, or captured by lambdas. Keep your graph-building helper methods returning `WorkContext` directly or executing `Scheduler.Run(ctx)` within the same stack frame.

> **Important caveat with `InjectNodes = true`:**
> `KaryInjector` inserts relay nodes between high-degree nodes, and `Slack` is enforced **per hop**. Across an `H`-hop path `u -> r_1 -> ... -> v`, `u` can execute up to `H * (Slack + 1)` work units before `v` advances (even at `Slack = 0`, 2 hops allow 2 items in flight). `GraphBuilder.Build(config)` exposes this bound as `ctx.MaxEdgeHops`. When `InjectNodes = true`, use `Ring<T>.FromContext(ctx, config)` (or size your external edge buffers for `ctx.MaxEdgeHops * (Slack + 1)`).

---

## 2. Example: Replacing `Ring<T>` with a Cycle-Indexed Arena Buffer

`Ring<T>` is ideal for fixed-size values (`int`, `float`, small structs, or object references). However, if your nodes produce **variable-length batches, strings/spans, or large temporary arrays**, copying into a `Ring<T>` or allocating per cycle creates GC pressure.

Because at most `Slack + 1` cycles are ever in flight simultaneously, you can use a **Cycle-Indexed Arena Buffer**: pre-allocate `N` arenas (where `N = RoundUpToPowerOf2(Slack + 1)`), and map each `workUnit` to `arenas[workUnit & mask]`.

- When the producer starts `workUnit = w`, it resets `arenas[w & mask]` to offset `0` and bump-allocates into it.
- That reset is **100% race-free** because `CanFire` guarantees the consumer has already finished `w - N` (and therefore will never touch `arenas[w & mask]` for the previous epoch again).

### Implementation

```csharp
using System.Numerics;
using System.Runtime.CompilerServices;
using deltalag;

/// <summary>
/// Zero-GC, bump-allocated arena buffer for variable-length unmanaged payloads
/// across a single producer -> consumer edge (or 1-to-many broadcast).
/// </summary>
public sealed class CycleArena<T> where T : unmanaged
{
	private readonly T[][] _slabs;
	private readonly int[] _heads;
	private readonly int _mask;

	public CycleArena(int slotCount, int elementsPerSlab)
	{
		if (slotCount <= 0 || (slotCount & (slotCount - 1)) != 0)
			throw new ArgumentException("Slot count must be a positive power of two.", nameof(slotCount));

		_slabs = new T[slotCount][];
		_heads = new int[slotCount];
		_mask = slotCount - 1;

		for (int i = 0; i < slotCount; i++)
			_slabs[i] = new T[elementsPerSlab];
	}

	public static CycleArena<T> FromConfig(Config config, int elementsPerSlab)
	{
		int slots = (int)BitOperations.RoundUpToPowerOf2((uint)(config.Slack + 1));
		return new CycleArena<T>(slots, elementsPerSlab);
	}

	/// <summary>
	/// Called once by the producer at the start of workUnit to reclaim the slab
	/// from (workUnit - slotCount).
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Reset(long workUnit)
	{
		_heads[(int)workUnit & _mask] = 0;
	}

	/// <summary>
	/// Bump-allocates a writable span inside the slab for workUnit.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public Span<T> Alloc(long workUnit, int count)
	{
		int slot = (int)workUnit & _mask;
		int start = _heads[slot];
		int next = start + count;

		if (next > _slabs[slot].Length)
			throw new InvalidOperationException(
				$"Arena slab {slot} exhausted (requested {next}, capacity {_slabs[slot].Length}).");

		_heads[slot] = next;
		return _slabs[slot].AsSpan(start, count);
	}

	/// <summary>
	/// Reads all data written into the slab for workUnit.
	/// Safe to call from one or more downstream consumer nodes.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ReadOnlySpan<T> ReadAll(long workUnit)
	{
		int slot = (int)workUnit & _mask;
		return _slabs[slot].AsSpan(0, _heads[slot]);
	}
}
```

### Usage in a Graph

```csharp
using deltalag;

var config = new Config
{
	Cycles = 128,
	Slack = 4,
	Threads = 2,
	Strategy = PartitionStrategy.Stride,
};

// 8 arena slabs (Slack + 1 rounded to power of two), 1024 floats per slab
var arena = CycleArena<float>.FromConfig(config, elementsPerSlab: 1024);

var builder = new GraphBuilder();

int producer = builder.AddNode(workUnit =>
{
	// Safe: all consumers of (workUnit - 8) have already completed.
	arena.Reset(workUnit);

	int batchSize = (workUnit % 16) + 1;
	Span<float> dst = arena.Alloc(workUnit, batchSize);
	for (int i = 0; i < dst.Length; i++)
		dst[i] = workUnit + i * 0.5f;
});

int consumer = builder.AddNode(workUnit =>
{
	ReadOnlySpan<float> batch = arena.ReadAll(workUnit);
	float sum = 0;
	foreach (float v in batch)
		sum += v;
	// process sum...
});

builder.AddEdge(producer, consumer);

var ctx = builder.Build(config);
using var scheduler = new Scheduler();
scheduler.Run(ctx);
```

If a producer emits multiple distinct chunks per cycle (e.g., header + payload, or variable slices for different consumers), you can also store `(int Offset, int Length)` descriptors in a `Ring<(int Offset, int Length)>` alongside the `CycleArena<T>`.

---

## 3. Other Common External Patterns

### 1-to-Many Broadcast Buffer

`Ring<T>` is documented as SPSC, but **1-to-many broadcast** works out of the box with a single `Ring<T>` (or `CycleArena<T>`) as long as:
1. Only **one** producer writes `workUnit`.
2. Every consumer reading `workUnit` has a directed edge `producer -> consumer` in `GraphBuilder`.
3. Consumers treat the read payload as **read-only**.

Because `CanFire` checks **all** children (`edges`) of `producer`, the producer cannot advance past `w + Slack` until the *slowest* consumer has finished `w`.

### Many-to-1 Reduction (Fan-In)

For `N` producers feeding 1 consumer, **do not** share a single `Ring<T>` across producers—that would cause concurrent writes to the same slot. Instead, give each edge `(producer_k -> consumer)` its own `Ring<T>` (or `T[]` of `N` rings), and have the consumer read from all `N` rings at `workUnit`:

```csharp
var inputs = new Ring<double>[producerCount];
for (int p = 0; p < producerCount; p++)
	inputs[p] = Ring<double>.FromConfig(config);

// Each producer k writes to inputs[k].Write(workUnit, value);
// Consumer reads inputs[0..N-1].Read(workUnit) and reduces.
```

### Building a Typed Graph DSL on Top of `GraphBuilder`

`GraphBuilder` uses raw `int` node IDs and untyped `Action<int>?` delegates. You can wrap it in a typed fluent layer without touching `deltalag`:

```csharp
public readonly record struct OutputPort<T>(int NodeId, Ring<T> Buffer);

public sealed class TypedPipeline
{
	private readonly GraphBuilder _builder = new();
	private readonly Config _config;

	public TypedPipeline(Config config) => _config = config;

	public OutputPort<TOut> AddSource<TOut>(Func<int, TOut> produce)
	{
		var ring = Ring<TOut>.FromConfig(_config);
		int id = _builder.AddNode(w => ring.Write(w, produce(w)));
		return new OutputPort<TOut>(id, ring);
	}

	public OutputPort<TOut> AddStage<TIn, TOut>(
		OutputPort<TIn> input,
		Func<int, TIn, TOut> transform)
	{
		var outRing = Ring<TOut>.FromConfig(_config);
		int id = _builder.AddNode(w =>
		{
			TIn val = input.Buffer.Read(w);
			outRing.Write(w, transform(w, val));
		});
		_builder.AddEdge(input.NodeId, id);
		return new OutputPort<TOut>(id, outRing);
	}

	public void AddSink<TIn>(OutputPort<TIn> input, Action<int, TIn> consume)
	{
		int id = _builder.AddNode(w => consume(w, input.Buffer.Read(w)));
		_builder.AddEdge(input.NodeId, id);
	}

	public void Run()
	{
		var ctx = _builder.Build(_config);
		using var scheduler = new Scheduler();
		scheduler.Run(ctx);
	}
}
```

---

## 4. Performance & Mechanical Sympathy Pitfalls

When writing custom external primitives, keep these hardware-level details in mind:

- **Watch for false sharing in custom slot arrays:**
  `deltalag`'s internal `Node` struct is padded to 128 bytes (two 64-byte cache lines) so adjacent nodes never share a cache line. In contrast, a plain `int[]` or `long[]` in a custom buffer packs 8–16 slots per 64-byte cache line. If producer and consumer run on different cores with small `Slack` (e.g., `Slack = 1`), writing `slot[w + 1]` and reading `slot[w]` can ping-pong the cache line. For ultra-high-frequency edges with tiny payloads, pad each slot struct to 64 bytes (`[StructLayout(LayoutKind.Explicit, Size = 64)]`).
- **Avoid heap allocations inside `Action<int>`:**
  `Scheduler` does zero allocations in its run loop. If your custom buffer allocates objects, boxes value types, or resizes collections per `workUnit`, GC pauses will dominate your latency. Pre-allocate everything before `Scheduler.Run`.
- **Re-running a graph (rebuild `WorkContext` vs. manual counter manipulation):**
  `GraphBuilder.Build(config)` allocates a fresh `Node[]` initialized to `WorkDone = 0` and `WorkClaimed = 0`. A `WorkContext` is single-use for `0..Cycles - 1` (since `Node.WorkDone` reaches `Cycles`). **The ideal, preferred path is to rebuild `WorkContext` via `builder.Build(config)` whenever you need to run another batch of cycles** (and reset any external buffer state if needed). If rebuilding `WorkContext` is not feasible for your use case, `WorkContext` fields (`NodeArray`, `Cycles`, etc.) and `Node` members are `public`, so you *can* manipulate or reset node counters yourself (e.g., resetting `ctx.NodeArray` or calling `PublishWorkDone` / `TryClaimWork` in place), **but this comes with no guarantees**—manually mutating counters or context arrays bypasses `GraphBuilder.Build`'s clean initialization contract and can cause stalled threads, skipped or duplicate cycles, or data races if counters and external buffers fall out of sync.
- **Thread count clamping:**
  `GraphBuilder.Build(config)` clamps positive `Threads` to `Math.Min(config.Threads, Environment.ProcessorCount)` (or defaults to `Math.Min(Environment.ProcessorCount, Math.Max(1, nodeCount / 4))` when `Threads <= 0`) and then caps to `nodeCount` (since you cannot have more active worker threads than nodes). External buffers sized by thread count should not assume `config.Threads` exceeds `nodeCount`.
