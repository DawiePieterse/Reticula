using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Jobs;

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
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), $"reticula-files-{Guid.NewGuid():N}");

    private static string BaseConnection =>
        Environment.GetEnvironmentVariable("RETICULA_TEST_DB") ?? "Host=localhost;Username=reticula;Password=reticula";

    private string ConnectionString => new NpgsqlConnectionStringBuilder(BaseConnection) { Database = _dbName }.ConnectionString;

    public FakeCalc Calc { get; } = new();
    public JobGate Gate { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Bootstrap:EngineerEmail", EngineerEmail);
        builder.UseSetting("Bootstrap:EngineerPassword", EngineerPassword);
        builder.UseSetting("Bootstrap:EngineerName", "Test Engineer");
        builder.UseSetting("Storage:Root", StorageRoot);
        builder.UseSetting("Jobs:PollIntervalMs", "100");
        builder.UseSetting("Jobs:CancellationCheckMs", "200");
        builder.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<ICalcClient>(Calc));
            s.AddSingleton(Gate);
            s.AddJobHandler<GatedJob>();
            s.AddJobHandler<FailingJob>();
        });
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
        if (Directory.Exists(StorageRoot)) Directory.Delete(StorageRoot, recursive: true);
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

    /// <summary>Set by a test to decide what the calc service returns for an uploaded file.</summary>
    public Func<CalcImportRequest, CalcImportResult> OnImport { get; set; } =
        r => new CalcImportResult(r.Kind, "kml", "WGS84", "test", [], [], []);

    public CalcImportRequest? LastImport { get; private set; }
    public IReadOnlyList<AdmdGroupLoad> LastGroupLoads { get; private set; } = [];
    public IReadOnlyList<BuildingPredictionInput> LastPredictionInputs { get; private set; } = [];

    public Task<CalcImportResult> ImportAsync(CalcImportRequest request, CancellationToken ct = default)
    {
        Throw();
        LastImport = request;
        return Task.FromResult(OnImport(request));
    }

    /// <summary>Set by a test to decide what an OpenStreetMap fetch returns.</summary>
    public Func<string, CalcImportResult> OnImportOsm { get; set; } = kind => new CalcImportResult(kind, "overpass", "WGS84", "OSM", [], [], []);

    public Task<CalcImportResult> ImportOsmAsync(string kind, string areaGeoJson, CancellationToken ct = default)
    {
        Throw();
        return Task.FromResult(OnImportOsm(kind));
    }

    /// <summary>Simplified predictor: building=house tag, else residential zoning, else low-confidence "other".</summary>
    public Task<PredictionResult> PredictBuildingTypesAsync(string rulesRef, IReadOnlyList<BuildingPredictionInput> buildings, CancellationToken ct = default)
    {
        Throw();
        LastPredictionInputs = buildings;
        var predictions = buildings.Select(b =>
            b.Tags.GetValueOrDefault("building") == "house" ? new BuildingPrediction(b.Id, "house", 0.9, "osm:building=house", false, [])
            : b.Zoning?.Contains("Residential", StringComparison.OrdinalIgnoreCase) == true ? new BuildingPrediction(b.Id, "house", 0.6, $"zoning:{b.Zoning}", false, [])
            : new BuildingPrediction(b.Id, "other", 0.3, "footprint", true, [])).ToList();
        return Task.FromResult(new PredictionResult("0123456789abcdef", "test", predictions));
    }

    public Task<JsonElement> GetAdmdFormAsync(string rulesRef, CancellationToken ct = default) =>
        Task.FromResult(JsonDocument.Parse("{\"indicators\":[{\"key\":\"dwelling\",\"options\":[\"rdp\",\"brick_small\"]}],\"special_loads\":{\"school\":25}}").RootElement.Clone());

    /// <summary>Simplified estimator: residential 1.5 kVA (category R2), "roof" missing unless given; school 25 kVA, other special 2 kVA.</summary>
    public Task<AdmdEstimate> EstimateAdmdAsync(AdmdEstimateRequest r, CancellationToken ct = default)
    {
        Throw();
        if (r.Observations?.TryGetValue("dwelling", out var d) == true && d.GetString() == "castle")
            throw new CalcRejectedException("dwelling: unknown option 'castle'");
        var special = r.Kind == "special";
        var estimated = special ? (r.SpecialLoad == "school" ? 25 : 2) : 1.5;
        var kva = r.OverrideKva ?? estimated;
        var missing = special || r.Observations?.ContainsKey("roof") == true ? new List<string>() : ["roof"];
        var traced = new TracedValue(kva, "kVA", "test", "test", "test clause", "0123456789abcdef", JsonDocument.Parse("[]").RootElement.Clone());
        var lc = special ? null : new AdmdLoadClass(r.LoadClass ?? "township_area", r.LoadClass is null ? "Township area" : $"Class {r.LoadClass}",
            "nrs034_15y", "NRS 034 test table", estimated, r.LoadClass is null ? "score" : "engineer");
        var result = new AdmdEstimate(r.Kind, missing, special ? null : "low", special ? r.SpecialLoad : lc!.Code, traced, estimated,
            r.OverrideKva is not null, "0123456789abcdef", "", lc);
        return Task.FromResult(result with { Raw = JsonSerializer.Serialize(new { kind = r.Kind, admd_kva = kva }) });
    }

    public Task<AdmdGroup> GroupAdmdAsync(string rulesRef, IReadOnlyList<AdmdGroupLoad> loads, CancellationToken ct = default)
    {
        LastGroupLoads = loads;
        var res = loads.Where(l => l.Kind == "residential").ToList();
        var factor = res.Count == 0 ? (double?)null : 1 + 1.5 / res.Count;
        var resKva = res.Sum(l => l.Kva) * (factor ?? 0);
        var special = loads.Where(l => l.Kind == "special").Sum(l => l.Kva);
        TracedValue T(double v) => new(v, "kVA", "test", "S = sum", "test clause", "0123456789abcdef", JsonDocument.Parse("[]").RootElement.Clone());
        return Task.FromResult(new AdmdGroup(res.Count, loads.Count - res.Count, factor is null ? null : T(factor.Value), T(resKva), special, T(resKva + special), "0123456789abcdef"));
    }

    /// <summary>Set by a test to decide what the calc service returns for a map extract.</summary>
    public Func<double[], MapExtract> OnExtract { get; set; } = b => new MapExtract([.. "PMTiles"u8, 3, .. new byte[120]], 42, 15, "test-planet.pmtiles");

    public double[]? LastExtractBox { get; private set; }

    public Task<MapExtract> ExtractMapAsync(double minLon, double minLat, double maxLon, double maxLat, CancellationToken ct = default)
    {
        Throw();
        LastExtractBox = [minLon, minLat, maxLon, maxLat];
        return Task.FromResult(OnExtract(LastExtractBox));
    }

    /// <summary>Set by a test to decide what the LV network build returns. By default each LV route is one branch and each source one node.</summary>
    public Func<string, IReadOnlyList<LvCandidate>, CalcLvNetwork> OnBuildLvNetwork { get; set; } = DefaultLvNetwork;

    public IReadOnlyList<LvCandidate> LastLvCandidates { get; private set; } = [];

    public Task<CalcLvNetwork> BuildLvNetworkAsync(string rulesRef, IReadOnlyList<LvCandidate> candidates, CancellationToken ct = default)
    {
        Throw();
        LastLvCandidates = candidates;
        return Task.FromResult(OnBuildLvNetwork(rulesRef, candidates));
    }

    public static CalcLvNetwork DefaultLvNetwork(string rulesRef, IReadOnlyList<LvCandidate> candidates)
    {
        var nodes = new List<CalcLvNode>();
        var branches = new List<CalcLvBranch>();
        foreach (var c in candidates)
        {
            var coords = c.Geometry.GetProperty("coordinates");
            if (c.Kind == "lv_route")
            {
                var line = coords.Deserialize<double[][]>()!;
                nodes.Add(new CalcLvNode($"N{nodes.Count + 1}", "end", line[0], null, null, null, null));
                nodes.Add(new CalcLvNode($"N{nodes.Count + 1}", "end", line[^1], null, null, null, null));
                branches.Add(new CalcLvBranch($"B{branches.Count + 1}", "route", nodes[^2].Id, nodes[^1].Id, line, 100, c.Id, null));
            }
            else if (c.Kind is "transformer" or "minisub")
            {
                nodes.Add(new CalcLvNode($"N{nodes.Count + 1}", "source", coords.Deserialize<double[]>()!, "TX1", c.Id, null, 0));
            }
        }
        var routes = branches.Count;
        return new CalcLvNetwork(rulesRef, "0123456789abcdef", "test tolerances", nodes, branches,
            [new LvFeeder("TX1-F1", "N1", routes, 100.0 * routes, routes, 100)],
            [new LvIssue("warning", "near_miss", "Route ends stop short.", 1, [], [[28.1, -25.5]])],
            new LvSummary(routes, 1, 1, 0, 0, 1, nodes.Count, routes, 100.0 * routes, 0));
    }

    private void Throw()
    {
        if (Unreachable) throw new CalcUnavailableException("Calc service unreachable.");
    }
}

/// <summary>Lets a test hold a running job open until it releases the gate.</summary>
public sealed class JobGate
{
    private readonly SemaphoreSlim _release = new(0);
    private readonly SemaphoreSlim _started = new(0);

    public void Release() => _release.Release();
    public Task<bool> WaitStartedAsync(TimeSpan timeout) => _started.WaitAsync(timeout);

    internal async Task EnterAsync(CancellationToken ct)
    {
        _started.Release();
        await _release.WaitAsync(ct);
    }
}

public sealed class GatedJob(JobGate gate) : IJobHandler
{
    public const string JobKind = "test.gated";
    public string Kind => JobKind;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        await context.Progress.ReportAsync(50, "Waiting at gate", ct);
        await gate.EnterAsync(ct);
        return new { ok = true };
    }
}

public sealed class FailingJob : IJobHandler
{
    public const string JobKind = "test.failing";
    public string Kind => JobKind;

    public Task<object?> RunAsync(JobContext context, CancellationToken ct) => throw new InvalidOperationException("boom");
}

/// <summary>
/// All API tests share one host and database. Hangfire keeps process-wide static state (log provider,
/// global configuration), so several hosts in one process interfere; production runs one host.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ReticulaApiFactory>
{
    public const string Name = "api";
}
