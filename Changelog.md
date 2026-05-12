# Changelog

## Latest Branch: Add-Configurable-Partition-Strategy

### Feature: Configurable Partition Strategy

- Added `PartitionStrategy` enum with two values: `Stride` and `ChunkedLinear`
- `Scheduler.Run()` now accepts a `PartitionStrategy` parameter to control how nodes are distributed across threads
  - `Stride` — each thread processes every N-th node (interleaved); generally lower p95 latency on linear pipelines
  - `ChunkedLinear` — each thread processes a contiguous block of nodes; favours spatial locality
- Both strategies produce identical, deterministic output
- Updated README with strategy documentation, a comparison table, and usage example

---

## Latest Branch: Master
### Refactor: Align Node Class to Cache Line
- Updated `Node` class to be cache aligned 3 Cache lines X (64 bytes) to reduce false sharing and improve performance under contention
- 

### Refactor: Pre-allocate Scheduling Structures in `Build()`

- `GraphBuilder.Build()` now returns a pre-allocated `WorkContext`
- `Scheduler.Run()` consumes this context directly, eliminating setup allocations at scheduling time
- Removed legacy `Graph`-based `Run()` method
- Updated README and API docs to reflect the new workflow
- Bumped .NET requirement to 10+

> This improves scheduling performance and predictability.

---

## v0.1.0

### Scheduler Update: Node Tasks and Progress Tracking

Node tasks have changed from `Action` to `Action<int>`, so each node now receives the current work unit when it fires.

Progress tracking has been split into two distinct phases:

| Phase | Description |
|---|---|
| `WorkClaimed` | Atomically reserves a work unit so only one thread can execute it |
| `WorkDone` | Publishes that the work unit fully completed and its outputs are visible |

> This claim/publish pattern prevents double-firing and reduces race conditions during parallel execution.
