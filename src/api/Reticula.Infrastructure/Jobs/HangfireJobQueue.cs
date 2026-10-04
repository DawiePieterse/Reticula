using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Jobs;
using Reticula.Infrastructure.Data;

namespace Reticula.Infrastructure.Jobs;

public sealed class HangfireJobQueue(
    ReticulaDbContext db,
    IBackgroundJobClient jobs,
    IEnumerable<IJobHandler> handlers,
    IJobNotifier notifier,
    TimeProvider time) : IJobQueue
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<JobRun> EnqueueAsync(string kind, object? payload, Guid requestedBy, Guid? projectId, CancellationToken ct = default)
    {
        if (!handlers.Any(h => h.Kind == kind)) throw new UnknownJobKindException(kind);

        var run = new JobRun(Guid.CreateVersion7(), kind, JsonSerializer.Serialize(payload ?? new { }, Json), requestedBy, projectId, time.GetUtcNow());
        db.JobRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var backgroundJobId = jobs.Enqueue<JobExecutor>(x => x.ExecuteAsync(run.Id, CancellationToken.None));
        // Targeted update: the executor may already be writing status on another connection.
        await db.JobRuns.Where(r => r.Id == run.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.BackgroundJobId, backgroundJobId), ct);

        await notifier.PublishAsync(JobUpdate.From(run), ct);
        return run;
    }

    public async Task<bool> CancelAsync(Guid jobId, CancellationToken ct = default)
    {
        var run = await db.JobRuns.FirstOrDefaultAsync(r => r.Id == jobId, ct);
        if (run is null || run.IsFinished) return false;

        // Deleting the Hangfire job triggers the CancellationToken passed to a running executor.
        if (run.BackgroundJobId is not null) jobs.Delete(run.BackgroundJobId);
        run.Cancel(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await notifier.PublishAsync(JobUpdate.From(run), ct);
        return true;
    }
}
