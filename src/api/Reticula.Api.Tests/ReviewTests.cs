using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Reticula.Api.Design;
using Reticula.Api.Documents;
using Reticula.Api.Field;
using Reticula.Api.Jobs;
using Reticula.Api.Layout;
using Reticula.Api.Projects;
using Reticula.Api.Review;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Geo;

namespace Reticula.Api.Tests;

/// <summary>Review and sign-off (plan Phase 7).</summary>
[Collection(ApiCollection.Name)]
public class ReviewTests(ReticulaApiFactory factory)
{
    private const double Lon = 28.10, Lat = -25.52;

    private static PolygonDto Square(double lon, double lat, double d) =>
        new("Polygon", [[[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]]);

    private async Task<(HttpClient Client, Guid ProjectId, Guid Transformer, Guid Building)> SetupAsync()
    {
        factory.Calc.Rules.Add("eskom/0.5.0");
        var client = await factory.EngineerClientAsync();
        var p = await (await client.PostAsJsonAsync("/api/projects", new SaveProjectRequest($"Review {Guid.NewGuid():N}", "eskom/0.5.0", Square(28.09, -25.53, 0.03), null)))
            .Content.ReadFromJsonAsync<ProjectDto>();
        factory.Calc.OnImport = r => new CalcImportResult(r.Kind, "geojson", "WGS84", "test",
            [new CalcFeature("way/1", Square(Lon + 0.0002, Lat + 0.0002, 0.0001), 80, null, null, "way/1", new() { ["building"] = "yes" }, [])], [], []);
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("x"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, "file", "f.geojson" },
            { new StringContent("buildings"), "kind" },
        };
        (await client.PostAsync($"/api/projects/{p!.Id}/imports", form)).EnsureSuccessStatusCode();
        var building = Guid.Parse((await client.GetFromJsonAsync<GeoFeatureCollection<BuildingProps>>($"/api/projects/{p.Id}/buildings"))!.Features[0].Id);
        (await client.PutAsJsonAsync($"/api/projects/{p.Id}/buildings/{building}/load", new LoadRequest("residential", [], null, null, null, null))).EnsureSuccessStatusCode();
        var tx = Guid.NewGuid();
        var point = new GeometryInput("Point", JsonSerializer.SerializeToElement(new[] { Lon, Lat + 0.0001 }));
        (await client.PutAsJsonAsync($"/api/projects/{p.Id}/candidates/{tx}", new CandidateRequest("transformer", point, "North site", null, null, null))).EnsureSuccessStatusCode();
        var line = new GeometryInput("LineString", JsonSerializer.SerializeToElement(new[] { new[] { Lon, Lat + 0.0001 }, new[] { Lon + 0.001, Lat + 0.0001 } }));
        (await client.PutAsJsonAsync($"/api/projects/{p.Id}/candidates/{Guid.NewGuid()}", new CandidateRequest("lv_route", line, null, null, null, null))).EnsureSuccessStatusCode();
        return (client, p.Id, tx, building);
    }

    private static async Task<T> PollAsync<T>(Func<Task<T>> get, Func<T, bool> done)
    {
        for (var i = 0; i < 200; i++)
        {
            var v = await get();
            if (done(v)) return v;
            await Task.Delay(100);
        }
        throw new TimeoutException();
    }

    private static async Task RunLvAsync(HttpClient client, Guid projectId, Guid tx)
    {
        var lv = await (await client.PostAsJsonAsync($"/api/projects/{projectId}/lv-designs", new LvDesignRequest(tx, null, null, null))).Content.ReadFromJsonAsync<StartedDesign>();
        await PollAsync(() => client.GetFromJsonAsync<DesignRunDetail>($"/api/projects/{projectId}/lv-designs/{lv!.Run.Id}")!, d => d!.Run.Status is "succeeded" or "failed");
    }

    private static async Task GenerateAsync(HttpClient client, Guid projectId)
    {
        var started = await (await client.PostAsJsonAsync($"/api/projects/{projectId}/document-sets", new GenerateDocumentsRequest(null))).Content.ReadFromJsonAsync<StartedDocuments>();
        await PollAsync(() => client.GetFromJsonAsync<DocumentSetDto>($"/api/projects/{projectId}/document-sets/{started!.Set.Id}")!, s => s!.Status is "succeeded" or "failed");
    }

    [Fact]
    public async Task Changes_are_audited_with_the_user_and_before_and_after()
    {
        var (client, projectId, tx, building) = await SetupAsync();
        (await client.PutAsJsonAsync($"/api/projects/{projectId}/buildings/{building}/load", new LoadRequest("special", null, "school", null, null, null))).EnsureSuccessStatusCode();
        await RunLvAsync(client, projectId, tx);
        var audit = await client.GetFromJsonAsync<List<AuditRow>>($"/api/projects/{projectId}/audit");
        Assert.Contains(audit!, a => a.EntityType == "Project" && a.Action == "created");
        var load = audit!.First(a => a.EntityType == "LoadPoint" && a.Action == "updated");
        Assert.Equal(["residential", "special"], load.Changes.GetProperty("Kind").EnumerateArray().Select(x => x.GetString()));
        Assert.NotEqual("system", load.User);
        var run = audit!.First(a => a.EntityType == "DesignRun" && a.Action == "updated");  // made by the job, audited as its user
        Assert.Equal(load.User, run.User);
        Assert.False(run.Changes.TryGetProperty("ResultJson", out _));
        var filtered = await client.GetFromJsonAsync<List<AuditRow>>($"/api/projects/{projectId}/audit?entityType=Candidate");
        Assert.All(filtered!, a => Assert.Equal("Candidate", a.EntityType));
        Assert.Equal(2, filtered!.Count);
    }

    [Fact]
    public async Task The_register_syncs_safely_when_asked_for_at_the_same_time()
    {
        var (client, projectId, tx, _) = await SetupAsync();
        await RunLvAsync(client, projectId, tx);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => i % 2 == 0
            ? client.GetAsync($"/api/projects/{projectId}/assumption-register")
            : client.GetAsync($"/api/projects/{projectId}/review")));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var register = await client.GetFromJsonAsync<List<RegisterRow>>($"/api/projects/{projectId}/assumption-register");
        Assert.Single(register!, a => a.Code == "rules:lv_design");
    }

    [Fact]
    public async Task Sign_off_waits_for_the_register_and_current_documents_then_issues_a_locked_revision_that_reproduces()
    {
        var defaultLv = factory.Calc.OnDesignLv;
        try
        {
            var (client, projectId, tx, _) = await SetupAsync();
            factory.Calc.OnDesignLv = _ => JsonDocument.Parse(
                "{\"rules\":\"eskom/0.5.0\",\"rules_hash\":\"abcdef0123456789\",\"options\":[],\"unverified\":[\"lv_design\"]," +
                "\"issues\":[{\"severity\":\"warning\",\"code\":\"route_loop_opened\",\"message\":\"Routes formed loops.\",\"count\":1,\"samples\":[]}]," +
                "\"comparison\":[{\"construction\":\"overhead\",\"passed\":true,\"worst_vdrop_pct\":6.1,\"max_loading_pct\":70,\"transformer_kva\":100,\"cost_total\":1000}]}").RootElement.Clone();
            await RunLvAsync(client, projectId, tx);

            var register = await client.GetFromJsonAsync<List<RegisterRow>>($"/api/projects/{projectId}/assumption-register");
            Assert.Contains(register!, a => a.Code == "rules:lv_design" && a.Status == "open");
            var loop = register!.Single(a => a.Code == "lv:route_loop_opened");
            Assert.StartsWith("North site:", loop.Text);
            var review = await client.GetFromJsonAsync<ReviewDto>($"/api/projects/{projectId}/review");
            Assert.False(review!.Readiness.CanSignOff);
            Assert.Contains(review.Readiness.Blockers, b => b.Contains("assumption"));
            Assert.Contains(review.Readiness.Blockers, b => b.Contains("Generate the documents"));

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/projects/{projectId}/assumptions/{loop.Id}/accept", new AcceptAssumptionRequest(""))).StatusCode);
            foreach (var a in register!.Where(a => a.Status == "open"))
                Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"/api/projects/{projectId}/assumptions/{a.Id}/accept",
                    new AcceptAssumptionRequest("Checked against the standard and the site."))).StatusCode);
            register = await client.GetFromJsonAsync<List<RegisterRow>>($"/api/projects/{projectId}/assumption-register");
            Assert.All(register!, a => Assert.NotEqual("open", a.Status));
            Assert.NotNull(register!.First(a => a.Status == "accepted").ResolvedBy);

            await GenerateAsync(client, projectId);
            review = await client.GetFromJsonAsync<ReviewDto>($"/api/projects/{projectId}/review");
            Assert.True(review!.Readiness.CanSignOff, string.Join("; ", review.Readiness.Blockers));

            var bad = await client.PostAsJsonAsync($"/api/projects/{projectId}/revisions", new SignOffRequest("", "x", false, null));
            var errors = (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
            Assert.True(errors.TryGetProperty("fullName", out _) && errors.TryGetProperty("registrationNumber", out _) && errors.TryGetProperty("declaration", out _));

            var r = await client.PostAsJsonAsync($"/api/projects/{projectId}/revisions", new SignOffRequest("A. Engineer", "20231234", true, "First issue"));
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
            var started = await r.Content.ReadFromJsonAsync<StartedRevision>();
            Assert.Equal(("A", "revision.issue"), (started!.Revision.Label, started.Job.Kind));
            var issued = await PollAsync(() => client.GetFromJsonAsync<RevisionDto>($"/api/projects/{projectId}/revisions/{started.Revision.Id}")!, x => x!.Status is "issued" or "failed");
            Assert.Equal("issued", issued!.Status);
            Assert.Equal(64, issued.SnapshotSha256!.Length);
            var stamp = factory.Calc.LastDocuments!.Value.GetProperty("package").GetProperty("stamp");
            Assert.Equal("A", stamp.GetProperty("revision").GetString());
            Assert.StartsWith("A. Engineer, ECSA registration 20231234", stamp.GetProperty("signed_off").GetString());
            var sets = await client.GetFromJsonAsync<DocumentSetsIndex>($"/api/projects/{projectId}/document-sets");
            var locked = sets!.Sets.Single(s => s.Revision == "A");
            Assert.True(locked.Locked);
            Assert.NotEmpty(locked.Documents);
            Assert.False(sets.Stale);

            // Reproduce: the same calc answer is identical; a changed one is caught with where it differs.
            var job = await (await client.PostAsync($"/api/projects/{projectId}/revisions/{issued.Id}/reproduce", null)).Content.ReadFromJsonAsync<JobDto>();
            Assert.Equal("revision.reproduce", job!.Kind);
            var reproduced = await PollAsync(() => client.GetFromJsonAsync<RevisionDto>($"/api/projects/{projectId}/revisions/{issued.Id}")!, x => x!.ReproducedAt is not null);
            Assert.True(reproduced!.Reproduced);
            var original = factory.Calc.OnDesignLv;
            factory.Calc.OnDesignLv = j => JsonDocument.Parse(original(j).GetRawText().Replace("\"worst_vdrop_pct\":6.1", "\"worst_vdrop_pct\":6.2")).RootElement.Clone();
            var at = reproduced.ReproducedAt;
            await client.PostAsync($"/api/projects/{projectId}/revisions/{issued.Id}/reproduce", null);
            reproduced = await PollAsync(() => client.GetFromJsonAsync<RevisionDto>($"/api/projects/{projectId}/revisions/{issued.Id}")!, x => x!.ReproducedAt != at);
            Assert.False(reproduced!.Reproduced);
            Assert.Contains("$.comparison[0].worst_vdrop_pct: 6.1 → 6.2", reproduced.Reproduction!.Value[0].GetProperty("difference").GetString());

            // Export the whole project.
            var ex = await (await client.PostAsync($"/api/projects/{projectId}/exports", null)).Content.ReadFromJsonAsync<StartedExport>();
            await PollAsync(() => client.GetFromJsonAsync<ReviewDto>($"/api/projects/{projectId}/review")!, x => x!.Exports.Any(e => e.Id == ex!.Export.Id && e.Status is "succeeded" or "failed"));
            var link = await client.GetFromJsonAsync<DocumentLink>($"/api/projects/{projectId}/exports/{ex!.Export.Id}/link");
            using var anon = factory.CreateClient();
            var zipBytes = await anon.GetByteArrayAsync(link!.Url);
            using var zip = new ZipArchive(new MemoryStream(zipBytes));
            var names = zip.Entries.Select(e => e.FullName).ToList();
            foreach (var expected in new[] { "README.txt", "project.json", "field/buildings.geojson", "field/candidates.geojson", "assumptions.json", "audit.json", "revisions.json", "revisions/A/snapshot.json" })
                Assert.Contains(expected, names);
            Assert.Contains(names, n => n.StartsWith("design-runs/", StringComparison.Ordinal) && n.Contains("-lv-"));
            Assert.Contains(names, n => n.StartsWith("documents/A/", StringComparison.Ordinal));
            using var b = new StreamReader(zip.GetEntry("field/buildings.geojson")!.Open());
            var geo = JsonDocument.Parse(await b.ReadToEndAsync()).RootElement;
            Assert.Equal("FeatureCollection", geo.GetProperty("type").GetString());
            Assert.Equal("residential", geo.GetProperty("features")[0].GetProperty("properties").GetProperty("load").GetProperty("kind").GetString());
            Assert.Equal(HttpStatusCode.Forbidden, (await anon.GetAsync($"/api/export-files/{ex.Export.Id}?token=nope")).StatusCode);
        }
        finally
        {
            factory.Calc.OnDesignLv = defaultLv;
        }
    }
}
