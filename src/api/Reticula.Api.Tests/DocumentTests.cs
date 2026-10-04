using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Reticula.Api.Costs;
using Reticula.Api.Design;
using Reticula.Api.Documents;
using Reticula.Api.Field;
using Reticula.Api.Layout;
using Reticula.Api.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Geo;

namespace Reticula.Api.Tests;

/// <summary>Rate lists (plan 6.1) and design documents (plan 6.2–6.8).</summary>
[Collection(ApiCollection.Name)]
public class DocumentTests(ReticulaApiFactory factory)
{
    private const double Lon = 28.10, Lat = -25.52;

    private static PolygonDto Square(double lon, double lat, double d) =>
        new("Polygon", [[[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]]);

    private static GeometryInput Point(double lon, double lat) => new("Point", JsonSerializer.SerializeToElement(new[] { lon, lat }));

    private async Task<(HttpClient Client, Guid ProjectId, Guid Transformer, Guid Building)> SetupAsync()
    {
        factory.Calc.Rules.Add("eskom/0.5.0");
        var client = await factory.EngineerClientAsync();
        var p = await (await client.PostAsJsonAsync("/api/projects", new SaveProjectRequest($"Docs {Guid.NewGuid():N}", "eskom/0.5.0", Square(28.09, -25.53, 0.03), null)))
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
        (await client.PutAsJsonAsync($"/api/projects/{p.Id}/candidates/{tx}", new CandidateRequest("transformer", Point(Lon, Lat + 0.0001), null, null, null, null))).EnsureSuccessStatusCode();
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

    [Fact]
    public async Task Rate_list_is_copied_overridden_with_a_date_imported_and_used_by_the_designs()
    {
        var (client, projectId, tx, _) = await SetupAsync();
        var index = await client.GetFromJsonAsync<RateListsIndex>("/api/rate-lists");
        Assert.Contains("indicative/2026-10", index!.Shipped);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/rate-lists", new CreateRateListRequest("", "nope/1", null))).StatusCode);
        var created = await client.PostAsJsonAsync("/api/rate-lists", new CreateRateListRequest("Contractor 2026", "indicative/2026-10", null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var list = await created.Content.ReadFromJsonAsync<RateListDetail>();
        Assert.Equal(new DateOnly(2026, 10, 1), list!.List.RateDate);
        var pole = list.Rows.Single(r => r.Section == "pole_each");
        Assert.Equal(("OH-POLE-9-160", 5200.0), (pole.Assembly, pole.AssemblyRate!.Value));

        var bad = await client.PutAsJsonAsync($"/api/rate-lists/{list.List.Id}/rates",
            new ChangeRatesRequest([new RateChange("conductor_per_m", "ABC-35", -1, new DateOnly(2030, 1, 1), null, null, null)], null, list.List.Version));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var unknown = await client.PutAsJsonAsync($"/api/rate-lists/{list.List.Id}/rates",
            new ChangeRatesRequest([new RateChange("conductor_per_m", "ABC-999", 10, null, null, null, null)], null, list.List.Version));
        Assert.Contains("ABC-999", await unknown.Content.ReadAsStringAsync());
        var changed = await (await client.PutAsJsonAsync($"/api/rate-lists/{list.List.Id}/rates",
            new ChangeRatesRequest([new RateChange("conductor_per_m", "ABC-35", 180, new DateOnly(2026, 9, 15), "Quote Q-17", null, null)], null, list.List.Version)))
            .Content.ReadFromJsonAsync<RateListDetail>();
        Assert.Equal(2, changed!.List.Revision);
        var abc = changed.Rows.Single(r => r.Code == "ABC-35");
        Assert.Equal(180, abc.Rate);
        Assert.Equal("2026-09-15", abc.Override!.Value.GetProperty("date").GetString());
        Assert.Equal(165, abc.Override.Value.GetProperty("previous").GetDouble());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/rate-lists/{list.List.Id}/rates",
            new ChangeRatesRequest([new RateChange("stay_each", null, 2000, null, null, null, null)], null, list.List.Version))).StatusCode);

        var csv = "code,description,unit,price,date\nPOLE-9-160,,each,3450,2026-09-20\nNEW-BOLT,M16 bolt,each,12.5,\nNOPE,,each,abc,\nGHOST,,each,4,\n";
        var form = new MultipartFormDataContent { { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(csv)) { Headers = { ContentType = new MediaTypeHeaderValue("text/csv") } }, "file", "supplier.csv" } };
        var imported = await (await client.PostAsync($"/api/rate-lists/{list.List.Id}/import", form)).Content.ReadFromJsonAsync<ImportResult>();
        Assert.Equal(2, imported!.Applied);
        Assert.Equal(["materials/GHOST"], imported.Unknown);
        Assert.Single(imported.Errors);
        Assert.Equal(5350, imported.List.Rows.Single(r => r.Section == "pole_each").AssemblyRate);
        Assert.Contains(imported.List.Rows, r => r.Code == "NEW-BOLT" && r.Rate == 12.5);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/projects/{projectId}/rate-list", new SetProjectRateList(Guid.NewGuid()))).StatusCode);
        var set = await (await client.PutAsJsonAsync($"/api/projects/{projectId}/rate-list", new SetProjectRateList(list.List.Id))).Content.ReadFromJsonAsync<ProjectRateList>();
        Assert.Equal("Contractor 2026 (rev 3)", set!.Name);

        var started = await (await client.PostAsJsonAsync($"/api/projects/{projectId}/lv-designs", new LvDesignRequest(tx, null, null, null))).Content.ReadFromJsonAsync<StartedDesign>();
        await PollAsync(() => client.GetFromJsonAsync<DesignRunDetail>($"/api/projects/{projectId}/lv-designs/{started!.Run.Id}")!, d => d!.Run.Status is "succeeded" or "failed");
        var rates = factory.Calc.LastLvDesign!.Value.GetProperty("rates");
        Assert.Equal("Contractor 2026 (rev 3)", rates.GetProperty("name").GetString());
        Assert.Equal(180, rates.GetProperty("conductor_per_m").GetProperty("ABC-35").GetDouble());
        Assert.Equal(3, rates.GetProperty("overrides").GetArrayLength());
    }

    [Fact]
    public async Task Documents_are_generated_from_the_latest_runs_downloaded_by_signed_link_and_go_stale()
    {
        var (client, projectId, tx, building) = await SetupAsync();
        var none = await client.PostAsJsonAsync($"/api/projects/{projectId}/document-sets", new GenerateDocumentsRequest(null));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        var lv = await (await client.PostAsJsonAsync($"/api/projects/{projectId}/lv-designs", new LvDesignRequest(tx, null, null, null))).Content.ReadFromJsonAsync<StartedDesign>();
        await PollAsync(() => client.GetFromJsonAsync<DesignRunDetail>($"/api/projects/{projectId}/lv-designs/{lv!.Run.Id}")!, d => d!.Run.Status is "succeeded" or "failed");

        var r = await client.PostAsJsonAsync($"/api/projects/{projectId}/document-sets", new GenerateDocumentsRequest("A. Engineer Pr Eng"));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var started = await r.Content.ReadFromJsonAsync<StartedDocuments>();
        Assert.Equal(("D1", "documents.generate"), (started!.Set.Revision, started.Job.Kind));
        var set = await PollAsync(() => client.GetFromJsonAsync<DocumentSetDto>($"/api/projects/{projectId}/document-sets/{started.Set.Id}")!, s => s!.Status is "succeeded" or "failed");
        Assert.Equal("succeeded", set!.Status);
        Assert.Equal(["report", "loads_xlsx"], set.Documents.Select(d => d.Kind));
        Assert.Equal("not met", set.Checklist!.Value[0].GetProperty("status").GetString());

        var pkg = factory.Calc.LastDocuments!.Value.GetProperty("package");
        Assert.Equal("D1", pkg.GetProperty("stamp").GetProperty("revision").GetString());
        Assert.Equal("A. Engineer Pr Eng", pkg.GetProperty("stamp").GetProperty("engineer").GetString());
        Assert.Equal("indicative/2026-10", pkg.GetProperty("stamp").GetProperty("rate_list").GetString());
        Assert.Equal(1, pkg.GetProperty("lv_designs").GetArrayLength());
        Assert.Equal(lv!.Run.Id.ToString(), pkg.GetProperty("lv_designs")[0].GetProperty("run_id").GetString());
        Assert.Equal(1, pkg.GetProperty("loads").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, pkg.GetProperty("mv_design").ValueKind);

        var index = await client.GetFromJsonAsync<DocumentSetsIndex>($"/api/projects/{projectId}/document-sets");
        Assert.False(index!.Stale);
        var report = set.Documents[0];
        var link = await client.GetFromJsonAsync<DocumentLink>($"/api/projects/{projectId}/documents/{report.Id}/link");
        using var anonymous = factory.CreateClient();
        var file = await anonymous.GetAsync(link!.Url);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("%PDF-1.4 test", await file.Content.ReadAsStringAsync());
        Assert.Equal("p-d1-report.pdf", file.Content.Headers.ContentDisposition!.FileNameStar ?? file.Content.Headers.ContentDisposition.FileName);
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync($"/api/document-files/{report.Id}?token=forged")).StatusCode);
        var other = set.Documents[1].Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.GetAsync(link.Url.Replace(report.Id.ToString(), other.ToString()))).StatusCode);

        // A changed load makes the documents stale.
        (await client.PutAsJsonAsync($"/api/projects/{projectId}/buildings/{building}/load", new LoadRequest("special", null, "school", null, null, null))).EnsureSuccessStatusCode();
        index = await client.GetFromJsonAsync<DocumentSetsIndex>($"/api/projects/{projectId}/document-sets");
        Assert.True(index!.Stale);
        Assert.Contains("loads changed", index.Changes);

        var xlsx = await client.GetAsync($"/api/projects/{projectId}/load-schedule.xlsx");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        Assert.Equal("loads_xlsx", factory.Calc.LastDocuments!.Value.GetProperty("kinds")[0].GetString());
    }
}
