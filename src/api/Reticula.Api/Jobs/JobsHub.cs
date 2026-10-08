using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Reticula.Domain.Auth;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Api.Jobs;

/// <summary>Clients call Watch(jobId) and then receive "jobUpdate" messages for that job.</summary>
[Authorize(Policy = Policies.FieldUser)]
public sealed class JobsHub : Hub
{
    public const string Path = "/hubs/jobs";
    public const string UpdateMethod = "jobUpdate";

    public static string Group(Guid jobId) => $"job:{jobId}";

    public Task Watch(Guid jobId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(jobId));

    public Task Unwatch(Guid jobId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(jobId));
}

public sealed class SignalRJobNotifier(IHubContext<JobsHub> hub) : IJobNotifier
{
    public Task PublishAsync(JobUpdate update, CancellationToken ct = default) =>
        hub.Clients.Group(JobsHub.Group(update.Id)).SendAsync(JobsHub.UpdateMethod, update, ct);
}
