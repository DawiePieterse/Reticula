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
using Reticula.Infrastructure.Maps;

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
    public FakeTileServer Tiles { get; } = new();
    public FakeOverpass Overpass { get; } = new();
    public FakeAssistantModel Assistant { get; } = new();
    public FakeGoogleTiles GoogleTiles { get; } = new();
    /// <summary>Off by default; imagery tests switch it on and back off.</summary>
    public Reticula.Infrastructure.Layout.GoogleImageryOptions GoogleOptions { get; } = new() { Zoom = 19, MaxTiles = 400 };
    /// <summary>Off by default (plan 8.5); assistant tests switch it on and back off.</summary>
    public Reticula.Infrastructure.Assistant.AssistantOptions AssistantOptions { get; } = new() { Model = "test-model" };

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
        builder.UseSetting("Tiles:SourceUrl", "http://tiles.test/{z}/{x}/{y}.png?key=secret");
        builder.UseSetting("Tiles:MinZoom", "12");
        builder.UseSetting("Tiles:MaxZoom", "16");
        builder.ConfigureServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<ICalcClient>(Calc));
            s.AddSingleton(Gate);
            s.AddJobHandler<GatedJob>();
            s.AddJobHandler<FailingJob>();
            s.AddHttpClient(TilePackJob.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Tiles);
            s.AddHttpClient<Reticula.Infrastructure.Layout.OverpassClient>().ConfigurePrimaryHttpMessageHandler(() => Overpass);
            s.Replace(ServiceDescriptor.Singleton(AssistantOptions));
            s.Replace(ServiceDescriptor.Singleton(GoogleOptions));
            s.AddHttpClient<Reticula.Infrastructure.Layout.GoogleTilesClient>().ConfigurePrimaryHttpMessageHandler(() => GoogleTiles);
            s.Replace(ServiceDescriptor.Transient<Reticula.Infrastructure.Assistant.IAssistantModel>(_ => Assistant));
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

    /// <summary>Simplified predictor: building=house tag, else residential zoning, else low-confidence "other".</summary>
    public Task<PredictionResult> PredictBuildingTypesAsync(string rulesRef, IReadOnlyList<BuildingPredictionInput> buildings, CancellationToken ct = default)
    {
        Throw();
        LastPredictionInputs = buildings;
        var predictions = buildings.Select(b =>
            b.ExtraSignals is { Count: > 0 } extra ? new BuildingPrediction(b.Id, extra[0].Type, extra[0].Confidence, extra[0].Source, extra[0].Confidence < 0.6, [.. extra])
            : b.Tags.GetValueOrDefault("building") == "house" ? new BuildingPrediction(b.Id, "house", 0.9, "osm:building=house", false, [])
            : b.Zoning?.Contains("Residential", StringComparison.OrdinalIgnoreCase) == true ? new BuildingPrediction(b.Id, "house", 0.6, $"zoning:{b.Zoning}", false, [])
            : new BuildingPrediction(b.Id, "other", 0.3, "footprint", true, [])).ToList();
        return Task.FromResult(new PredictionResult("0123456789abcdef", "test", predictions));
    }

    /// <summary>The last LV design request, as the calc service would receive it (snake_case JSON).</summary>
    public JsonElement? LastLvDesign { get; private set; }

    public Func<JsonElement, JsonElement> OnDesignLv { get; set; } = _ => JsonDocument.Parse(
        "{\"rules\":\"eskom/0.3.0\",\"rules_hash\":\"abcdef0123456789\",\"issues\":[],\"options\":[],\"unverified\":[\"lv_design\"]," +
        "\"comparison\":[{\"construction\":\"overhead\",\"passed\":true,\"worst_vdrop_pct\":6.1,\"max_loading_pct\":70,\"transformer_kva\":100,\"cost_total\":1000}]}").RootElement.Clone();

    public Task<JsonElement> DesignLvAsync(object request, CancellationToken ct = default)
    {
        Throw();
        var json = JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        LastLvDesign = json;
        return Task.FromResult(OnDesignLv(json));
    }

    public JsonElement? LastMvDesign { get; private set; }

    public Task<JsonElement> DesignMvAsync(object request, CancellationToken ct = default)
    {
        Throw();
        LastMvDesign = JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        return Task.FromResult(JsonDocument.Parse(
            "{\"rules\":\"eskom/0.4.0\",\"rules_hash\":\"0011223344556677\",\"passed\":true,\"issues\":[],\"checks\":[{\"passed\":true}],\"cost_total\":5000,\"unverified\":[]," +
            "\"sites\":[{\"placement\":{\"site_id\":\"T1\",\"rating_kva\":100,\"z_pct\":4,\"x_r\":2,\"design_kva\":80},\"tap_pct\":2.5}," +
            "{\"placement\":{\"site_id\":\"T2\",\"rating_kva\":null,\"z_pct\":null,\"x_r\":null,\"design_kva\":900},\"tap_pct\":null}]," +
            "\"mv_network\":{\"supply_id\":\"SUPPLY\",\"nodes\":[],\"branches\":[]}}").RootElement.Clone());
    }

    public Task<IReadOnlyList<string>> ListRatesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(["indicative/2026-10"]);

    public Task<JsonElement?> GetRatesAsync(string rateRef, CancellationToken ct = default) => Task.FromResult<JsonElement?>(rateRef != "indicative/2026-10" ? null
        : JsonDocument.Parse("{\"name\":\"indicative\",\"rate_date\":\"2026-10-01\",\"currency\":\"ZAR\",\"conductor_per_m\":{\"ABC-35\":165,\"ABC-50\":195}," +
            "\"pole_each\":{\"WP-9-160\":{\"assembly\":\"OH-POLE-9-160\"}},\"stay_each\":1900,\"materials\":{\"POLE-9-160\":{\"description\":\"Wood pole 9 m\",\"unit\":\"each\",\"rate\":3300}," +
            "\"LAB-POLE-9\":{\"description\":\"Labour\",\"unit\":\"each\",\"rate\":1900}},\"assemblies\":{\"OH-POLE-9-160\":{\"description\":\"LV pole 9 m\"," +
            "\"components\":[{\"material\":\"POLE-9-160\",\"qty\":1},{\"material\":\"LAB-POLE-9\",\"qty\":1}]}}}").RootElement.Clone());

    public JsonElement? LastDocuments { get; private set; }

    public Task<JsonElement> RenderDocumentsAsync(object request, CancellationToken ct = default)
    {
        Throw();
        LastDocuments = JsonSerializer.SerializeToElement(request);
        var b64 = Convert.ToBase64String("%PDF-1.4 test"u8.ToArray());
        return Task.FromResult(JsonDocument.Parse(
            "{\"files\":[{\"kind\":\"report\",\"name\":\"p-d1-report.pdf\",\"title\":\"Design report\",\"content_type\":\"application/pdf\",\"size\":13,\"sha256\":\"" + new string('a', 64) + "\",\"data_b64\":\"" + b64 + "\"}," +
            "{\"kind\":\"loads_xlsx\",\"name\":\"p-d1-load-schedule.xlsx\",\"title\":\"Load schedule (Excel)\",\"content_type\":\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet\",\"size\":13,\"sha256\":\"" + new string('b', 64) + "\",\"data_b64\":\"" + b64 + "\"}]," +
            "\"checklist\":[{\"id\":\"C01\",\"text\":\"Connection point\",\"status\":\"not met\",\"detail\":\"\"}],\"warnings\":[\"unverified\"]}").RootElement.Clone());
    }

    public JsonElement? LastRooftop { get; private set; }
    public long LastRooftopBytes { get; private set; }
    public bool RooftopModelUsed { get; set; } = true;

    /// <summary>Signals every unconfirmed building as a shop when the model is "used".</summary>
    public async Task<JsonElement> ClassifyRooftopsAsync(object request, Stream imagery, string fileName, CancellationToken ct = default)
    {
        Throw();
        LastRooftop = JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        using var ms = new MemoryStream();
        await imagery.CopyToAsync(ms, ct);
        LastRooftopBytes = ms.Length;
        var label = LastRooftop.Value.GetProperty("imagery_label").GetString();
        var signals = RooftopModelUsed
            ? LastRooftop.Value.GetProperty("buildings").EnumerateArray().Where(b => b.GetProperty("confirmed_type").ValueKind == JsonValueKind.Null)
                .Select(b => new { id = b.GetProperty("id").GetString(), signal = new { source = $"rooftop:{label}", type = "shop", confidence = 0.8 } }).ToArray()
            : [];
        return JsonSerializer.SerializeToElement(new
        {
            rules_hash = "6666666666666666", imagery_label = label, width = 100, height = 100, gsd_m = 0.3, outside_imagery = 0, method = "test",
            model = new { used = RooftopModelUsed, reason = RooftopModelUsed ? "cross-validated accuracy 90%" : "1 confirmed buildings inside the imagery; at least 30 are needed",
                trained_on = 1, types = new { house = 1 }, left_out_types = Array.Empty<string>(), accuracy = RooftopModelUsed ? 0.9 : (double?)null, min_accuracy = 0.75 },
            signals,
        });
    }

    public JsonElement? LastOptionSearch { get; private set; }

    public Task<JsonElement> OptimiseLvAsync(object request, CancellationToken ct = default)
    {
        Throw();
        LastOptionSearch = JsonSerializer.SerializeToElement(request, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        return Task.FromResult(JsonDocument.Parse(
            "{\"rules\":\"eskom/0.5.0\",\"rules_hash\":\"5555555555555555\",\"evaluations\":42,\"options\":[" +
            "{\"objective\":\"capex\",\"design\":{\"construction\":\"overhead\"},\"option\":{\"passed\":true,\"cost\":{\"total\":1000}},\"lifetime\":{\"total\":{\"value\":1500}}}," +
            "{\"objective\":\"lifetime\",\"design\":{\"construction\":\"underground\"},\"option\":{\"passed\":true,\"cost\":{\"total\":1200}},\"lifetime\":{\"total\":{\"value\":1400}}}]}").RootElement.Clone());
    }

    public JsonElement? LastBulkStudy { get; private set; }

    public Task<JsonElement> StudyBulkAsync(object request, CancellationToken ct = default)
    {
        Throw();
        LastBulkStudy = JsonSerializer.SerializeToElement(request);
        return Task.FromResult(JsonDocument.Parse(
            "{\"rules\":\"eskom/0.4.0\",\"rules_hash\":\"8899aabbccddeeff\",\"passed\":false,\"supply_kva\":82.5,\"notified_max_demand_kva\":100," +
            "\"checks\":[{\"passed\":true},{\"passed\":false}],\"buses\":[],\"lines\":[],\"transformers\":[]}").RootElement.Clone());
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

/// <summary>Answers tile requests with a tiny PNG per tile; tiles in <see cref="Missing"/> return 404.</summary>
public sealed class FakeTileServer : HttpMessageHandler
{
    private int _requests;
    public int Requests => _requests;
    public Func<Uri, bool> Missing { get; set; } = _ => false;
    public bool Down { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        if (Down) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        if (Missing(request.RequestUri!)) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        var path = request.RequestUri!.AbsolutePath; // /z/x/y.png
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. System.Text.Encoding.ASCII.GetBytes(path)];
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
    }

    // The client factory recycles handlers; this one lives as long as the test host.
    protected override void Dispose(bool disposing) { }
}

/// <summary>Stands in for the Overpass API: records the query and answers with a fixed body.</summary>
public sealed class FakeOverpass : HttpMessageHandler
{
    public string? LastQuery { get; private set; }
    public System.Net.HttpStatusCode Status { get; set; } = System.Net.HttpStatusCode.OK;
    public string Body { get; set; } = "{\"elements\":[]}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var form = await request.Content!.ReadAsStringAsync(ct);
        LastQuery = Uri.UnescapeDataString(form.Replace('+', ' ')).Split("data=", 2)[^1];
        return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
    }

    protected override void Dispose(bool disposing) { }
}


/// <summary>A scripted model: each call answers with the next step, given the transcript so far.</summary>
public sealed class FakeAssistantModel : Reticula.Infrastructure.Assistant.IAssistantModel
{
    public Queue<Func<Reticula.Infrastructure.Assistant.ModelRequest, Reticula.Infrastructure.Assistant.ModelReply>> Script { get; } = new();
    public List<Reticula.Infrastructure.Assistant.ModelRequest> Requests { get; } = [];

    public Task<Reticula.Infrastructure.Assistant.ModelReply> SendAsync(Reticula.Infrastructure.Assistant.ModelRequest request, CancellationToken ct)
    {
        Requests.Add(new(request.System, request.Tools, (System.Text.Json.Nodes.JsonArray)request.Messages.DeepClone()));
        var step = Script.Count > 0 ? Script.Dequeue() : _ => Text("Done.");
        return Task.FromResult(step(request));
    }

    public static Reticula.Infrastructure.Assistant.ModelReply Text(string text) =>
        new(new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["type"] = "text", ["text"] = text }), "end_turn");

    public static Reticula.Infrastructure.Assistant.ModelReply Tools(params (string Name, object Input)[] calls) =>
        new(new System.Text.Json.Nodes.JsonArray([.. calls.Select((c, i) => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "tool_use", ["id"] = $"toolu_{Guid.NewGuid():N}", ["name"] = c.Name, ["input"] = System.Text.Json.JsonSerializer.SerializeToNode(c.Input),
        })]), "tool_use");

    /// <summary>The tool results the model was given in its last request.</summary>
    public static IEnumerable<System.Text.Json.Nodes.JsonObject> LastToolResults(Reticula.Infrastructure.Assistant.ModelRequest r) =>
        (r.Messages.Last()!["content"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>().Where(b => b["type"]?.GetValue<string>() == "tool_result");
}


/// <summary>Stands in for Google's Map Tiles API: a session, then PNG tiles.</summary>
public sealed class FakeGoogleTiles : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request.RequestUri!);
        if (request.RequestUri!.AbsolutePath.EndsWith("/createSession", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"session\":\"sess-1\"}", System.Text.Encoding.UTF8, "application/json") });
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
    }

    protected override void Dispose(bool disposing) { }
}
