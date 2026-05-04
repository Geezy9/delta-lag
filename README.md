# delta-lag

> Lock-free, parallel DAG scheduler for C# — distance constraints keep nodes in sync without locks.

**Prerequisites:** .NET 9+

```bash
git clone https://github.com/Geezy9/delta-lag.git
cd delta-lag && dotnet build && dotnet run
```

---

## Quick Example

```csharp
using deltalag;

var slotX = new Slot<int>();
var slotY = new Slot<int>();

var builder = new GraphBuilder();
int producer = builder.AddNode(() => slotX.Value = 42);
int consumer = builder.AddNode(() => slotY.Value = slotX.Value + 1);
builder.AddEdge(producer, consumer);

var scheduler = new Scheduler();
scheduler.Run(builder.Build(), cycles: 100, slack: 0, algo: "Wave");

Console.WriteLine(slotY.Value); // 43
```

Nodes share data through `Slot<T>` — no message passing, no boxing. The scheduler enforces ordering structurally via `WorkDone` counters and a CAS (`Interlocked.CompareExchange`).

---

## How Scheduling Works

Before a node fires, `CanFire` checks two constraints:

```
parent.WorkDone >= node.WorkDone + 1   // parent must be ahead
child.WorkDone  >= node.WorkDone - slack // node can't lap children
```

- `slack = 0` → strict lock-step (safe, no stale reads)  
- `slack > 0` → pipeline parallelism (producer runs ahead; consumer may read older values)

---

## API

**`GraphBuilder`**
| Method | Description |
|---|---|
| `int AddNode(Action? task)` | Add a node. Returns its index. |
| `void AddEdge(int from, int to)` | Add a directed edge. |
| `Graph Build()` | Compile to CSR-encoded graph. |

**`Scheduler`**
| Method | Description |
|---|---|
| `void Run(Graph, int cycles, int slack, string algo)` | Run the graph. |

**`Slot<T>`** — `T Value` — shared data cell captured by closures.

---

## License

MIT
