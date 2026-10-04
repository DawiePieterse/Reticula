using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Reticula.Api.Auth;
using Reticula.Api.Jobs;
using Reticula.Domain.Auth;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Api.Tests;

[Collection(ApiCollection.Name)]
public class JobTests(ReticulaApiFactory factory)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Diagnostics_job_runs_to_completion()
    {
        var client = await factory.EngineerClientAsync();
        var r = await client.PostAsync("/api/system/diagnostics", null);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var queued = await r.Content.ReadFromJsonAsync<JobDto>();

        var done = await WaitFinishedAsync(client, queued!.Id);
        Assert.Equal("succeeded", done.Status);
        Assert.Equal(100, done.ProgressPct);
        Assert.Equal("eskom/0.1.0", done.Result!.Value.GetProperty("rules")[0].GetProperty("ref").GetString());
    }

    [Fact]
    public async Task Diagnostics_job_fails_when_calc_is_unhealthy()
    {
        var client = await factory.EngineerClientAsync();
        factory.Calc.Healthy = false;
        try
        {
            var queued = await (await client.PostAsync("/api/system/diagnostics", null)).Content.ReadFromJsonAsync<JobDto>();
            var done = await WaitFinishedAsync(client, queued!.Id);
            Assert.Equal("failed", done.Status);
            Assert.Contains("not healthy", done.Error);
        }
        finally
        {
            factory.Calc.Healthy = true;
        }
    }

    [Fact]
    public async Task Handler_exception_marks_job_failed()
    {
        var client = await factory.EngineerClientAsync();
        var run = await EnqueueAsync(FailingJob.JobKind);
        var done = await WaitFinishedAsync(client, run.Id);
        Assert.Equal("failed", done.Status);
        Assert.Equal("boom", done.Error);
    }

    [Fact]
    public async Task Hub_pushes_progress_and_completion()
    {
        var client = await factory.EngineerClientAsync();
        var token = await AccessTokenAsync();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, JobsHub.Path), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var updates = new List<JobUpdate>();
        var finished = new TaskCompletionSource<JobUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JobUpdate>(JobsHub.UpdateMethod, u =>
        {
            lock (updates) updates.Add(u);
            if (u.Status is "succeeded" or "failed" or "cancelled") finished.TrySetResult(u);
        });
        await hub.StartAsync();

        var run = await EnqueueAsync(GatedJob.JobKind);
        await hub.InvokeAsync("Watch", run.Id);
        Assert.True(await factory.Gate.WaitStartedAsync(Timeout), "job never started");
        factory.Gate.Release();

        var last = await finished.Task.WaitAsync(Timeout);
        Assert.Equal("succeeded", last.Status);
        Assert.Equal(100, last.ProgressPct);
        Assert.Equal("succeeded", (await WaitFinishedAsync(client, run.Id)).Status);
    }

    [Fact]
    public async Task Cancelling_a_running_job_stops_it_and_is_not_overwritten()
    {
        var client = await factory.EngineerClientAsync();
        var run = await EnqueueAsync(GatedJob.JobKind);
        Assert.True(await factory.Gate.WaitStartedAsync(Timeout), "job never started");

        var r = await client.PostAsync($"/api/jobs/{run.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Equal("cancelled", (await r.Content.ReadFromJsonAsync<JobDto>())!.Status);

        // Give the executor time to observe cancellation and write its final state.
        await Task.Delay(1000);
        var after = await client.GetFromJsonAsync<JobDto>($"/api/jobs/{run.Id}");
        Assert.Equal("cancelled", after!.Status);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/jobs/{run.Id}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Inspector_can_read_jobs_but_not_start_or_cancel()
    {
        var engineer = await factory.EngineerClientAsync();
        var email = $"inspector-{Guid.NewGuid():N}@test.local";
        (await engineer.PostAsJsonAsync("/api/users", new CreateUserRequest(email, "Inspector", "inspector-pass-1", Roles.Inspector, null))).EnsureSuccessStatusCode();
        var inspector = await factory.ClientAsAsync(email, "inspector-pass-1");

        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PostAsync("/api/system/diagnostics", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await inspector.GetAsync("/api/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PostAsync($"/api/jobs/{Guid.NewGuid()}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Unknown_job_kind_is_rejected()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        await Assert.ThrowsAsync<UnknownJobKindException>(() => queue.EnqueueAsync("nope", null, Guid.NewGuid(), null));
    }

    [Fact]
    public async Task Client_errors_are_accepted_from_signed_in_users_only()
    {
        var body = new { message = "TypeError: x is undefined", stack = "at foo", url = "/projects", appVersion = "0.1.0" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/system/client-errors", body)).StatusCode);
        var client = await factory.EngineerClientAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/system/client-errors", body)).StatusCode);
    }

    private async Task<Domain.Jobs.JobRun> EnqueueAsync(string kind)
    {
        var client = await factory.EngineerClientAsync();
        var me = await client.GetFromJsonAsync<MeResponse>("/api/auth/me");
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(kind, null, me!.Id, null);
    }

    private async Task<string> AccessTokenAsync() =>
        (await ReticulaApiFactory.LoginAsync(factory.CreateClient(), ReticulaApiFactory.EngineerEmail, ReticulaApiFactory.EngineerPassword)).AccessToken;

    private static async Task<JobDto> WaitFinishedAsync(HttpClient client, Guid id)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var job = await client.GetFromJsonAsync<JobDto>($"/api/jobs/{id}");
            if (job!.Status is "succeeded" or "failed" or "cancelled") return job;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Job {id} did not finish within {Timeout}.");
    }
}
