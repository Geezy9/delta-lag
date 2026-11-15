# delta-lag

> Lock-free, parallel DAG scheduler for C# — distance constraints keep nodes in sync without locks.

Status: Experimental. Not production‑ready. APIs may change.

**Prerequisites:** .NET 10+

```bash
git clone https://github.com/Geezy9/delta-lag.git
cd delta-lag
dotnet build
```
## Why

TLDR: Fast, Deterministic, lightweight.

Most task schedulers do a great job maximizing throughput, but they pay for it with layers of locks, queues, and work-stealing algorithms. That’s great for general workloads, but overkill when you want predictable, low-overhead parallelism.

Lock-free algorithms like ring buffers show how far you can get with nothing but atomic counters. delta-lag extends that idea to an entire DAG: every node progresses like a position in a ring buffer, coordinated only by distance constraints.

No locks. No stealing. No global queues.


## Warning:

When utilizing the slack parameter, please be aware that this library does not provide buffering for `Slot<T>`. Therefore, external buffering mechanisms must be implemented.

## Docs

- [API spec](docs/api-spec.md)

## Quick Example with a ring buffer.

```csharp
using deltalag;

const int BufferSize = 8;
int[] buffer = new int[BufferSize];

var builder = new GraphBuilder();

// Producer node: Writes data into ring buffer
// Each work-unit gets assigned to a buffer slot using modulo
int producer = builder.AddNode(workUnit =>
{
    int index = workUnit % BufferSize;
    buffer[index] = workUnit;
});

// Consumer node: Reads data from ring buffer
// Uses the same modulo logic to read from the correct slot
int consumer = builder.AddNode(workUnit =>
{
    int index = workUnit % BufferSize;
    int value = buffer[index];
    
    // NOTE: Console.WriteLine is blocking and slow - used here only for demonstration.
    // In production code, use non-blocking operations to avoid performance degradation.
    Console.WriteLine($"Consumed {value} from slot {index}");
});

// Define dependency: Consumer must execute after Producer for each work-unit
// This prevents the consumer from reading before the producer writes
builder.AddEdge(producer, consumer);

// Build compiles the graph and pre-allocates all scheduling structures upfront.
// Run consumes the WorkContext with zero setup allocations at scheduling time.
// - cycles:   32 work-units will be processed
// - slack:    0 means strict ordering (no lookahead)
// - threads:  2 parallel execution threads
// - strategy: controls how nodes are distributed across threads
var ctx = builder.Build();
// if you need to mutate the state of the graph you can do so by modifying the returned WorkContext before passing it to a run.
var scheduler = new Scheduler();
scheduler.Run(ctx, cycles: 32, slack: 0, threads: 2, strategy: PartitionStrategy.Stride);

```

Nodes share data through `Slot<T>` — no message passing, no boxing. The scheduler enforces ordering structurally via claimed work and published completion using atomic CAS operations.

---

## Partition Strategies

`PartitionStrategy` controls how graph nodes are divided across threads. The right choice depends on the shape of your graph and how work is distributed.

| Scenario | Recommended strategy |
|---|---|
| Linear pipeline/Sparse Graph, uniform work | `Stride` |
| Dense DAG with spatial locality | `ChunkedLinear` |
| 
| Unsure | `Stride` — generally more stable latency |

All strategies produce identical, deterministic output. The difference is purely in how threads are assigned nodes; correctness is unaffected.

## How Scheduling Works

Before a node fires, `CanFire` checks two constraints:

```
parent.WorkDone >= node.WorkDone + 1    // parent must be ahead
child.WorkDone  >= node.WorkDone - slack // node can't lap children
```

If the node is eligible, the scheduler:

1. computes the next work unit with `CanFire`
2. atomically claims that work unit with `TryClaimWork`
3. runs `Task(workUnit)`
4. publishes completion with `PublishWorkDone(workUnit + 1)`

- `slack = 0` → strict lock-step (safe, no stale reads)  
- `slack > 0` → pipeline parallelism (producer runs ahead; consumer may read older values)

---

## API

API is documented in [`docs/api-spec.md`](docs/api-spec.md).



## License

MIT
