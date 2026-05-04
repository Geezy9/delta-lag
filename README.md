# delta-lag

> **Distance Constraint based DAG engine for parallel workloads**

`delta-lag` is a lightweight, lock-free C# scheduling engine that executes work across a Directed Acyclic Graph (DAG) in parallel. Each node holds a closure (an `Action`) representing a unit of work. The scheduler fires nodes concurrently across threads, enforcing ordering through *distance constraints* rather than locks or barriers.

---

## Table of Contents

- [Core Concepts](#core-concepts)
- [How Closures Work](#how-closures-work)
- [Distance Constraints & the Slack Parameter](#distance-constraints--the-slack-parameter)
- [Architecture](#architecture)
- [Getting Started](#getting-started)
- [API Reference](#api-reference)

---

## Core Concepts

| Concept | Description |
|---|---|
| **Node** | A vertex in the graph. Holds an `Action` (closure) and a `WorkDone` counter. |
| **Slot<T>** | A typed, allocation-free data cell shared between nodes via closure capture. |
| **Graph** | A CSR-encoded DAG of nodes and directed edges. |
| **Scheduler** | Drives parallel execution using the Wave algorithm and distance constraints. |
| **Slack** | The maximum number of cycles a node is allowed to run *ahead of* its children. |

---

## How Closures Work

This is the central design of `delta-lag`. Each node performs its work through a plain `Action` delegate — a **closure** — that captures `Slot<T>` instances by reference at construction time.

### The Pattern

```csharp
// 1. Create shared data slots — typed, no boxing.
var slotA = new Slot<float>();
var slotB = new Slot<float>();

// 2. Build the graph.
var builder = new GraphBuilder();

// Node 0 — producer: writes into slotA
int n0 = builder.AddNode(() => {
    slotA.Value = ComputeSomething();
});

// Node 1 — consumer: reads slotA, writes slotB
int n1 = builder.AddNode(() => {
    slotB.Value = slotA.Value * 2.0f;
});

// Node 2 — sink: reads slotB
int n2 = builder.AddNode(() => {
    Console.WriteLine(slotB.Value);
});

// 3. Connect nodes (data flow direction).
builder.AddEdge(n0, n1);
builder.AddEdge(n1, n2);

var graph = builder.Build();
```

### Why Closures + Slots?

- **No boxing.** `Slot<T>` is a generic reference type. The value inside it is read and written directly — there are no `object` casts, no heap allocation per cycle.
- **No explicit message passing.** Nodes do not send or receive messages. They simply read and write shared `Slot<T>` fields. The scheduler guarantees a parent has completed its current cycle before any child fires, so reads are always safe.
- **Pure `Action`.** The node's task is just `() => { ... }`. It can capture any number of slots, local variables, or services. The scheduler calls `node.Task?.Invoke()` — nothing more.

### Execution Safety

Safety is not enforced by locks. It is enforced structurally:

```
Parent must complete cycle N+1 before child may begin cycle N+1.
```

The `CanFire` check verifies this before every invocation. As long as a parent's `WorkDone` counter is ahead of a child's by at least 1, the child's read of the shared `Slot<T>` is guaranteed to see the parent's latest written value.

```


With `slack > 0`, the producer is allowed to run ahead by up to `slack` cycles, enabling pipeline parallelism at the cost of the consumer reading an older slot value (a deliberate trade-off).

---

## Distance Constraints & the Slack Parameter

The `CanFire` function enforces two symmetric constraints before a node at index `i` is allowed to fire:

```
For every parent p of i:   p.WorkDone >= i.WorkDone + 1
For every child  c of i:   c.WorkDone >= i.WorkDone - slack
```

**Parent constraint** — a node cannot fire until all of its parents have completed at least one more cycle than it has. This is the fundamental data-dependency guarantee.

**Child constraint** — a node cannot run more than `slack` cycles ahead of any of its children. Setting `slack = 0` enforces strict lock-step execution. Raising slack permits the producer to pipeline work.

The `CanFire` return value is the node's current `WorkDone` snapshot. This snapshot is passed directly into `TryIncrementWorkDone`, which performs a **Compare-And-Swap**:

```csharp
// Only increments if WorkDone is still the value we read — prevents double-firing.
Interlocked.CompareExchange(ref _workDone, expected + 1, expected) == expected
```

If two threads race on the same node, only one CAS wins. The other thread simply skips and retries on its next pass.

---

## Architecture

```
GraphBuilder          Graph (CSR)            Scheduler
─────────────         ────────────           ─────────────────────────────────
AddNode(action) ──▶   nodes[]       ──▶      Parallel.For over node chunks
AddEdge(u, v)   ──▶   edges[]                  └─ while (!allDone)
Build()         ──▶   offsets[]                    └─ CanFire(i, slack, ...)
                       + reverse CSR                   ├─ parent distance check
                         (parentEdges[])               ├─ child distance check
                         (parentOffsets[])             └─ CAS TryIncrementWorkDone
```

### CSR Format

Edges are stored in **Compressed Sparse Row** format — a flat array of destination indices (`edges`) paired with a prefix-sum offset array (`offsets`). Children of node `i` are `edges[offsets[i] .. offsets[i+1]]`. A mirrored reverse CSR is built at runtime for fast parent lookups.

This layout keeps edge data **contiguous in memory**, which is cache-friendly during the tight `CanFire` inner loop.

---

## Getting Started

**Prerequisites:** .NET 9 or later.

```bash
git clone https://github.com/Geezy9/delta-lag.git
cd delta-lag
dotnet build
dotnet run
```

### Minimal Example

```csharp
using deltalag;

var slotX = new Slot<int>();
var slotY = new Slot<int>();

var builder = new GraphBuilder();

int producer = builder.AddNode(() => slotX.Value = 42);
int consumer = builder.AddNode(() => slotY.Value = slotX.Value + 1);

builder.AddEdge(producer, consumer);

var graph  = builder.Build();
var scheduler = new Scheduler();

scheduler.Run(graph, cycles: 100, slack: 0, algo: "Wave");

Console.WriteLine(slotY.Value); // 43
```

---

## API Reference

### `GraphBuilder`

| Method | Description |
|---|---|
| `int AddNode(Action? task)` | Registers a node with an optional closure. Returns the node index. |
| `void AddEdge(int from, int to)` | Adds a directed edge from node `from` to node `to`. |
| `Graph Build()` | Compiles the builder state into a CSR-encoded `Graph`. |

### `Scheduler`

| Method | Description |
|---|---|
| `void Run(Graph graph, int cycles, int slack, string algo)` | Executes the graph for `cycles` iterations using the named algorithm (`"Wave"`). |

### `Slot<T>`

| Member | Description |
|---|---|
| `T Value` | The shared data cell. Read and written directly inside node closures. |

### `Node`

| Member | Description |
|---|---|
| `Action? Task` | The closure invoked each time the node fires. |
| `int WorkDone` | Volatile counter; incremented atomically after each successful fire. |
| `bool TryIncrementWorkDone(int expected)` | CAS increment — returns `true` only if no other thread fired first. |

---

## License

MIT
