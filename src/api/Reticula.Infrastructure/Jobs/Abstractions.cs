using System.Text.Json;
using Reticula.Domain.Jobs;

namespace Reticula.Infrastructure.Jobs;

/// <summary>Runs one kind of background job. Register implementations in DI; the executor picks by <see cref="Kind"/>.</summary>
public interface IJobHandler
{
    string Kind { get; }

    /// <returns>A result object serialised to JSON on the job run, or null.</returns>
    Task<object?> RunAsync(JobContext context, CancellationToken ct);
}

public sealed record JobContext(Guid JobId, Guid RequestedBy, Guid? ProjectId, JsonElement Payload, IJobProgress Progress);

public interface IJobProgress
{
    Task ReportAsync(int pct, string message, CancellationToken ct = default);
}

/// <summary>Pushes job state changes to watchers. The API implements this with SignalR.</summary>
public interface IJobNotifier
{
    Task PublishAsync(JobUpdate update, CancellationToken ct = default);
}

public sealed record JobUpdate(
    Guid Id, string Kind, string Status, int ProgressPct, string? Message, string? Error,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt)
{
    public static JobUpdate From(JobRun r) => new(
        r.Id, r.Kind, r.Status.ToString().ToLowerInvariant(), r.ProgressPct, r.Message, r.Error, r.StartedAt, r.FinishedAt);
}

public interface IJobQueue
{
    /// <exception cref="UnknownJobKindException">No handler is registered for <paramref name="kind"/>.</exception>
    Task<JobRun> EnqueueAsync(string kind, object? payload, Guid requestedBy, Guid? projectId, CancellationToken ct = default);

    /// <returns>False when the job does not exist or has already finished.</returns>
    Task<bool> CancelAsync(Guid jobId, CancellationToken ct = default);
}

public sealed class UnknownJobKindException(string kind) : Exception($"No job handler is registered for kind '{kind}'.");

/// <summary>Used when no real-time channel is configured.</summary>
public sealed class NullJobNotifier : IJobNotifier
{
    public Task PublishAsync(JobUpdate update, CancellationToken ct = default) => Task.CompletedTask;
}
