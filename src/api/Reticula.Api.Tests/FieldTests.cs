using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Reticula.Api.Auth;
using Reticula.Api.Field;
using Reticula.Api.Layout;
using Reticula.Api.Projects;
using Reticula.Domain.Auth;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Geo;

namespace Reticula.Api.Tests;

[Collection(ApiCollection.Name)]
public class FieldTests(ReticulaApiFactory factory)
{
    private const double Lon = 28.10, Lat = -25.52;

    private sealed record Ctx(HttpClient Engineer, Guid ProjectId, List<Guid> Buildings);

    private static PolygonDto Square(double lon, double lat, double d) =>
        new("Polygon", [[[lon, lat], [lon + d, lat], [lon + d, lat + d], [lon, lat + d], [lon, lat]]]);

    /// <summary>A project with one stand (erf 7001) and three imported buildings on it.</summary>
    private async Task<Ctx> SetupAsync()
    {
        var client = await factory.EngineerClientAsync();
        var p = await (await client.PostAsJsonAsync("/api/projects", new SaveProjectRequest($"Field {Guid.NewGuid():N}", "eskom/0.1.0", Square(28.09, -25.53, 0.03), null)))
            .Content.ReadFromJsonAsync<ProjectDto>();

        factory.Calc.OnImport = r => new CalcImportResult(r.Kind, "kml", "WGS84", "test",
            [new CalcFeature("s1", Square(Lon, Lat, 0.001), 5000, "7001", "Residential 1", null, [], [])], [], []);
        await UploadAsync(client, p!.Id, "stands");
        factory.Calc.OnImport = r => new CalcImportResult(r.Kind, "geojson", "WGS84", "test",
            [.. Enumerable.Range(0, 3).Select(i => new CalcFeature($"way/{i}", Square(Lon + 0.0002 * (i + 1), Lat + 0.0002, 0.0001), 80, null, null, $"way/{i}", new() { ["building"] = "yes" }, []))], [], []);
        await UploadAsync(client, p.Id, "buildings");

        var fc = await client.GetFromJsonAsync<GeoFeatureCollection<BuildingProps>>($"/api/projects/{p.Id}/buildings");
        return new Ctx(client, p.Id, [.. fc!.Features.Select(f => Guid.Parse(f.Id))]);
    }

    private static async Task UploadAsync(HttpClient client, Guid projectId, string kind)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("x"u8.ToArray()), "file", "f.kml" },
            { new StringContent(kind), "kind" },
        };
        (await client.PostAsync($"/api/projects/{projectId}/imports", form)).EnsureSuccessStatusCode();
    }

    private async Task<HttpClient> InspectorAsync(HttpClient engineer)
    {
        var email = $"inspector-{Guid.NewGuid():N}@test.local";
        (await engineer.PostAsJsonAsync("/api/users", new CreateUserRequest(email, "Inspector", "inspector-pass-1", Roles.Inspector, null))).EnsureSuccessStatusCode();
        return await factory.ClientAsAsync(email, "inspector-pass-1");
    }

    private static async Task<BuildingFieldDto> InspectAsync(HttpClient c, Ctx ctx, Guid buildingId, string action, string? type = null, uint? version = null, Guid? inspectionId = null)
    {
        var current = version ?? (await CurrentAsync(c, ctx, buildingId)).Version;
        var r = await c.PutAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/{buildingId}/inspection",
            new BuildingInspectionRequest(inspectionId ?? Guid.NewGuid(), action, type, new PositionDto(Lon, Lat, 4.5), DateTimeOffset.UtcNow, "note", current));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<BuildingFieldDto>())!;
    }

    private static async Task<BuildingFieldDto> CurrentAsync(HttpClient c, Ctx ctx, Guid buildingId) =>
        (await c.GetFromJsonAsync<BuildingFieldDto>($"/api/projects/{ctx.ProjectId}/buildings/{buildingId}"))!;

    [Fact]
    public async Task One_tap_confirm_uses_the_prediction_and_counts_progress()
    {
        var ctx = await SetupAsync();
        var inspector = await InspectorAsync(ctx.Engineer);
        var b = await InspectAsync(inspector, ctx, ctx.Buildings[0], "confirm");
        Assert.Equal(("confirmed", b.PredictedType, "7001"), (b.Status, b.EffectiveType, b.Erf));

        var corrected = await InspectAsync(inspector, ctx, ctx.Buildings[1], "correct", "shop");
        Assert.Equal(("confirmed", "shop"), (corrected.Status, corrected.EffectiveType));

        var progress = await ctx.Engineer.GetFromJsonAsync<FieldProgress>($"/api/projects/{ctx.ProjectId}/field-progress");
        Assert.Equal((3, 2, 1), (progress!.Buildings, progress.Confirmed, progress.Outstanding));
    }

    [Fact]
    public async Task Same_inspection_synced_twice_is_applied_once()
    {
        var ctx = await SetupAsync();
        var id = Guid.NewGuid();
        var first = await InspectAsync(ctx.Engineer, ctx, ctx.Buildings[0], "correct", "school", inspectionId: id);
        // The second send carries the old version; it must not conflict or apply again.
        var again = await InspectAsync(ctx.Engineer, ctx, ctx.Buildings[0], "correct", "shop", version: 1, inspectionId: id);
        Assert.Equal("school", again.EffectiveType);
        Assert.Equal(first.Version, again.Version);
    }

    [Fact]
    public async Task Stale_version_returns_409_with_the_current_state()
    {
        var ctx = await SetupAsync();
        var seen = await CurrentAsync(ctx.Engineer, ctx, ctx.Buildings[0]);
        await InspectAsync(ctx.Engineer, ctx, ctx.Buildings[0], "correct", "school", version: seen.Version);

        var r = await ctx.Engineer.PutAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/{ctx.Buildings[0]}/inspection",
            new BuildingInspectionRequest(Guid.NewGuid(), "correct", "shop", null, DateTimeOffset.UtcNow, null, seen.Version));
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("school", (await r.Content.ReadFromJsonAsync<BuildingFieldDto>())!.EffectiveType);
    }

    [Fact]
    public async Task Building_collection_carries_version_and_effective_type()
    {
        var ctx = await SetupAsync();
        await InspectAsync(ctx.Engineer, ctx, ctx.Buildings[0], "correct", "school");
        var fc = await ctx.Engineer.GetFromJsonAsync<GeoFeatureCollection<BuildingProps>>($"/api/projects/{ctx.ProjectId}/buildings");
        var props = fc!.Features.Single(f => f.Id == ctx.Buildings[0].ToString()).Properties;
        Assert.Equal(("school", "confirmed"), (props.EffectiveType, props.Status));
        Assert.Equal((await CurrentAsync(ctx.Engineer, ctx, ctx.Buildings[0])).Version, props.Version);
    }

    [Fact]
    public async Task Missing_version_or_bad_action_is_rejected()
    {
        var ctx = await SetupAsync();
        var url = $"/api/projects/{ctx.ProjectId}/buildings/{ctx.Buildings[0]}/inspection";
        Assert.Equal(HttpStatusCode.BadRequest, (await ctx.Engineer.PutAsJsonAsync(url, new BuildingInspectionRequest(Guid.NewGuid(), "confirm", null, null, DateTimeOffset.UtcNow, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ctx.Engineer.PutAsJsonAsync(url, new BuildingInspectionRequest(Guid.NewGuid(), "demolish", null, null, DateTimeOffset.UtcNow, null, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ctx.Engineer.PutAsJsonAsync(url, new BuildingInspectionRequest(Guid.NewGuid(), "correct", "castle", null, DateTimeOffset.UtcNow, null, 1))).StatusCode);
    }

    [Fact]
    public async Task Not_present_building_cannot_take_a_load()
    {
        var ctx = await SetupAsync();
        var b = await InspectAsync(ctx.Engineer, ctx, ctx.Buildings[2], "not_present");
        Assert.Equal("notpresent", b.Status);
        var r = await ctx.Engineer.PutAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/{b.Id}/load", new LoadRequest("residential", [], null, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task New_building_is_added_once_and_must_be_in_the_area()
    {
        var ctx = await SetupAsync();
        var req = new NewBuildingRequest(Guid.NewGuid(), Guid.NewGuid(), "house", new PositionDto(Lon + 0.0005, Lat + 0.0005, 3), DateTimeOffset.UtcNow, "Behind the shop");
        var r = await ctx.Engineer.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/new", req);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var b = await r.Content.ReadFromJsonAsync<BuildingFieldDto>();
        Assert.Equal(("new", "house", "7001"), (b!.Status, b.EffectiveType, b.Erf));
        Assert.Equal(HttpStatusCode.OK, (await ctx.Engineer.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/new", req)).StatusCode);

        var outside = req with { Id = Guid.NewGuid(), Position = new PositionDto(2.35, 48.85, 3) };
        Assert.Equal(HttpStatusCode.BadRequest, (await ctx.Engineer.PostAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/new", outside)).StatusCode);

        var fc = await ctx.Engineer.GetFromJsonAsync<JsonElement>($"/api/projects/{ctx.ProjectId}/buildings");
        Assert.Contains(fc.GetProperty("features").EnumerateArray(), f => f.GetProperty("geometry").GetProperty("type").GetString() == "Point");
    }

    [Fact]
    public async Task Photos_upload_once_and_download()
    {
        var ctx = await SetupAsync();
        var id = Guid.NewGuid();
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
        MultipartFormDataContent Form(string type) => new()
        {
            { new ByteArrayContent(jpeg) { Headers = { ContentType = new MediaTypeHeaderValue(type) } }, "file", "site.jpg" },
            { new StringContent(id.ToString()), "id" },
            { new StringContent(ctx.Buildings[0].ToString()), "buildingId" },
            { new StringContent(DateTimeOffset.UtcNow.ToString("O")), "capturedAt" },
        };
        Assert.Equal(HttpStatusCode.Created, (await ctx.Engineer.PostAsync($"/api/projects/{ctx.ProjectId}/photos", Form("image/jpeg"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ctx.Engineer.PostAsync($"/api/projects/{ctx.ProjectId}/photos", Form("image/jpeg"))).StatusCode);
        Assert.Equal(jpeg, await ctx.Engineer.GetByteArrayAsync($"/api/projects/{ctx.ProjectId}/photos/{id}"));

        id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await ctx.Engineer.PostAsync($"/api/projects/{ctx.ProjectId}/photos", Form("application/pdf"))).StatusCode);
        var list = await ctx.Engineer.GetFromJsonAsync<List<PhotoDto>>($"/api/projects/{ctx.ProjectId}/photos?buildingId={ctx.Buildings[0]}");
        Assert.Single(list!);
    }

    [Fact]
    public async Task Candidates_validate_geometry_and_detect_conflicts()
    {
        var ctx = await SetupAsync();
        var inspector = await InspectorAsync(ctx.Engineer);
        var url = $"/api/projects/{ctx.ProjectId}/candidates/{Guid.NewGuid()}";
        var point = new GeometryInput("Point", JsonSerializer.SerializeToElement(new[] { Lon, Lat }));
        var line = new GeometryInput("LineString", JsonSerializer.SerializeToElement(new[] { new[] { Lon, Lat }, new[] { Lon + 0.001, Lat } }));

        var created = await inspector.PutAsJsonAsync(url, new CandidateRequest("transformer", point, "Open corner", null, null, null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var feature = await created.Content.ReadFromJsonAsync<GeoFeature<CandidateProps>>();

        Assert.Equal(HttpStatusCode.BadRequest, (await inspector.PutAsJsonAsync($"/api/projects/{ctx.ProjectId}/candidates/{Guid.NewGuid()}", new CandidateRequest("mv_route", point, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await inspector.PutAsJsonAsync($"/api/projects/{ctx.ProjectId}/candidates/{Guid.NewGuid()}", new CandidateRequest("mv_route", line, null, null, null, null))).StatusCode);

        var moved = new GeometryInput("Point", JsonSerializer.SerializeToElement(new[] { Lon + 0.0001, Lat }));
        Assert.Equal(HttpStatusCode.OK, (await inspector.PutAsJsonAsync(url, new CandidateRequest("transformer", moved, "Moved", feature!.Properties.Version, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await inspector.PutAsJsonAsync(url, new CandidateRequest("transformer", point, "Stale", feature.Properties.Version, null, null))).StatusCode);

        var list = await inspector.GetFromJsonAsync<GeoFeatureCollection<CandidateProps>>($"/api/projects/{ctx.ProjectId}/candidates");
        Assert.Equal(2, list!.Features.Count);
        Assert.Equal(HttpStatusCode.NoContent, (await inspector.DeleteAsync(url)).StatusCode);
        Assert.Single((await inspector.GetFromJsonAsync<GeoFeatureCollection<CandidateProps>>($"/api/projects/{ctx.ProjectId}/candidates"))!.Features);
    }

    [Fact]
    public async Task Load_estimates_feed_the_assumptions_register_until_confirmed()
    {
        var ctx = await SetupAsync();
        var inspector = await InspectorAsync(ctx.Engineer);
        var url = $"/api/projects/{ctx.ProjectId}/buildings/{ctx.Buildings[0]}/load";
        var obs = new Dictionary<string, JsonElement> { ["dwelling"] = JsonSerializer.SerializeToElement("rdp") };

        var lp = await (await inspector.PutAsJsonAsync(url, new LoadRequest("residential", obs, null, null, null, null))).Content.ReadFromJsonAsync<LoadPointDto>();
        Assert.Equal((1.5, "estimated", false), (lp!.Kva, lp.Status, lp.Overridden));
        var open = await OpenAssumptionsAsync(ctx);
        Assert.Equal(["admd_estimated", "indicators_missing"], open.Select(a => a.Code).Order());
        Assert.Contains("erf 7001", open[0].Text);

        Assert.Equal(HttpStatusCode.BadRequest, (await inspector.PutAsJsonAsync(url, new LoadRequest("residential", obs, null, 2.2, null, lp.Version))).StatusCode);
        lp = await (await inspector.PutAsJsonAsync(url, new LoadRequest("residential", obs, null, 2.2, "=Spaza shop at the back", lp.Version))).Content.ReadFromJsonAsync<LoadPointDto>();
        Assert.Equal((2.2, 1.5, true), (lp!.Kva, lp.EstimatedKva, lp.Overridden));
        Assert.Contains("admd_overridden", (await OpenAssumptionsAsync(ctx)).Select(a => a.Code));

        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PostAsync($"/api/projects/{ctx.ProjectId}/load-points/{lp.Id}/confirm", null)).StatusCode);
        var confirmed = await (await ctx.Engineer.PostAsync($"/api/projects/{ctx.ProjectId}/load-points/{lp.Id}/confirm", null)).Content.ReadFromJsonAsync<LoadPointDto>();
        Assert.Equal("confirmed", confirmed!.Status);
        Assert.Empty(await OpenAssumptionsAsync(ctx));

        // Re-estimating reopens the register.
        await inspector.PutAsJsonAsync(url, new LoadRequest("residential", obs, null, null, null, confirmed.Version));
        Assert.Contains("admd_estimated", (await OpenAssumptionsAsync(ctx)).Select(a => a.Code));

        var csv = await ctx.Engineer.GetStringAsync($"/api/projects/{ctx.ProjectId}/load-schedule.csv");
        Assert.Contains("erf,building_id,building_type", csv);
    }

    [Fact]
    public async Task Schedule_totals_and_csv_escape_formula_text()
    {
        var ctx = await SetupAsync();
        var res = new Dictionary<string, JsonElement> { ["dwelling"] = JsonSerializer.SerializeToElement("rdp"), ["roof"] = JsonSerializer.SerializeToElement("tiles") };
        await ctx.Engineer.PutAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/{ctx.Buildings[0]}/load", new LoadRequest("residential", res, null, null, null, null));
        await ctx.Engineer.PutAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/{ctx.Buildings[1]}/load", new LoadRequest("residential", res, null, 3, "=HYPERLINK(\"x\")", null));
        await ctx.Engineer.PutAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/{ctx.Buildings[2]}/load", new LoadRequest("special", null, "school", null, null, null));

        var s = await ctx.Engineer.GetFromJsonAsync<LoadSchedule>($"/api/projects/{ctx.ProjectId}/load-schedule");
        Assert.Equal(3, s!.Rows.Count);
        Assert.Equal((2, 1, 25.0), (s.Totals!.ResidentialCount, s.Totals.SpecialCount, s.Totals.SpecialKva));
        Assert.Equal((1.5 + 3) * (1 + 1.5 / 2) + 25, s.Totals.TotalKva, 6);

        var csv = await ctx.Engineer.GetStringAsync($"/api/projects/{ctx.ProjectId}/load-schedule.csv");
        Assert.Contains("'=HYPERLINK", csv);
        Assert.DoesNotContain(",=HYPERLINK", csv);
        Assert.Contains("TOTAL after diversity", csv);
    }

    [Fact]
    public async Task Engineer_class_choice_is_stored_and_classes_reach_the_group_calculation()
    {
        var ctx = await SetupAsync();
        var obs = new Dictionary<string, JsonElement> { ["dwelling"] = JsonSerializer.SerializeToElement("rdp") };
        var scored = await (await ctx.Engineer.PutAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/{ctx.Buildings[0]}/load",
            new LoadRequest("residential", obs, null, null, null, null))).Content.ReadFromJsonAsync<LoadPointDto>();
        Assert.Equal(("township_area", null), (scored!.Category, scored.ClassOverride));

        var chosen = await (await ctx.Engineer.PutAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/{ctx.Buildings[1]}/load",
            new LoadRequest("residential", obs, null, null, null, null, "rural_village"))).Content.ReadFromJsonAsync<LoadPointDto>();
        Assert.Equal(("rural_village", "rural_village"), (chosen!.Category, chosen.ClassOverride));
        var text = (await OpenAssumptionsAsync(ctx)).First(a => a.SubjectId == chosen.Id && a.Code == "admd_estimated").Text;
        Assert.Contains("chosen by the engineer", text);

        await ctx.Engineer.GetFromJsonAsync<LoadSchedule>($"/api/projects/{ctx.ProjectId}/load-schedule");
        Assert.Equal(["rural_village", "township_area"], factory.Calc.LastGroupLoads.Select(l => l.LoadClass).Order());
    }

    [Fact]
    public async Task Rejected_observations_return_400_with_the_reason()
    {
        var ctx = await SetupAsync();
        var obs = new Dictionary<string, JsonElement> { ["dwelling"] = JsonSerializer.SerializeToElement("castle") };
        var r = await ctx.Engineer.PutAsJsonAsync($"/api/projects/{ctx.ProjectId}/buildings/{ctx.Buildings[0]}/load", new LoadRequest("residential", obs, null, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("castle", await r.Content.ReadAsStringAsync());
    }

    private async Task<List<AssumptionDto>> OpenAssumptionsAsync(Ctx ctx) =>
        (await ctx.Engineer.GetFromJsonAsync<List<AssumptionDto>>($"/api/projects/{ctx.ProjectId}/assumptions?status=open"))!;
}
