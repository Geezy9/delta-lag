
v0.1.0
---
Scheduler update changes node tasks from `Action` to `Action<int>`, so each node receives the current work unit when it fires.

It also splits progress tracking into two phases:

- `WorkClaimed` — atomically reserves a work unit so only one thread can execute it
- `WorkDone` — publishes that the work unit fully completed and its outputs are visible

This claim/publish pattern prevents double-firing and reduces race conditions during parallel execution.
