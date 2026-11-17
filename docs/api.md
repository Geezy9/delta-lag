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
6. `new Scheduler().Run(ctx, config)`

## Key APIs

### GraphBuilder

- `int AddNode(Action<int>? task = null)`
- `void AddEdge(int from, int to)`
- `Scheduler.WorkContext Build(Config con)`

### Scheduler

- `void Run(Scheduler.WorkContext ctx, Config config)`

### Scheduler.WorkContext (`ref struct`)

- `Node[] NodeArray`
- `int[] Edges`
- `int[] Offsets`
- `int[] ParentEdges`
- `int[] ParentOffsets`
- `Action<int>?[] Tasks`

### Node (`struct`, 128 bytes)

- `long WorkClaimed { get; }`
- `long WorkDone { get; set; }`
- `bool TryClaimWork(long expected)`
- `void PublishWorkDone(long newValue)`

### Graph

- `int NodeCount`
- `List<int> edges`
- `List<int> offsets`

### KaryInjector

- `static Scheduler.WorkContext InjectKaryTree(Graph graph, Config con)`

### Slot\<T\>

- `T Value`

### Ring\<T\>

- `T[] Buffers`
- `Ring(int size)`
- `static Ring<T> FromCon(Config con)`
- `T Read(long workUnit)`
- `void Write(long workUnit, T payload)`

### Config (main fields)

- `Cycles`
- `Slack`
- `Threads`
- `Strategy`
- `SeperateRootThread`
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

See [api-spec.md](api-spec.md).
