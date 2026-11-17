# API Spec

## Target

- .NET 10+

## Namespace

- `deltalag`

## Core flow

1. Build a graph with `GraphBuilder`.
2. Create a `Config`.
3. Compile graph + context using `Build(config)`.
4. Execute with `Scheduler.Run(ctx, config)`.

## GraphBuilder

### `int AddNode(Action<int>? task = null)`

Adds a node and optional task delegate. Returns node index.

### `void AddEdge(int from, int to)`

Adds a directed edge (`from -> to`).

Throws:

- `ArgumentOutOfRangeException` when `from` or `to` is outside `0..nodeCount - 1`.

### `Scheduler.WorkContext Build(Config con)`

Builds CSR forward/reverse edge arrays, allocates the `Node[]` state array, optionally injects k-ary relay nodes when `con.InjectNodes` is `true`, and populates `Tasks` (`Action<int>?[]`) aligned by node index. Original node indices (`0..N - 1`) are preserved when relay nodes are injected; injected nodes have `null` task entries.

Throws:

- `InvalidOperationException` when the graph contains a cycle.
- `IndexOutOfRangeException` when edge target validation fails.

## Scheduler

### `void Run(WorkContext ctx, Config config)`

Runs until each node reaches `config.Cycles` completed work units.

## Config

`Scheduler.Run` uses:

- `Cycles` (`int`) target work units per node
- `Slack` (`int`) allowed downstream lag
- `Threads` (`int`) desired parallel worker count
- `Strategy` (`PartitionStrategy`) partition method
- `SeperateRootThread` (`bool`) optional root-node thread isolation

`GraphBuilder.Build` also uses:

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
- `Leveled` — Groups nodes by topological depth (Kahn's BFS levels) and assigns entire levels round-robin across threads (or isolates level 0 on thread `0` when `SeperateRootThread` is `true`).
  - *Use When:* Layered/staged DAGs where you want all nodes at the same dependency depth co-located on a thread while pipelining across depths (e.g., multi-stage neural network / transform layers or frame-by-frame render passes with `Slack > 0`).
- `DepthFirst` — Orders nodes via depth-first traversal from roots and assigns contiguous chunks of that order to each thread.
  - *Use When:* Deep branching pipelines where you want a thread to follow a producer-to-consumer chain vertically before moving to sibling branches, keeping chain data hot in L1/L2 cache (e.g., tree-like expression evaluation or parallel ray/branch processing).

## Scheduler.WorkContext

`public ref struct WorkContext` (stack-only; cannot be boxed or captured by lambdas/async methods).

- `Node[] NodeArray` — contiguous array of 128-byte cache-line padded `Node` structs
- `int[] Edges` — forward CSR child node indices
- `int[] Offsets` — forward CSR row offsets (`Length == NodeArray.Length + 1`)
- `int[] ParentEdges` — reverse CSR parent node indices
- `int[] ParentOffsets` — reverse CSR row offsets (`Length == NodeArray.Length + 1`)
- `Action<int>?[] Tasks` — per-node task delegates (`Length == NodeArray.Length`) invoked with `workUnit`; `null` for nodes without a task (including injected relay nodes)

## Node

`[StructLayout(LayoutKind.Explicit, Size = 128)] public struct Node`

Two 64-byte cache lines isolating `_workDone` (offset `0`) and `_workClaimed` (offset `64`) to prevent false sharing. Access elements in place (`ctx.NodeArray[i]` or `ref Node`); copying a `Node` by value detaches mutations from scheduler state.

### `long WorkClaimed { get; }`

Count of claimed work units (volatile read; last claimed `workUnit` is `WorkClaimed - 1`).

### `long WorkDone { get; set; }`

Count of published completed work units (volatile read; atomic `Interlocked.Exchange` write).

### `bool TryClaimWork(long expected)`

Atomically claims work unit `expected` (`0 -> 1`, `1 -> 2`, ...) via `Interlocked.CompareExchange` when `_workClaimed == expected`.

### `void PublishWorkDone(long newValue)`

Atomically publishes completed work count (`Interlocked.Exchange`).

## Graph

Low-level CSR container used during graph construction and optimization.

- `int NodeCount` — total number of nodes in the graph
- `readonly List<int> edges` — flattened child indices
- `readonly List<int> offsets` — CSR row offsets (`Count == NodeCount + 1`)

## KaryInjector

### `static Scheduler.WorkContext InjectKaryTree(Graph graph, Config con)`

Rewrites high fan-in / fan-out nodes in `graph` by appending k-ary relay nodes and returns a `WorkContext` with forward and reverse CSR arrays and a zero-initialized `NodeArray`. (`Tasks` is populated by `GraphBuilder.Build`.)

Throws:

- `ArgumentNullException` when `graph` is `null`.

## Slot<T>

### `T Value`

Typed shared value object used by captured closures. Unbuffered; only use when `Slack = 0`.

## Ring<T>

Typed, non-boxing SPSC ring buffer for passing data per edge when `Slack > 0`.

- `T[] Buffers` — backing power-of-two array

### `Ring(int size)`

Allocates a ring buffer of length `size`.

Throws:

- `ArgumentException` when `size` is not a power of two.

### `static Ring<T> FromCon(Config con)`

Creates a `Ring<T>` sized to `con.Slack + 1` rounded up to the next power of two.

Throws:

- `OverflowException` when `con.Slack == int.MaxValue`.

### `T Read(long workUnit)`

Reads the payload for `workUnit` (masked to the buffer capacity).

### `void Write(long workUnit, T payload)`

Writes `payload` for `workUnit` (masked to the buffer capacity).

## Notes

- Graph must be a DAG.
- Scheduling is lock-free and atomic-based.
- `Slack = 0` provides strict lock-step progression.
- If `Slack > 0`, use `Ring<T>` (e.g., `Ring<T>.FromCon(config)`) per edge instead of `Slot<T>` to avoid stale reads or overwrites.
