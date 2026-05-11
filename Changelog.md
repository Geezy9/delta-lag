# Changelog

## Latest Branch: Master

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
