using System.Net.Http.Json;
using Reticula.Api.Auth;
using Reticula.Api.Jobs;
using Reticula.Api.Projects;
using Reticula.Domain.Auth;
using Reticula.Infrastructure.Geo;

namespace Reticula.Api.Tests;

/// <summary>Shapes and setup steps every test class needs: a square polygon, a project, a finished job.</summary>
public static class TestFixtures
{
    public static readonly TimeSpan JobTimeout = TimeSpan.FromSeconds(20);

    public static PolygonDto Square(double lon = 28.10, double lat = -25.52, double d = 0.01) =>
        new("Polygon", [[[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]]);

    /// <summary>An engineer client and a new project on eskom/0.1.0 with the given area.</summary>
    public static async Task<(HttpClient Client, Guid ProjectId)> NewProjectAsync(this ReticulaApiFactory factory, string prefix, PolygonDto area)
    {
        var client = await factory.EngineerClientAsync();
        var r = await client.PostAsJsonAsync("/api/projects", new SaveProjectRequest($"{prefix} {Guid.NewGuid():N}", "eskom/0.1.0", area, null));
        r.EnsureSuccessStatusCode();
        return (client, (await r.Content.ReadFromJsonAsync<ProjectDto>())!.Id);
    }

    /// <summary>A new user with the role, signed in.</summary>
    public static async Task<(HttpClient Client, Guid Id)> NewUserAsync(this ReticulaApiFactory factory, string role, string? registrationNo = null)
    {
        var engineer = await factory.EngineerClientAsync();
        var email = $"{role}-{Guid.NewGuid():N}@test.local";
        var r = await engineer.PostAsJsonAsync("/api/users", new CreateUserRequest(email, $"Test {role}", "user-test-pass-1", role, registrationNo));
        r.EnsureSuccessStatusCode();
        var user = (await r.Content.ReadFromJsonAsync<UserDto>())!;
        return (await factory.ClientAsAsync(email, "user-test-pass-1"), user.Id);
    }

    public static async Task<HttpClient> InspectorClientAsync(this ReticulaApiFactory factory) => (await factory.NewUserAsync(Roles.Inspector)).Client;

    /// <summary>Polls the job until it has succeeded, failed or been cancelled.</summary>
    public static async Task<JobDto> WaitFinishedAsync(HttpClient client, Guid id)
    {
        var deadline = DateTime.UtcNow + JobTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var job = await client.GetFromJsonAsync<JobDto>($"/api/jobs/{id}");
            if (job!.Status is "succeeded" or "failed" or "cancelled") return job;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Job {id} did not finish within {JobTimeout}.");
    }
}
