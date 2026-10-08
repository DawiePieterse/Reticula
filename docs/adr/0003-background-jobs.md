# ADR 0003 – Background jobs and progress

Date: 2026-10-03 · Status: accepted

## Decision

A. Hangfire runs background jobs, with storage in the same Postgres database under the `hangfire` schema.
B. Each job also has an application row in `job_runs` holding kind, status, progress, message, result, error and who asked for it. The API and UI read this table, never Hangfire's tables.
C. Handlers implement `IJobHandler` and report progress through `IJobProgress`. The executor records the outcome, and each progress step is saved and pushed.
D. Progress is pushed over SignalR at `/hubs/jobs`. A client calls `Watch(jobId)` and then receives `jobUpdate` messages. Pushed updates are light. The client fetches the full job, including its result, once the job finishes.
E. Jobs never retry automatically. A failed engineering run is re-run deliberately.
F. Cancelling deletes the Hangfire job, which cancels the handler's token. The run is marked cancelled at once and the executor never overwrites that.
G. Each job run starts its own trace, so its calls to the calc service carry one `traceparent` and the logs line up.

## Hangfire's static state

Hangfire keeps process-wide statics: `JobStorage.Current`, `JobActivator.Current` and its log provider. With them, two hosts in one process run each other's jobs or log to a disposed logger.

- Storage and the job activator are registered per DI container instead of through the statics.
- All API tests share one host through a single xUnit collection. Production runs one host per process, so the log provider static is safe there.

## Consequences

- No Hangfire dashboard. Browser navigation cannot send the bearer token, and `job_runs` with the jobs API covers what the engineer needs.
- Design runs in Phase 2 will be job handlers that call the calc service and report its progress.
