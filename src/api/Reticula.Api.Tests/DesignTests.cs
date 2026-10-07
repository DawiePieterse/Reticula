using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reticula.Api.Costing;
using Reticula.Api.Design;
using Reticula.Api.Documents;
using Reticula.Api.Field;
using Reticula.Infrastructure.Geo;
using static Reticula.Api.Tests.TestFixtures;

namespace Reticula.Api.Tests;

/// <summary>Design runs (ADR 0011), the connection point (plan 4.1), optimisation and adoption (Phase 5), rates (6.1) and documents (Phase 6).</summary>
[Collection(ApiCollection.Name)]
public class DesignTests(ReticulaApiFactory factory)
{
    private const double Lon = 28.10, Lat = -25.52;

    internal static GeometryInput Point(double dx = 0) => new("Point", JsonSerializer.SerializeToElement(new[] { Lon + dx, Lat }));

    internal static GeometryInput Line(double dy = 0) =>
        new("LineString", JsonSerializer.SerializeToElement(new[] { new[] { Lon, Lat + dy }, new[] { Lon + 0.002, Lat + dy } }));

    internal static async Task<Guid> MarkAsync(HttpClient client, Guid projectId, string kind, GeometryInput geometry)
    {
        var id = Guid.NewGuid();
        var r = await client.PutAsJsonAsync($"/api/projects/{projectId}/candidates/{id}", new CandidateRequest(kind, geometry, null, null, null, null));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return id;
    }

    /// <summary>A project with a transformer, an LV route and an MV route marked in the field.</summary>
    internal static async Task<(HttpClient Client, Guid ProjectId)> MarkedProjectAsync(ReticulaApiFactory factory, string prefix)
    {
        var (client, projectId) = await factory.NewProjectAsync(prefix, Square(28.09, -25.53, 0.03));
        await MarkAsync(client, projectId, "transformer", Point());
        await MarkAsync(client, projectId, "lv_route", Line());
        await MarkAsync(client, projectId, "mv_route", Line(0.001));
        return (client, projectId);
    }

    internal static async Task<JobDtoResult> RunAsync(HttpClient client, Guid projectId, object body)
    {
        var r = await client.PostAsJsonAsync($"/api/projects/{projectId}/design-runs", body);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var job = await WaitFinishedAsync(client, (await r.Content.ReadFromJsonAsync<Jobs.JobDto>())!.Id);
        Assert.True(job.Status == "succeeded", job.Error);
        return new JobDtoResult(job.Result!.Value);
    }

    internal sealed record JobDtoResult(JsonElement Result)
    {
        public Guid RunId => Result.GetProperty("runId").GetGuid();
    }

    internal static async Task SaveConnectionPointAsync(HttpClient client, Guid projectId)
    {
        var r = await client.PutAsJsonAsync($"/api/projects/{projectId}/connection-point",
            new SaveConnectionPointRequest(PointDto.Of(Lon - 0.01, Lat), 11, 2000, 8.5, 6, 4, 6, "Eskom quotation Q-2026-0042", new DateOnly(2026, 9, 1), null));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Connection_point_is_entered_with_its_source_and_says_when_the_studies_can_run()
    {
        var (client, projectId) = await factory.NewProjectAsync("CP", Square());
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/api/projects/{projectId}/connection-point")).StatusCode);

        var unsourced = await client.PutAsJsonAsync($"/api/projects/{projectId}/connection-point",
            new SaveConnectionPointRequest(PointDto.Of(Lon, Lat), 11, 2000, 8.5, 9, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, unsourced.StatusCode);
        var problem = await unsourced.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("source", out _));
        Assert.True(problem.GetProperty("errors").TryGetProperty("fault3PhMinKa", out _));

        var located = await client.PutAsJsonAsync($"/api/projects/{projectId}/connection-point",
            new SaveConnectionPointRequest(PointDto.Of(Lon, Lat), 11, null, null, null, null, null, null, null, null));
        Assert.False((await located.Content.ReadFromJsonAsync<ConnectionPointDto>())!.Complete);

        await SaveConnectionPointAsync(client, projectId);
        var cp = (await client.GetFromJsonAsync<ConnectionPointDto>($"/api/projects/{projectId}/connection-point"))!;
        Assert.True(cp.Saved && cp.Complete);
        Assert.Equal((2000.0, 8.5, "Eskom quotation Q-2026-0042"), (cp.CapacityKva!.Value, cp.Fault3PhKa!.Value, cp.Source));

        var inspector = await factory.InspectorClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PutAsJsonAsync($"/api/projects/{projectId}/connection-point",
            new SaveConnectionPointRequest(PointDto.Of(Lon, Lat), 11, null, null, null, null, null, null, null, null))).StatusCode);
    }

    [Fact]
    public async Task A_run_sends_every_input_stores_the_exact_result_and_turns_placeholders_into_assumptions()
    {
        factory.Calc.DesignFit = false;
        factory.Calc.DesignPlaceholders.Clear();
        factory.Calc.DesignPlaceholders.Add("lv_design.max_drop_pct: PLACEHOLDER value 5");
        var (client, projectId) = await MarkedProjectAsync(factory, "Run");
        await SaveConnectionPointAsync(client, projectId);

        var result = await RunAsync(client, projectId, new { options = new { construction = "compare", objective = "lifetime" } });
        var sent = JsonNode.Parse(factory.Calc.DesignRequests[^1])!;
        Assert.Equal("eskom/0.1.0", sent["rules"]!.GetValue<string>());
        Assert.Equal(["transformer", "lv_route", "mv_route"], sent["candidates"]!.AsArray().Select(c => c!["kind"]!.GetValue<string>()));
        Assert.All(sent["candidates"]!.AsArray(), c => Assert.Equal("field", c!["source"]!.GetValue<string>()));
        Assert.Equal(2000, sent["connection_point"]!["capacity_kva"]!.GetValue<double>());
        // Rates are global: whichever library is active, with its items.
        Assert.NotEmpty(sent["rates"]!["items"]!.AsArray());
        Assert.Equal("compare", sent["options"]!["construction"]!.GetValue<string>());

        var status = (await client.GetFromJsonAsync<DesignRunsStatus>($"/api/projects/{projectId}/design-runs"))!;
        var run = Assert.Single(status.Runs);
        Assert.Equal((1, "run", true, (string?)null, false), (run.Number, run.Mode, run.Current, run.Stale, run.FitToSubmit));
        Assert.Equal(result.RunId, run.Id);
        Assert.Equal(10000.0, run.Capex);
        Assert.Equal(64, run.InputsHash.Length);

        var detail = (await client.GetFromJsonAsync<DesignRunDetail>($"/api/projects/{projectId}/design-runs/{run.Id}"))!;
        Assert.Equal("lifetime", detail.Options.GetProperty("objective").GetString());
        Assert.Equal(4.2, detail.Result!.Value.GetProperty("checks")[0].GetProperty("value").GetDouble());
        var stored = await client.GetStringAsync($"/api/projects/{projectId}/design-runs/{run.Id}/request");
        // The same request, value for value (the database keeps JSON with its keys in its own order).
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(factory.Calc.DesignRequests[^1]), JsonNode.Parse(stored)));

        var assumptions = (await client.GetFromJsonAsync<List<AssumptionDto>>($"/api/projects/{projectId}/assumptions?status=open"))!;
        var placeholder = Assert.Single(assumptions, a => a.Code.StartsWith("rules_placeholder:"));
        Assert.Contains("max_drop_pct", placeholder.Text);

        // A new route makes the run stale and says why; the next run without the placeholder clears it.
        await MarkAsync(client, projectId, "lv_route", Line(0.002));
        status = (await client.GetFromJsonAsync<DesignRunsStatus>($"/api/projects/{projectId}/design-runs"))!;
        Assert.Contains("routes or sites", status.Runs[0].Stale);
        factory.Calc.DesignPlaceholders.Clear();
        await RunAsync(client, projectId, new { });
        status = (await client.GetFromJsonAsync<DesignRunsStatus>($"/api/projects/{projectId}/design-runs"))!;
        Assert.Equal([(2, true, (string?)null), (1, false, status.Runs[1].Stale)], status.Runs.Select(r => (r.Number, r.Current, r.Stale)));
        Assert.Null(status.Runs[0].Stale);
        Assert.DoesNotContain((await client.GetFromJsonAsync<List<AssumptionDto>>($"/api/projects/{projectId}/assumptions?status=open"))!,
            a => a.Code.StartsWith("rules_placeholder:"));
    }

    [Fact]
    public async Task Options_are_checked_before_the_calc_service_sees_them()
    {
        var (client, projectId) = await factory.NewProjectAsync("Opts", Square());
        var r = await client.PostAsJsonAsync($"/api/projects/{projectId}/design-runs",
            new { mode = "run", options = new { construction = "aerial", rules = "eskom/9.9.9", transformer_ratings = new { TX1 = -5 } } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var errors = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        foreach (var key in new[] { "options.construction", "options.rules", "options.transformer_ratings" }) Assert.True(errors.TryGetProperty(key, out _), key);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/projects/{projectId}/design-runs",
            new { mode = "run", optimise = new { siting = true } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/projects/{projectId}/design-runs",
            new { mode = "optimise", optimise = new { max_evaluations = 5000 } })).StatusCode);
        var inspector = await factory.InspectorClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PostAsJsonAsync($"/api/projects/{projectId}/design-runs", new { })).StatusCode);
    }

    [Fact]
    public async Task An_optimisation_keeps_every_option_and_adopting_one_makes_it_the_design_without_recomputing()
    {
        var (client, projectId) = await MarkedProjectAsync(factory, "Opt");
        var result = await RunAsync(client, projectId, new { mode = "optimise", optimise = new { objectives = new[] { "capex", "lifetime" }, siting = false } });
        var sent = JsonNode.Parse(factory.Calc.OptimiseRequests[^1])!;
        Assert.False(sent["options"]!["siting"]!.GetValue<bool>());
        Assert.Equal(3, sent["design"]!["candidates"]!.AsArray().Count);

        var status = (await client.GetFromJsonAsync<DesignRunsStatus>($"/api/projects/{projectId}/design-runs"))!;
        var opt = Assert.Single(status.Runs);
        Assert.Equal(("optimise", false), (opt.Mode, opt.Current));
        Assert.Equal(2, opt.Summary!.Value.GetProperty("comparison").GetArrayLength());
        Assert.Equal(factory.Calc.DesignPlaceholders.Count, opt.Summary!.Value.GetProperty("placeholders").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/api/projects/{projectId}/design")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/projects/{projectId}/design-runs/{opt.Id}/adopt", new AdoptRequest("spare"))).StatusCode);
        var calls = factory.Calc.DesignRequests.Count;
        var adopt = await client.PostAsJsonAsync($"/api/projects/{projectId}/design-runs/{result.RunId}/adopt", new AdoptRequest("lifetime"));
        Assert.Equal(HttpStatusCode.Created, adopt.StatusCode);
        var adopted = (await adopt.Content.ReadFromJsonAsync<DesignRunDto>())!;
        Assert.Equal((2, "adopted", opt.Id, 10500.0), (adopted.Number, adopted.Mode, adopted.ParentRunId!.Value, adopted.Capex!.Value));
        Assert.Equal(calls, factory.Calc.DesignRequests.Count);

        var design = (await client.GetFromJsonAsync<DesignRunDetail>($"/api/projects/{projectId}/design"))!;
        Assert.Equal(adopted.Id, design.Run.Id);
        Assert.True(design.Run.Current);
        Assert.Equal(150, design.Options.GetProperty("transformer_ratings").GetProperty("TX1").GetDouble());
        Assert.Null(design.Run.Stale);
    }

    [Fact]
    public async Task Rates_import_from_csv_take_overrides_and_reach_the_design_request()
    {
        var (client, projectId) = await MarkedProjectAsync(factory, "Rates");
        var lib = (await client.GetFromJsonAsync<RateLibraryDto>("/api/rates"))!;
        Assert.NotEmpty(lib.Source);

        var bad = await client.PutAsJsonAsync("/api/rates/overrides/NOPE", new SaveRateOverrideRequest(1, new DateOnly(2026, 10, 1), "quote"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var form = new MultipartFormDataContent
        {
            { new StringContent("code,description,unit,rate,uncertainty_pct\nM-ABC-70,\"LV ABC 70 mm², 4-core\",m,135.50,10\nM-NEW,Kiosk,each,45000,\n"), "file", "rates.csv" },
            { new StringContent("Supplier list Oct 2026"), "name" },
            { new StringContent("2026-10-01"), "rateDate" },
            { new StringContent("ACME quotation 7781"), "source" },
        };
        var import = await client.PostAsync("/api/rates/import", form);
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var imported = (await import.Content.ReadFromJsonAsync<RateImportDto>())!;
        Assert.Equal((1, 1), (imported.Replaced, imported.Added));
        Assert.Equal("Supplier list Oct 2026", imported.Library.Name);
        Assert.Equal(lib.ItemCount + 1, imported.Library.ItemCount);

        using var broken = new MultipartFormDataContent
        {
            { new StringContent("code,description,unit,rate\nX,thing,m,lots\n"), "file", "rates.csv" },
            { new StringContent("Bad"), "name" }, { new StringContent("2026-10-01"), "rateDate" }, { new StringContent("x"), "source" },
        };
        var rejected = await client.PostAsync("/api/rates/import", broken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("Row 2", await rejected.Content.ReadAsStringAsync());
        Assert.Equal("Supplier list Oct 2026", (await client.GetFromJsonAsync<RateLibraryDto>("/api/rates"))!.Name);

        var o = await client.PutAsJsonAsync("/api/rates/overrides/M-POLE-9", new SaveRateOverrideRequest(3100, new DateOnly(2026, 10, 2), "site quote"));
        Assert.Equal(HttpStatusCode.OK, o.StatusCode);
        await RunAsync(client, projectId, new { });
        var rates = JsonNode.Parse(factory.Calc.DesignRequests[^1])!["rates"]!;
        Assert.Equal("Supplier list Oct 2026 with 1 engineer's rate", rates["name"]!.GetValue<string>());
        var items = rates["items"]!.AsArray().ToDictionary(i => i!["code"]!.GetValue<string>(), i => i!);
        Assert.Equal((135.5, "ACME quotation 7781"), (items["M-ABC-70"]["rate"]!.GetValue<double>(), items["M-ABC-70"]["source"]!.GetValue<string>()));
        Assert.Equal(3100, items["M-POLE-9"]["rate"]!.GetValue<double>());

        // A rate change makes the design stale.
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/rates/overrides/M-POLE-9")).StatusCode);
        var status = (await client.GetFromJsonAsync<DesignRunsStatus>($"/api/projects/{projectId}/design-runs"))!;
        Assert.Contains("rates", status.Runs[0].Stale);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/rates/overrides/M-POLE-9")).StatusCode);
    }

    [Fact]
    public async Task Documents_are_generated_from_the_design_stamped_and_stored_and_go_stale_with_the_inputs()
    {
        factory.Calc.DesignPlaceholders.Clear();
        var (client, projectId) = await MarkedProjectAsync(factory, "Docs");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/projects/{projectId}/documents", new GenerateDocumentsRequest(null, null))).StatusCode);
        var run = await RunAsync(client, projectId, new { });

        var put = await client.PutAsJsonAsync($"/api/projects/{projectId}/report-sections/scope", new { title = "Scope", text = "Electrification of 22 stands." });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        await client.PutAsJsonAsync($"/api/projects/{projectId}/report-sections/draft-only", new { title = "Unapproved", text = "Not printed." });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/projects/{projectId}/report-sections/scope/approve", null)).StatusCode);

        var r = await client.PostAsJsonAsync($"/api/projects/{projectId}/documents", new GenerateDocumentsRequest(null, null));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var job = await WaitFinishedAsync(client, (await r.Content.ReadFromJsonAsync<Jobs.JobDto>())!.Id);
        Assert.True(job.Status == "succeeded", job.Error);

        var docs = (await client.GetFromJsonAsync<DocumentsStatus>($"/api/projects/{projectId}/documents"))!.Documents;
        Assert.Equal(9, docs.Count);
        Assert.All(docs, d => Assert.Equal((run.RunId, 0, "0123456789abcdef", (string?)null), (d.DesignRunId, d.RevisionNumber, d.RulesHash, d.Stale)));
        var drawing = docs.Single(d => d.Kind == "drawing_dxf");
        Assert.EndsWith("-DWG-001", drawing.Number);
        Assert.Equal("test_drawing_dxf_R0.dxf", drawing.FileName);
        Assert.Equal("FAKE drawing_dxf R0", await client.GetStringAsync($"/api/projects/{projectId}/documents/{drawing.Id}/file"));

        var report = JsonNode.Parse(factory.Calc.DocumentBodies["report_pdf"])!;
        Assert.Equal(("draft", "Eskom", false), (report["meta"]!["revision_label"]!.GetValue<string>(), report["meta"]!["authority"]!.GetValue<string>(),
            report["meta"]!["signed_off"]!.GetValue<bool>()));
        Assert.Equal(["Scope"], report["sections"]!.AsArray().Select(s => s!["title"]!.GetValue<string>()));
        Assert.Equal(10000, report["design"]!["cost"]!["capex"]!.GetValue<double>());
        var schedule = JsonNode.Parse(factory.Calc.DocumentBodies["load_schedule_xlsx"])!;
        Assert.NotNull(schedule["rows"]);
        var pack = JsonNode.Parse(factory.Calc.LastPackBody!)!;
        Assert.Equal(8, pack["files"]!.AsArray().Count);
        Assert.Equal("RkFLRSBkcmF3aW5nX2R4ZiBSMA==", pack["files"]![0]!["content_base64"]!.GetValue<string>());

        await MarkAsync(client, projectId, "pole", Point(0.001));
        docs = (await client.GetFromJsonAsync<DocumentsStatus>($"/api/projects/{projectId}/documents"))!.Documents;
        Assert.All(docs, d => Assert.Contains("inputs changed", d.Stale));
        await RunAsync(client, projectId, new { });
        docs = (await client.GetFromJsonAsync<DocumentsStatus>($"/api/projects/{projectId}/documents"))!.Documents;
        Assert.All(docs, d => Assert.Contains("newer design run", d.Stale));
    }
}
