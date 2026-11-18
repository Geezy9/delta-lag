# API Spec

## Target

- .NET 10+

## Namespace

- `deltalag`

## Core flow

1. Build a graph with `GraphBuilder`.
2. Create a `Config`.
3. Compile graph, schedules, and execution settings into `WorkContext` using `Build(config)`.
4. Execute with `Scheduler.Run(ctx)`.

## GraphBuilder

### `int AddNode(Action<int>? task = null)`

Adds a node and optional task delegate. Returns node index.

### `void AddEdge(int from, int to)`

Adds a directed edge (`from -> to`).

Throws:

- `ArgumentOutOfRangeException` when `from` or `to` is outside `0..nodeCount - 1`.

### `Scheduler.WorkContext Build(Config con)`

Builds CSR forward/reverse edge arrays, allocates the `Node[]` state array, pre-allocates `ThreadSchedules`, captures `Cycles`, `Slack`, and `ThreadCount`, optionally injects k-ary relay nodes when `con.InjectNodes` is `true`, and populates `Tasks` (`Action<int>?[]`) aligned by node index. Original node indices (`0..N - 1`) are preserved when relay nodes are injected; injected nodes have `null` task entries.

Throws:

- `InvalidOperationException` when the graph contains a cycle, or when `SeparateRootThread` / `SeparateSinkThread` leaves fewer than 1 general worker thread.
- `IndexOutOfRangeException` when edge target validation fails.

## Scheduler

### `void Run(WorkContext ctx)`

Runs until each node reaches `ctx.Cycles` completed work units using the pre-allocated schedules and configuration stored in `ctx`.

## Config

`GraphBuilder.Build` reads all configuration and bakes execution settings into `Scheduler.WorkContext`:

- `Cycles` (`int`) target work units per node
- `Slack` (`int`) allowed downstream lag
- `Threads` (`int`) desired parallel worker count
- `Strategy` (`PartitionStrategy`) partition method
- `SeparateRootThread` (`bool`) optional root-node thread isolation
- `SeparateSinkThread` (`bool`) optional sink-node (out-degree `0`) thread isolation
- `InjectNodes` (`bool`) enables k-ary fan-in/fan-out node injection
- `K` (`int`) branch factor for injected tree nodes
- `MaxFanIn` (`int`) fan-in limit before injection
- `MaxFanOut` (`int`) fan-out limit before injection

## PartitionStrategy

Controls how nodes are assigned to worker threads (all strategies preserve correctness and deterministic outcomes):

- `ChunkedLinear` — Splits node indices `0..N - 1` into contiguous slices across threads (`thread t` gets a block of consecutive node IDs).
  - *Use When:* Simple linear pipelines or graphs where nodes were added in a natural locality order (e.g., an audio/signal processing chain `source -> filter -> gain -> sink` split into stages per thread).
- `Stride` — Distributes node indices round-robin (`node i` goes to `thread i % threadCount`).
  - *Use When:* Uniform or wide graphs where adjacent node IDs have uneven cost or heavy contention, so interleaving balances load across threads (e.g., many independent feature extractors where odd/even or grouped nodes vary in CPU cost).
- `Clustered` — Traverses connected components breadth-first (following both children and parents) and assigns contiguous chunks of that order to each thread.
  - *Use When:* Graphs with multiple loosely connected or independent subgraphs where keeping neighboring nodes on the same thread improves cache locality and cuts cross-thread synchronization (e.g., running several independent simulation entities or physics islands in one graph).
- `Leveled` — Groups nodes by topological depth (Kahn's BFS levels) and assigns entire levels round-robin across threads (or isolates level 0 on thread `0` when `SeparateRootThread` is `true`).
  - *Use When:* Layered/staged DAGs where you want all nodes at the same dependency depth co-located on a thread while pipelining across depths (e.g., multi-stage neural network / transform layers or frame-by-frame render passes with `Slack > 0`).
- `DepthFirst` — Orders nodes via depth-first traversal from roots and assigns contiguous chunks of that order to each thread.
  - *Use When:* Deep branching pipelines where you want a thread to follow a producer-to-consumer chain vertically before moving to sibling branches, keeping chain data hot in L1/L2 cache (e.g., tree-like expression evaluation or parallel ray/branch processing).

## Scheduler.WorkContext

`public ref struct WorkContext` (stack-only; cannot be boxed or captured by lambdas/async methods).

Compiled graph and execution context returned by `GraphBuilder.Build` and passed to `Scheduler.Run`. All fields are `public` and populated by `GraphBuilder.Build`:

- `Node[] NodeArray` — Per-node atomic counter array (`128`-byte padded `Node` structs).
- `int[] Edges`, `int[] Offsets` — Forward CSR adjacency arrays.
- `int[] ParentEdges`, `int[] ParentOffsets` — Reverse CSR adjacency arrays.
- `Action<int>?[] Tasks` — Task delegates indexed by node ID (`null` for injected relay nodes).
- `int Cycles` — Target work units per node.
- `int Slack` — Allowed downstream lag.
- `int ThreadCount` — Effective worker thread count.
- `int[][] ThreadSchedules` — Pre-partitioned node ID lists per worker thread.
- `int MaxEdgeHops` — Maximum hop distance between any original node and its original children after optional `KaryInjector` relay insertion (`1` when `InjectNodes = false` or no relay nodes are injected).

## Node

`public struct Node` (`128` bytes via `[StructLayout(LayoutKind.Explicit, Size = 128)]`, separating `_workDone` at offset `0` and `_workClaimed` at offset `64` to prevent false sharing). Access elements in place via `ctx.NodeArray[i]`.

- `long WorkClaimed` — Volatile read of the current claimed work counter.
- `long WorkDone` — Volatile read of the current completed work counter.
- `bool TryClaimWork(long expected)` — Atomically advances `_workClaimed` from `expected` to `expected + 1`.
- `void PublishWorkDone(long newValue)` — Atomically publishes `_workDone = newValue`.

## Slot<T>

### `T Value`

Typed shared value object used by captured closures. Unbuffered; only safe when `Slack = 0` and `MaxEdgeHops == 1`.

## Ring<T>

Typed, non-boxing SPSC ring buffer for passing data per edge when `Slack > 0` or `MaxEdgeHops > 1`.

### `Ring(int size)`

Allocates a ring buffer of length `size`.

Throws:

- `ArgumentException` when `size` is not a power of two.

### `static Ring<T> FromCon(Config con)`

Creates a `Ring<T>` sized for single-hop edges (`con.Slack + 1` rounded up to the next power of two). Equivalent to `FromCon(con, maxHops: 1)`.

Throws:

- `ArgumentNullException` when `con` is `null`.
- `ArgumentOutOfRangeException` when `con.Slack < 0`.
- `OverflowException` when `con.Slack == int.MaxValue`.

### `static Ring<T> FromCon(Config con, int maxHops)`

Creates a `Ring<T>` sized for multi-hop relay paths (`maxHops * (con.Slack + 1)` rounded up to the next power of two).

Throws:

- `ArgumentNullException` when `con` is `null`.
- `ArgumentOutOfRangeException` when `con.Slack < 0` or `maxHops < 1`.
- `OverflowException` when the computed capacity overflows `int`.

### `static Ring<T> FromContext(Scheduler.WorkContext ctx, Config con)`

Creates a `Ring<T>` sized from `ctx.MaxEdgeHops` (`FromCon(con, Math.Max(1, ctx.MaxEdgeHops))`). Use this when `InjectNodes = true` so edge buffers automatically accommodate injected relay depth.

### `T Read(long workUnit)`

Reads the payload for `workUnit` (masked to the buffer capacity).

### `void Write(long workUnit, T payload)`

Writes `payload` for `workUnit` (masked to the buffer capacity).

## Notes

- Graph must be a DAG.
- Scheduling is lock-free and atomic-based.
- `Slack = 0` with `MaxEdgeHops == 1` provides strict lock-step progression.
- If `Slack > 0` or `InjectNodes = true` (with `MaxEdgeHops > 1`), use `Ring<T>` (e.g., `Ring<T>.FromContext(ctx, config)`) per edge instead of `Slot<T>` to avoid stale reads or overwrites.
- A `WorkContext` is single-use for `0..Cycles - 1`. The preferred path when re-running is to rebuild a fresh `WorkContext` via `GraphBuilder.Build(config)`. Although `WorkContext` fields (`NodeArray`, `Cycles`, etc.) and `Node` members are `public`, users who choose to manipulate node counters directly do so with no guarantees.
