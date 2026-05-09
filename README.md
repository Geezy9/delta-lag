# delta-lag

> Lock-free, parallel DAG scheduler for C# — distance constraints keep nodes in sync without locks.

**Prerequisites:** .NET 9+

```bash
git clone https://github.com/Geezy9/delta-lag.git
cd delta-lag && dotnet build && dotnet run
```
## Why

TLDR: Fast, Deterministic, lightweight.

Most task schedulers do a great job maximizing throughput, but they pay for it with layers of locks, queues, and work-stealing algorithms. That’s great for general workloads, but overkill when you want predictable, low-overhead parallelism.

Lock-free algorithms like ring buffers show how far you can get with nothing but atomic counters. delta-lag extends that idea to an entire DAG: every node progresses like a position in a ring buffer, coordinated only by distance constraints.

No locks. No stealing. No global queues.


## Quick Example

```csharp
using deltalag;

var slotX = new Slot<int>();
var slotY = new Slot<int>();

var builder = new GraphBuilder();
int producer = builder.AddNode(_ => slotX.Value = 42);
int consumer = builder.AddNode(_ => slotY.Value = slotX.Value + 1);
builder.AddEdge(producer, consumer);

var scheduler = new Scheduler();
scheduler.Run(builder.Build(), cycles: 100, slack: 0, algo: "Wave");

Console.WriteLine(slotY.Value); // 43
```

Nodes share data through `Slot<T>` — no message passing, no boxing. The scheduler enforces ordering structurally via claimed work and published completion using atomic CAS operations.

---

## What Changed Recently

The latest scheduler update changes node tasks from `Action` to `Action<int>`, so each node receives the current work unit when it fires.

It also splits progress tracking into two phases:

- `WorkClaimed` — atomically reserves a work unit so only one thread can execute it
- `WorkDone` — publishes that the work unit fully completed and its outputs are visible

This claim/publish pattern prevents double-firing and reduces race conditions during parallel execution.

---

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

**`GraphBuilder`**
| Method | Description |
|---|---|
| `int AddNode(Action<int>? task)` | Add a node. The task receives the current work unit index. Returns the node index. |
| `void AddEdge(int from, int to)` | Add a directed edge. |
| `Graph Build()` | Compile to CSR-encoded graph. |

**`Scheduler`**
| Method | Description |
|---|---|
| `void Run(Graph, int cycles, int slack, string algo)` | Run the graph. |

**`Node`**
| Member | Description |
|---|---|
| `int WorkClaimed` | The highest work unit atomically reserved for execution. |
| `int WorkDone` | The highest fully published work unit. |
| `bool TryClaimWork(int expected)` | Atomically claims a work unit. |
| `void PublishWorkDone(int newValue)` | Publishes completion after task output is visible. |

**`Slot<T>`** — `T Value` — shared data cell captured by closures.

---
## Important:
When utilizing the slack parameter, please be aware that this library does not provide buffering for `Slot<T>`. Therefore, external buffering mechanisms must be implemented.

## License

MIT
