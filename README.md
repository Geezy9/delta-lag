# delta-lag

Lock-free, deterministic DAG scheduler for C#.

Status: Experimental. APIs may change.

## Requirements

- .NET 10+

## Quick start

```bash
git clone https://github.com/Geezy9/delta-lag.git
cd delta-lag
dotnet build
dotnet test
```

## Why

delta-lag is built for predictable parallel DAG execution with minimal coordination overhead:

- No global queues
- No work stealing
- Lock-free progression with atomics

Each node tracks its own claimed and completed work. Eligibility is enforced by parent/child distance constraints.

## Important note about `Slack`

If `Slack > 0`, producers may run ahead of consumers by up to `Slack` work units.

- `Slot<T>` is a single unbuffered value and should only be used with `Slack = 0`.
- `Ring<T>` is a typed, non-boxing SPSC ring buffer intended for per-edge data passing when `Slack > 0` (use `Ring<T>.FromCon(config)` to size it to `Slack + 1` rounded up to a power of two).

## Example

```csharp
using deltalag;

var config = new Config
{
    Cycles = 32,
    Slack = 4,
    Threads = 2,
    Strategy = PartitionStrategy.Stride,
    InjectNodes = false,
};

var ring = Ring<int>.FromCon(config);

var builder = new GraphBuilder();

int producer = builder.AddNode(workUnit =>
{
    ring.Write(workUnit, workUnit * 10);
});

int consumer = builder.AddNode(workUnit =>
{
    int value = ring.Read(workUnit);
    Console.WriteLine($"Consumed {value} at work unit {workUnit}");
});

builder.AddEdge(producer, consumer);

var ctx = builder.Build(config);

var scheduler = new Scheduler();
scheduler.Run(ctx, config);
```

## Partition strategies

`PartitionStrategy` controls node-to-thread assignment:

- `Stride`
- `ChunkedLinear`
- `Clustered`
- `Leveled`
- `DepthFirst`

All strategies preserve correctness and deterministic outcomes.

## Documentation

- [API spec](docs/api-spec.md)
- [API quick reference](docs/api.md)

## License

MIT
