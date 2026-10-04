using System.Diagnostics;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Reticula.Domain.Jobs;
using Reticula.Infrastructure.Audit;
using Reticula.Infrastructure.Data;

namespace Reticula.Infrastructure.Jobs;

/// <summary>
/// Hangfire entry point. Loads the job run, dispatches to its handler and records the outcome.
/// No automatic retries: an engineering run that failed is re-run deliberately by the user.
/// </summary>
public sealed class JobExecutor(
    ReticulaDbContext db,
    IEnumerable<IJobHandler> handlers,
    IJobNotifier notifier,
    TimeProvider time,
    AuditActor actor,
    ILogger<JobExecutor> logger)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(Guid jobRunId, CancellationToken ct)
    {
        var run = await db.JobRuns.FirstOrDefaultAsync(r => r.Id == jobRunId, ct);
        if (run is null)
        {
            logger.LogWarning("Job run {JobId} not found; nothing to execute", jobRunId);
            return;
        }
        if (run.Status != JobStatus.Queued)
        {
            logger.LogInformation("Job run {JobId} is {Status}, not queued; skipping", jobRunId, run.Status);
            return;
        }

        // A trace per run: calls to the calc service carry its traceparent, so job and calc logs line up.
        using var activity = new Activity($"job {run.Kind}").Start();
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["JobId"] = run.Id,
            ["JobKind"] = run.Kind,
            ["TraceId"] = activity.TraceId.ToString(),
        });

        // Changes made by the job are audited as the user who started it.
        actor.UserId = run.RequestedBy;
        var handler = handlers.FirstOrDefault(h => h.Kind == run.Kind);
        if (handler is null)
        {
            run.Fail($"No handler for job kind '{run.Kind}'.", time.GetUtcNow());
            await SaveAndPublishAsync(run, CancellationToken.None);
            return;
        }

        run.Start(time.GetUtcNow());
        await SaveAndPublishAsync(run, ct);

        try
        {
            using var payload = JsonDocument.Parse(run.PayloadJson);
            var progress = new Progress(this, run);
            var result = await handler.RunAsync(new JobContext(run.Id, run.RequestedBy, run.ProjectId, payload.RootElement.Clone(), progress), ct);

            // The user may have cancelled while the handler was finishing.
            await db.Entry(run).ReloadAsync(CancellationToken.None);
            run.Succeed(result is null ? null : JsonSerializer.Serialize(result, HangfireJobQueue.Json), time.GetUtcNow());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await db.Entry(run).ReloadAsync(CancellationToken.None);
            run.Cancel(time.GetUtcNow());
        }
        catch (Exception e)
        {
            logger.LogError(e, "Job {JobId} ({JobKind}) failed", run.Id, run.Kind);
            await db.Entry(run).ReloadAsync(CancellationToken.None);
            run.Fail(e.Message, time.GetUtcNow());
        }

        await SaveAndPublishAsync(run, CancellationToken.None);
        logger.LogInformation("Job {JobId} ({JobKind}) finished: {Status}", run.Id, run.Kind, run.Status);
    }

    private async Task SaveAndPublishAsync(JobRun run, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        await notifier.PublishAsync(JobUpdate.From(run), ct);
    }

    private sealed class Progress(JobExecutor executor, JobRun run) : IJobProgress
    {
        public async Task ReportAsync(int pct, string message, CancellationToken ct = default)
        {
            run.Report(pct, message);
            await executor.SaveAndPublishAsync(run, ct);
        }
    }
}
