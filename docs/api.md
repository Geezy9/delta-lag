# API Quick Reference

## Target

- .NET 10+

## Namespace

- `deltalag`

## Core flow

1. `GraphBuilder builder = new();`
2. `builder.AddNode(...)`
3. `builder.AddEdge(from, to)`
4. `Config config = new() { ... }`
5. `var ctx = builder.Build(config)`
6. `new Scheduler().Run(ctx)`

## Key APIs

### GraphBuilder

- `int AddNode(Action<int>? task = null)`
- `void AddEdge(int from, int to)`
- `Scheduler.WorkContext Build(Config con)`

### Scheduler

- `void Run(Scheduler.WorkContext ctx)`

### Scheduler.WorkContext (`ref struct`)

Compiled graph and execution context returned by `GraphBuilder.Build` and consumed by `Scheduler.Run`. All fields are `public`:


- *Do not touch unless you know what you're doing. Manipulating these can cause undefined behavior.*

- `Node[] NodeArray` — Per-node atomic counter array
- `int[] Edges`, `int[] Offsets` — Forward CSR adjacency arrays
- `int[] ParentEdges`, `int[] ParentOffsets` — Reverse CSR adjacency arrays
- `Action<int>?[] Tasks` — Node task delegates indexed by node ID
- `int Cycles` — Target work units per node
- `int Slack` — Allowed downstream lag
- `int ThreadCount` — Effective worker thread count
- `int[][] ThreadSchedules` — Per-thread node schedules
- `int MaxEdgeHops` — Maximum hop distance between any original producer and consumer after optional k-ary relay injection (`1` when no relay nodes are injected)

### Node (`struct`, 128-byte cache-padded)

- `long WorkClaimed`
- `long WorkDone`
- `bool TryClaimWork(long expected)`
- `void PublishWorkDone(long newValue)`

### Slot\<T\>

- `T Value`

### Ring\<T\>

- `Ring(int size)`
- `static Ring<T> FromCon(Config con)`
- `static Ring<T> FromCon(Config con, int maxHops)`
- `static Ring<T> FromContext(Scheduler.WorkContext ctx, Config con)`
- `T Read(long workUnit)`
- `void Write(long workUnit, T payload)`

### Config (main fields)

- `Cycles`
- `Slack`
- `Threads`
- `Strategy`
- `SeparateRootThread`
- `SeparateSinkThread`
- `InjectNodes`
- `K`
- `MaxFanIn`
- `MaxFanOut`

### PartitionStrategy

- `ChunkedLinear`
- `Stride`
- `Clustered`
- `Leveled`
- `DepthFirst`

## Full spec

See [api-spec.md](api-spec.md) and the [Pro User Guide](extending.md) for extending `delta-lag` externally.
