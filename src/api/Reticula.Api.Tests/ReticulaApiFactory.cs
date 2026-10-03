using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Reticula.Infrastructure.Calc;

namespace Reticula.Api.Tests;

/// <summary>
/// Hosts the API against a fresh Postgres database per test class.
/// Base connection from RETICULA_TEST_DB (default: local reticula role).
/// </summary>
public sealed class ReticulaApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string EngineerEmail = "engineer@test.local";
    public const string EngineerPassword = "engineer-test-pass";

    private readonly string _dbName = $"reticula_test_{Guid.NewGuid():N}";

    private static string BaseConnection =>
        Environment.GetEnvironmentVariable("RETICULA_TEST_DB") ?? "Host=localhost;Username=reticula;Password=reticula";

    private string ConnectionString => new NpgsqlConnectionStringBuilder(BaseConnection) { Database = _dbName }.ConnectionString;

    public FakeCalc Calc { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Bootstrap:EngineerEmail", EngineerEmail);
        builder.UseSetting("Bootstrap:EngineerPassword", EngineerPassword);
        builder.UseSetting("Bootstrap:EngineerName", "Test Engineer");
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<ICalcClient>(Calc)));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(BaseConnection) { Database = "postgres" }.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<HttpClient> ClientAsAsync(string email, string password)
    {
        var client = CreateClient();
        var token = await LoginAsync(client, email, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }

    public Task<HttpClient> EngineerClientAsync() => ClientAsAsync(EngineerEmail, EngineerPassword);

    public static async Task<AccessTokenResponse> LoginAsync(HttpClient client, string email, string password)
    {
        var r = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<AccessTokenResponse>())!;
    }
}

public sealed class FakeCalc : ICalcClient
{
    public bool Healthy { get; set; } = true;
    public bool Unreachable { get; set; }
    public HashSet<string> Rules { get; } = ["eskom/0.1.0"];

    public Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(Healthy && !Unreachable);

    public Task<IReadOnlyList<string>> ListRulesAsync(CancellationToken ct = default)
    {
        Throw();
        return Task.FromResult<IReadOnlyList<string>>([.. Rules]);
    }

    public Task<RulesInfo?> GetRulesInfoAsync(string rulesRef, CancellationToken ct = default)
    {
        Throw();
        return Task.FromResult(Rules.Contains(rulesRef) ? new RulesInfo(rulesRef, "0123456789abcdef", "2026-10-03") : null);
    }

    private void Throw()
    {
        if (Unreachable) throw new CalcUnavailableException("Calc service unreachable.");
    }
}
