# API Spec

## Target
- .NET 10+

## Namespace
- `deltalag`

## Core Flow
1. Build a graph with `GraphBuilder`.
2. Compile it with `Build()`.
3. Run it with `Scheduler.Run()` and a `Config`.

## GraphBuilder

### `int AddNode(Action<int>? task = null)`
Adds a node and optional task. Returns the node index.

### `void AddEdge(int from, int to)`
Adds a directed edge (`from -> to`).

### `Scheduler.WorkContext Build()`
Compiles the graph to CSR arrays and prebuilds reverse edges.
Throws `InvalidOperationException` if the graph has a cycle.

## Scheduler

### `void Run(Scheduler.WorkContext ctx, Config config)`
Runs the graph until each node reaches `config.Cycles` work units.

`config` fields used by the scheduler:
- `Cycles`
- `Slack`
- `Threads`
- `Strategy`
- `SeperateRootThread`

## PartitionStrategy
- `ChunkedLinear`
- `Stride`
- `Clustered`
- `Leveled`
- `DepthFirst`

## Scheduler.WorkContext
- `Graph Graph`
- `Node[] NodeArray`
- `int[] Edges`
- `int[] Offsets`
- `int[] ParentEdges`
- `int[] ParentOffsets`

## Node

### `long WorkClaimed`
Highest claimed work index.

### `long WorkDone`
Highest published completed work index.

### `Action<int>? Task`
Task delegate for the node.

### `bool TryClaimWork(long expected)`
Atomically claims the next work unit.

### `void PublishWorkDone(long newValue)`
Publishes completion.

## Slot<T>

### `T Value`
Typed shared value used by node closures.

## Notes
- Graph must be a DAG.
- Scheduler uses lock-free atomics (`Interlocked`/`Volatile`).
- `slack = 0` gives strict lock-step progression.
