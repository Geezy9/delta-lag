# delta-lag 
[![Watch a one-minute video tour of delta-lag](https://gitdiagram.com/video-badge.svg)](https://gitdiagram.com/geezy9/delta-lag/video)

**A lock-free, deterministic DAG scheduler for .NET that replaces task queues and work stealing with pure arithmetic.**

Most parallel schedulers spend a surprising amount of time coordinating work: pushing to concurrent queues, stealing tasks across threads, and allocating closures on the heap. `delta-lag` takes a different approach. It compiles your dependency graph into flat, cache-contiguous arrays, assigns nodes statically to threads, and coordinates execution using a single rule: **a node only runs when its parents are ahead of it and its children aren't too far behind.**

No global queues. No locks. No work stealing. No allocations in the hot loop.

> **Version:** `v2.0.0` · Requires **.NET 10+**.

---

## The Core Idea

In a cyclic or tick-based DAG—such as a simulation loop, DSP graph, or multi-stage data pipeline—every node executes for `Cycles` iterations. Instead of scheduling individual tasks dynamically, `delta-lag` tracks progress per node with atomic counters and enforces two distance constraints:

1. **Upstream readiness:** All parents must have completed at least `w + 1` before a node can run work unit `w`.
2. **Bounded backpressure (`Slack`):** All children must have completed at least `w - Slack` before a node can run work unit `w`.

When `Slack = 0`, the graph runs in strict lock-step. When `Slack > 0`, upstream nodes can run ahead of downstream nodes by up to `Slack` iterations, turning your DAG into a bounded, lock-free pipeline without a single mutex or channel.

## Built for Mechanical Sympathy

Every piece of `delta-lag` is designed to stay out of the CPU's way:

- **Zero false sharing by layout:** Each `Node` is explicitly sized to 128 bytes—two 64-byte cache lines—separating `_workDone` (offset `0`) from `_workClaimed` (offset `64`). Reads and compare-and-swap claims never contend on the same cache line, nor spill into adjacent nodes.
- **Flat CSR graph representation:** Forward and reverse edges are stored in Compressed Sparse Row (`int[]`) arrays. Checking eligibility (`CanFire`) is a tight, contiguous scan with zero pointer chasing.
- **Static thread schedules:** Nodes are partitioned across threads once before execution using a chosen `PartitionStrategy`, eliminating runtime queue contention.
- **Zero-allocation edge buffers:** `Ring<T>` provides a non-boxing, power-of-two SPSC ring buffer indexed directly by `workUnit & mask`, sized automatically via `Ring<T>.FromContext(ctx, config)`.

---

## Quick Start

```bash
git clone https://github.com/Geezy9/delta-lag.git
cd delta-lag
dotnet build
dotnet test
```

## Example: Pipelined 3-Stage Graph

Here is a pipeline where `ingest` feeds `transform`, which feeds `sink`, running 64 cycles across 2 threads with `Slack = 4` (allowing up to 4 work units in flight per edge):

```csharp
using deltalag;

var config = new Config
{
    Cycles = 64,                       // Each node executes 64 iterations (workUnit 0..63)
    Slack = 4,                         // Producers can run up to 4 cycles ahead of consumers
    Threads = 2,                       // Static worker thread count
    Strategy = PartitionStrategy.Stride, // Round-robin node assignment across threads
};

// Declare one typed, non-boxing ring buffer per edge (allocated after Build)
Ring<int> rawToTransform = null!;
Ring<int> transformToSink = null!;

var builder = new GraphBuilder();

// Nodes receive the current cycle index (workUnit) and run when
// all parents are ahead (parent.WorkDone >= workUnit + 1)
// and all children are within Slack (child.WorkDone >= workUnit - Slack).
int ingest = builder.AddNode(workUnit =>
{
    rawToTransform.Write(workUnit, workUnit * 10);
});

int transform = builder.AddNode(workUnit =>
{
    int input = rawToTransform.Read(workUnit);
    transformToSink.Write(workUnit, input + 1);
});

int sink = builder.AddNode(workUnit =>
{
    int result = transformToSink.Read(workUnit);
    Console.WriteLine($"Cycle {workUnit}: {result}");
});

// Wire directed dependencies: ingest -> transform -> sink
builder.AddEdge(ingest, transform);
builder.AddEdge(transform, sink);

// Validate acyclicity and compile the graph into flat CSR arrays
var ctx = builder.Build(config);

// FromContext sizes each ring from ctx.MaxEdgeHops * (Slack + 1) rounded up to a power of two
// so an ahead-running producer never overwrites an unread cycle (even with InjectNodes = true).
rawToTransform = Ring<int>.FromContext(ctx, config);
transformToSink = Ring<int>.FromContext(ctx, config);

// Partition nodes across threads and run until all nodes complete 64 cycles
using var scheduler = new Scheduler();
scheduler.Run(ctx);
```

---

## Passing Data Between Nodes

Because nodes can run ahead of their dependents when `Slack > 0`, choose the edge buffer that matches your `Slack` setting:

- **`Ring<T>`** — A typed, non-boxing SPSC ring buffer for per-edge communication when `Slack > 0` or `InjectNodes = true`. **Prefer `Ring<T>.FromContext(ctx, config)`**: it reads `ctx.MaxEdgeHops` from the built graph and sizes the buffer to `ctx.MaxEdgeHops * (Slack + 1)` (rounded up to a power of two), preventing producers from overwriting unread slots. Avoid `Ring<T>.FromConfig(config)` unless you know your graph is strictly single-hop—it hardcodes `maxHops = 1` (`Slack + 1`), so if `InjectNodes = true` adds relay hops (or `Slack` is enforced per hop across a multi-hop path), producers can run `MaxEdgeHops * (Slack + 1)` cycles ahead and silently overwrite unread data.
- **`Slot<T>`** — A single unbuffered value wrapper. Only safe when `Slack = 0` and `MaxEdgeHops == 1` (strict single-hop lock-step).

---

## Tuning Execution

### Partition Strategies

`PartitionStrategy` controls how graph nodes are mapped to worker threads. All strategies preserve determinism and dependency correctness; choosing the right one lets you trade off cache locality against load balance:

| Strategy | Best For |
| --- | --- |
| `Stride` | Evenly distributing heterogeneous workloads across threads |
| `ChunkedLinear` | Contiguous node ranges with minimal scheduling overhead |
| `Clustered` | Keeping connected subgraphs on the same thread for cache locality |
| `Leveled` | Grouping nodes by topological depth |
| `DepthFirst` | Following long dependency chains on a single thread |

Set `SeparateRootThread = true` in `Config` to isolate root nodes on their own thread and reduce startup latency for downstream stages, and `SeparateSinkThread = true` to isolate sink nodes (out-degree `0`) on their own thread (requires at least one remaining general worker thread).

### Automatic Fan-In / Fan-Out Smoothing

Wide fan-in or fan-out nodes can become a bottleneck during dependency checks. Setting `InjectNodes = true` in `Config` enables `KaryInjector`, which automatically rewrites high-degree edges into balanced *k*-ary relay trees (controlled by `K`, `MaxFanIn`, and `MaxFanOut`) during `GraphBuilder.Build(config)`.

### Reusable Worker Pool & Thread Affinity

`Scheduler` implements `IDisposable` and maintains a persistent pool of background worker `Thread`s across `Run(ctx)` calls, avoiding per-run thread creation overhead. If `ThreadCount` or `ThreadAffinity` changes between runs, the pool is automatically reconfigured.

Set `ThreadAffinity = true` in `Config` to pin worker `t` to logical core `t % Environment.ProcessorCount` (supported on Windows via `SetThreadAffinityMask` and Linux via `sched_setaffinity`, with graceful fallback to unpinned execution on unsupported platforms). Call `scheduler.Dispose()` (or use `using`) when finished to shut down and join worker threads cleanly.

---

## Documentation

- [API Specification](docs/api-spec.md)
- [API Quick Reference](docs/api.md)
- [Power User Guide: Extending delta-lag](docs/extending.md)
## License

MIT
