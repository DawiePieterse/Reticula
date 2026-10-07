using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Reticula.Api.Design;
using Reticula.Api.Field;
using Reticula.Api.Jobs;
using Reticula.Api.Layout;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Geo;
using static Reticula.Api.Tests.TestFixtures;

namespace Reticula.Api.Tests;

/// <summary>Pre-design placement (ADR 0010): the proposal is stored as candidates with source "proposed", replacing the previous proposal only.</summary>
[Collection(ApiCollection.Name)]
public class PlacementTests(ReticulaApiFactory factory)
{
    private const double Lon = 28.1, Lat = -25.52;

    private static JsonElement Point(double lon, double lat) => JsonSerializer.SerializeToElement(new { type = "Point", coordinates = new[] { lon, lat } });
    private static JsonElement Line(double lon, double lat) => JsonSerializer.SerializeToElement(new { type = "LineString", coordinates = new[] { new[] { lon, lat }, new[] { lon + 0.001, lat } } });

    private static CalcPlacement Proposal(int transformers) => new("eskom/0.7.0", "0123456789abcdef", "test",
        [.. Enumerable.Range(1, transformers).Select(k => new CalcPlacementTransformer($"TX{k}", [Lon + 0.001 * k, Lat], 100, 40, 61.5, 61.5, 2, 420))],
        [], [], [],
        [.. Enumerable.Range(1, transformers).SelectMany(k => new[]
        {
            new CalcPlacementCandidate("transformer", Point(Lon + 0.001 * k, Lat), $"TX{k}"),
            new CalcPlacementCandidate("lv_route", Line(Lon + 0.001 * k, Lat), $"TX{k}"),
        }), new CalcPlacementCandidate("mv_route", Line(Lon, Lat - 0.001), null)],
        JsonDocument.Parse("{\"value\": 1}").RootElement, null,
        [new LvIssue("error", "unassigned", "Two loads out of reach.", 2, ["a", "b"], [])], ["growth 10 %"]);

    private static async Task<JsonElement> RunAsync(HttpClient client, Guid projectId)
    {
        var r = await client.PostAsync($"/api/projects/{projectId}/placement", null);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var job = await WaitFinishedAsync(client, (await r.Content.ReadFromJsonAsync<JobDto>())!.Id);
        Assert.Equal("succeeded", job.Status);
        return job.Result!.Value;
    }

    [Fact]
    public async Task Proposal_becomes_proposed_candidates_and_a_rerun_replaces_only_those()
    {
        var (client, projectId) = await factory.NewProjectAsync("Placement", Square(Lon - 0.01, Lat - 0.01, 0.03));
        factory.Calc.OnImport = r => new CalcImportResult("roads", "geojson", "WGS84", "test",
            [new CalcFeature("w1", GeoJsonGeometry.Line([Lon, Lat], [Lon + 0.002, Lat]), 0, null, null, "way/1", new() { ["highway"] = "residential" }, [], 201, "Main St", "residential")], [], []);
        var form = new MultipartFormDataContent { { new StringContent("roads"), "kind" },
            { new ByteArrayContent("{}"u8.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }, "file", "x.geojson" } };
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync($"/api/projects/{projectId}/imports", form)).StatusCode);

        // A site the inspector marked stays through every rerun.
        var mine = Guid.NewGuid();
        var marked = await client.PutAsJsonAsync($"/api/projects/{projectId}/candidates/{mine}",
            new CandidateRequest("pole", new GeometryInput("Point", Point(Lon, Lat).GetProperty("coordinates")), "my pole", null, null, null));
        Assert.Equal(HttpStatusCode.Created, marked.StatusCode);

        factory.Calc.OnPlace = (_, _, _) => Proposal(2);
        var result = await RunAsync(client, projectId);
        Assert.Equal(2, result.GetProperty("transformers").GetInt32());
        Assert.Equal(2, result.GetProperty("unplaced").GetInt32());
        Assert.Single(factory.Calc.LastPlacement!.Value.Roads);
        Assert.Null(factory.Calc.LastPlacement!.Value.ConnectionPoint);

        var candidates = (await client.GetFromJsonAsync<GeoFeatureCollection<CandidateProps>>($"/api/projects/{projectId}/candidates"))!.Features;
        Assert.Equal(5, candidates.Count(c => c.Properties.Source == "proposed"));
        Assert.Single(candidates, c => c.Properties.Source == "field" && c.Id == mine.ToString());
        var notes = candidates.Where(c => c.Properties.Kind == "transformer").Select(c => c.Properties.Notes).ToList();
        Assert.Contains(notes, n => n!.StartsWith("Proposed TX1: 100 kVA, 40 loads", StringComparison.Ordinal));

        var status = await client.GetFromJsonAsync<PlacementStatus>($"/api/projects/{projectId}/placement");
        Assert.NotNull(status!.Placement);
        Assert.Null(status.Job);
        Assert.Equal(2, status.Placement!.Result.Transformers.Count);
        Assert.Equal("unassigned", status.Placement.Result.Issues[0].Code);

        // Confirming a proposed site on the tablet (a PUT with the same geometry) makes it a field candidate.
        var proposed = candidates.First(c => c.Properties.Kind == "transformer");
        var confirm = await client.PutAsJsonAsync($"/api/projects/{projectId}/candidates/{proposed.Id}",
            new CandidateRequest("transformer", new GeometryInput("Point", JsonSerializer.SerializeToElement(((JsonElement)proposed.Geometry).GetProperty("coordinates"))),
                "confirmed on site", proposed.Properties.Version, null, null));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        candidates = (await client.GetFromJsonAsync<GeoFeatureCollection<CandidateProps>>($"/api/projects/{projectId}/candidates"))!.Features;
        Assert.Equal("field", candidates.First(c => c.Id == proposed.Id).Properties.Source);
        Assert.Equal(4, candidates.Count(c => c.Properties.Source == "proposed"));

        factory.Calc.OnPlace = (_, _, _) => Proposal(1);
        await RunAsync(client, projectId);
        candidates = (await client.GetFromJsonAsync<GeoFeatureCollection<CandidateProps>>($"/api/projects/{projectId}/candidates"))!.Features;
        Assert.Equal(3, candidates.Count(c => c.Properties.Source == "proposed"));
        Assert.Equal(2, candidates.Count(c => c.Properties.Source == "field"));
        Assert.Equal(1, (await client.GetFromJsonAsync<PlacementStatus>($"/api/projects/{projectId}/placement"))!.Placement!.Transformers);
    }

    [Fact]
    public async Task Only_the_engineer_runs_placement_and_an_unknown_project_is_not_found()
    {
        var (client, projectId) = await factory.NewProjectAsync("Placement", Square());
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/projects/{Guid.NewGuid()}/placement", null)).StatusCode);
        var status = await client.GetFromJsonAsync<PlacementStatus>($"/api/projects/{projectId}/placement");
        Assert.Null(status!.Placement);
    }
}
