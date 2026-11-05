# delta-lag

> Parallel DAG scheduler for C# — distance constraints keep nodes in sync without locks.

Status: Experimental. Not production‑ready. APIs may change.

**Prerequisites:** .NET 10+

```bash
git clone https://github.com/Geezy9/delta-lag.git
cd delta-lag && dotnet build && dotnet run
```
## Why and What

delta-lag is primarily a streaming DAG dataflow engine. You build the graph once, then execute it repeatedly.

Its architecture is designed for workloads where you know exactly what work must be performed, in what order, and how many times. In other words, a fixed DAG rather than a dynamically evolving task graph.

Areas where it excels:

- Audio processing
- DSP pipelines
- Telemetry fusion
- Long-running, low-overhead processing loops
- Game engine update pipelines
- Time-budgeted execution loops
   

## How it differs

**TL;DR:** 
- No global ready queue.
- No work stealing.
- No runtime graph traversal.
- No allocations during execution.
- No Locks

In most schedulers, work flows through nodes. In delta-lag, nodes advance through work.

That shifts the scheduling question from:

"What should run next?"

to:

"How far ahead is each node relative to its neighbors?"

By combining that model with a static CSR graph and pre-allocated scheduling structures, delta-lag minimizes runtime overhead and synchronization costs.

The tradeoff is that graphs are fixed at runtime and the scheduler does not perform dynamic load balancing.

No locks. No work stealing. No global queues.


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

## Warning:

When utilizing the slack parameter, please be aware that this library does not provide buffering for `Slot<T>`. Therefore, external buffering mechanisms must be implemented.

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

### `PartitionStrategy.Stride` *(recommended default)*

Each thread is assigned every N-th node, where N is the thread count.

- Thread 0 → nodes 0, 4, 8, 12, …
- Thread 1 → nodes 1, 5, 9, 13, …
- Thread 2 → nodes 2, 6, 10, 14, …

**Best for:** pipelines and DAGs where nodes are roughly uniform in cost. Stride interleaving naturally spreads hot nodes across threads and tends to reduce contention on adjacent nodes.

### `PartitionStrategy.ChunkedLinear`

Each thread is assigned a contiguous block of nodes.

- Thread 0 → nodes 0–15
- Thread 1 → nodes 16–31
- Thread 2 → nodes 32–47

**Best for:** graphs where nodes have strong spatial locality or when cache-line affinity within a range matters. Can perform worse than Stride when nodes at the boundary of a pipeline are frequently contended.

### Choosing a strategy

| Scenario | Recommended strategy |
|---|---|
| Linear pipeline, uniform work | `Stride` |
| Dense DAG with spatial locality | `ChunkedLinear` |
| Unsure | `Stride` — generally lower p95 latency |

Both strategies produce identical, deterministic output. The difference is purely in how threads are assigned nodes; correctness is unaffected.



---

## Housekeeping 
- **Release 1.0.2** has a deterministic Benchmark Harness, a Dataset, and Dataset Inspector
- **Help Wanted** If you want to help out, Open a Pr. 

## API

**`GraphBuilder`**
| Method | Description |
|---|---|
| `int AddNode(Action<int>? task)` | Add a node. The task receives the current work unit index. Returns the node index. |
| `void AddEdge(int from, int to)` | Add a directed edge. |
| `WorkContext Build()` | Compile to CSR-encoded graph and pre-allocate all scheduling structures. |

**`Scheduler`**
| Method | Description |
|---|---|
| `void Run(WorkContext ctx, int cycles, int slack, int threads, PartitionStrategy strategy)` | Run the pre-built work context using the specified partition strategy. |

**`PartitionStrategy`**
| Value | Description |
|---|---|
| `Stride` | Distributes nodes across threads in an interleaved pattern (every N-th node per thread). Generally lower p95 latency on linear pipelines. |
| `ChunkedLinear` | Distributes nodes across threads as contiguous blocks. Favours spatial locality within a range. |

**`Scheduler.WorkContext`**
| Member | Description |
|---|---|
| `Graph Graph` | The underlying CSR-encoded graph. |
| `Node[] NodeArray` | Pre-materialized node array. |
| `int[] Edges` | Pre-materialized forward edge array. |
| `int[] Offsets` | Pre-materialized CSR offset array. |
| `int[] ParentEdges` | Pre-built reverse edge array for parent lookups. |
| `int[] ParentOffsets` | Pre-built reverse CSR offsets for parent lookups. |

**`Node`**
| Member | Description |
|---|---|
| `int WorkClaimed` | The highest work unit atomically reserved for execution. |
| `int WorkDone` | The highest fully published work unit. |
| `bool TryClaimWork(int expected)` | Atomically claims a work unit. |
| `void PublishWorkDone(int newValue)` | Publishes completion after task output is visible. |

**`Slot<T>`** — `T Value` — shared data cell captured by closures.



## License

MIT
