using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Reticula.Api.Auth;
using Reticula.Api.Documents;
using Reticula.Api.Field;
using Reticula.Api.Jobs;
using Reticula.Api.Review;
using Reticula.Domain.Auth;
using static Reticula.Api.Tests.DesignTests;
using static Reticula.Api.Tests.TestFixtures;

namespace Reticula.Api.Tests;

/// <summary>Review (Phase 7): assumptions, revisions that reproduce, sign-off by the registered engineer, the audit trail and the export.</summary>
[Collection(ApiCollection.Name)]
public class ReviewTests(ReticulaApiFactory factory)
{
    private static async Task<JobDto> FinishAsync(HttpClient client, HttpResponseMessage r)
    {
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var job = await WaitFinishedAsync(client, (await r.Content.ReadFromJsonAsync<JobDto>())!.Id);
        Assert.True(job.Status == "succeeded", job.Error);
        return job;
    }

    [Fact]
    public async Task A_revision_is_signed_off_only_when_fit_reproduced_documented_and_without_open_assumptions()
    {
        factory.Calc.OnDesign = null;
        factory.Calc.DesignPlaceholders.Clear();
        factory.Calc.DesignPlaceholders.Add("mv_design.max_drop_pct: PLACEHOLDER");
        factory.Calc.DesignFit = false;
        var (engineer, userId) = await factory.NewUserAsync(Roles.Engineer);
        var (_, projectId) = await MarkedProjectAsync(factory, "Sign");
        // The new engineer works on the project the bootstrap engineer made: every engineer sees every project.
        await RunAsync(engineer, projectId, new { });

        var create = await engineer.PostAsJsonAsync($"/api/projects/{projectId}/revisions", new CreateRevisionRequest(null, "for comment", null));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var rev = (await create.Content.ReadFromJsonAsync<RevisionDto>())!;
        Assert.Equal((1, "for comment", 1, false), (rev.Number, rev.Label, rev.DesignRunNumber, rev.Locked));

        var detail = (await engineer.GetFromJsonAsync<RevisionDetail>($"/api/projects/{projectId}/revisions/{rev.Id}"))!;
        var blockers = string.Join(" | ", detail.Blockers);
        foreach (var why in new[] { "ECSA registration", "not fit to submit: 1 placeholder value in the rules", "assumption(s) are still open", "Reproduce",
                     "Generate the documents" })
            Assert.Contains(why, blockers);
        var refused = await engineer.PostAsJsonAsync($"/api/projects/{projectId}/revisions/{rev.Id}/sign-off", new SignOffRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(5, (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reasons").GetArrayLength());

        // The engineer accepts the placeholder as stated; a confirmed assumption stays confirmed through later runs.
        var open = (await engineer.GetFromJsonAsync<List<AssumptionDto>>($"/api/projects/{projectId}/assumptions?status=open"))!;
        var placeholder = Assert.Single(open);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await engineer.PostAsJsonAsync($"/api/projects/{projectId}/assumptions/{placeholder.Id}/confirm", new ConfirmAssumptionRequest(" "))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await engineer.PostAsJsonAsync($"/api/projects/{projectId}/assumptions/{placeholder.Id}/confirm",
            new ConfirmAssumptionRequest("Confirmed with the authority's planning engineer on 2026-10-05."))).StatusCode);

        // A fit design: the rules values verified, the connection point entered, the assumption confirmed by the engineer.
        factory.Calc.DesignPlaceholders.Clear();
        factory.Calc.DesignFit = true;
        await SaveConnectionPointAsync(engineer, projectId);
        await RunAsync(engineer, projectId, new { });
        var bootstrap = await factory.EngineerClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await bootstrap.PutAsJsonAsync($"/api/users/{userId}", new UpdateUserRequest(null, "20100123"))).StatusCode);
        rev = (await (await engineer.PostAsJsonAsync($"/api/projects/{projectId}/revisions", new CreateRevisionRequest(null, "for construction", null)))
            .Content.ReadFromJsonAsync<RevisionDto>())!;
        Assert.True(rev.FitToSubmit);
        Assert.Equal(2, rev.Number);

        var reproduce = await FinishAsync(engineer, await engineer.PostAsync($"/api/projects/{projectId}/revisions/{rev.Id}/reproduce", null));
        Assert.True(reproduce.Result!.Value.GetProperty("identical").GetBoolean());
        await FinishAsync(engineer, await engineer.PostAsync($"/api/projects/{projectId}/revisions/{rev.Id}/documents", null));
        var docs = (await engineer.GetFromJsonAsync<DocumentsStatus>($"/api/projects/{projectId}/documents"))!.Documents;
        Assert.All(docs, d => Assert.Equal((rev.Id, 2), (d.RevisionId!.Value, d.RevisionNumber)));

        detail = (await engineer.GetFromJsonAsync<RevisionDetail>($"/api/projects/{projectId}/revisions/{rev.Id}"))!;
        Assert.Empty(detail.Blockers);
        Assert.Equal("confirmed", (await engineer.GetFromJsonAsync<List<AssumptionDto>>($"/api/projects/{projectId}/assumptions"))!.Single(a => a.Id == placeholder.Id).Status);

        var signed = await engineer.PostAsJsonAsync($"/api/projects/{projectId}/revisions/{rev.Id}/sign-off", new SignOffRequest(null));
        Assert.Equal(HttpStatusCode.Accepted, signed.StatusCode);
        var accepted = (await signed.Content.ReadFromJsonAsync<SignOffAccepted>())!;
        Assert.Equal(("Test engineer", "20100123", true), (accepted.Revision.EngineerName!, accepted.Revision.RegistrationNo!, accepted.Revision.Locked));
        var regen = await WaitFinishedAsync(engineer, accepted.Documents.Id);
        Assert.True(regen.Status == "succeeded", regen.Error);
        var meta = JsonNode.Parse(factory.Calc.DocumentBodies["report_pdf"])!["meta"]!;
        Assert.Equal((true, "Test engineer", "20100123", 2), (meta["signed_off"]!.GetValue<bool>(), meta["engineer_name"]!.GetValue<string>(),
            meta["engineer_registration"]!.GetValue<string>(), meta["revision_number"]!.GetValue<int>()));
        docs = (await engineer.GetFromJsonAsync<DocumentsStatus>($"/api/projects/{projectId}/documents"))!.Documents;
        Assert.Equal(9, docs.Count);
        Assert.All(docs, d => Assert.True(d.Locked));

        Assert.Equal(HttpStatusCode.Conflict, (await engineer.PostAsJsonAsync($"/api/projects/{projectId}/revisions/{rev.Id}/sign-off", new SignOffRequest(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await engineer.PostAsync($"/api/projects/{projectId}/revisions/{rev.Id}/documents", null)).StatusCode);

        var inspector = await factory.InspectorClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await inspector.PostAsJsonAsync($"/api/projects/{projectId}/revisions/{rev.Id}/sign-off", new SignOffRequest(null))).StatusCode);
    }

    [Fact]
    public async Task A_reproduction_that_differs_names_where()
    {
        factory.Calc.DesignPlaceholders.Clear();
        var (client, projectId) = await MarkedProjectAsync(factory, "Repro");
        await RunAsync(client, projectId, new { });
        var rev = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/revisions", new CreateRevisionRequest(null, null, null)))
            .Content.ReadFromJsonAsync<RevisionDto>())!;
        factory.Calc.OnDesign = d =>
        {
            d["cost"]!["capex"] = 99.0;
            return d;
        };
        try
        {
            var job = await FinishAsync(client, await client.PostAsync($"/api/projects/{projectId}/revisions/{rev.Id}/reproduce", null));
            Assert.False(job.Result!.Value.GetProperty("identical").GetBoolean());
            Assert.Equal(["$.cost.capex"], job.Result.Value.GetProperty("differences").EnumerateArray().Select(x => x.GetString()));
        }
        finally
        {
            factory.Calc.OnDesign = null;
        }
        var detail = (await client.GetFromJsonAsync<RevisionDetail>($"/api/projects/{projectId}/revisions/{rev.Id}"))!;
        Assert.False(detail.Revision.Reproduced);
        Assert.Contains(detail.Blockers, b => b.Contains("different result"));
        // A reproduction is not the project's design.
        var runs = (await client.GetFromJsonAsync<Design.DesignRunsStatus>($"/api/projects/{projectId}/design-runs"))!.Runs;
        Assert.Equal([("reproduce", false), ("run", true)], runs.Select(r => (r.Mode, r.Current)));
    }

    [Fact]
    public async Task Every_change_is_audited_with_who_and_what_and_the_export_holds_the_project()
    {
        factory.Calc.DesignPlaceholders.Clear();
        var (client, projectId) = await MarkedProjectAsync(factory, "Audit");
        await SaveConnectionPointAsync(client, projectId);
        await client.PutAsJsonAsync($"/api/projects/{projectId}/connection-point",
            new Design.SaveConnectionPointRequest(Reticula.Infrastructure.Geo.PointDto.Of(28.09, -25.52), 11, 2500, 8.5, 6, 4, 6, "Eskom letter 2", null, null));
        await RunAsync(client, projectId, new { });

        var audit = (await client.GetFromJsonAsync<List<AuditEntryDto>>($"/api/projects/{projectId}/audit?take=200"))!;
        Assert.Contains(audit, a => (a.EntityType, a.Action) == ("Project", "added"));
        Assert.Equal(3, audit.Count(a => (a.EntityType, a.Action) == ("Candidate", "added")));
        var change = audit.First(a => (a.EntityType, a.Action) == ("ConnectionPoint", "modified"));
        Assert.Equal("Test Engineer", change.UserName);
        Assert.Equal((2000, 2500), (change.Before!.Value.GetProperty("CapacityKva").GetDouble(), change.After!.Value.GetProperty("CapacityKva").GetDouble()));
        Assert.False(change.After.Value.TryGetProperty("Location", out _) && change.Before.Value.GetProperty("Location").GetString() == change.After.Value.GetProperty("Location").GetString());
        // Changes a background job saves are the requesting user's.
        var run = audit.Single(a => a.EntityType == "DesignRun");
        Assert.Equal("Test Engineer", run.UserName);
        Assert.DoesNotContain(audit, a => a.EntityType is "JobRun" or "AuditEntry");

        var r = await client.GetAsync($"/api/projects/{projectId}/export");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/zip", r.Content.Headers.ContentType!.MediaType);
        using var zip = new ZipArchive(await r.Content.ReadAsStreamAsync());
        var names = zip.Entries.Select(e => e.FullName).ToHashSet();
        foreach (var n in new[] { "README.txt", "project.json", "layout/buildings.geojson", "field/candidates.geojson", "design/runs.json",
                     "design/run-1/request.json", "design/run-1/result.json", "review/audit.json" })
            Assert.Contains(n, names);
        using var candidates = JsonDocument.Parse(zip.GetEntry("field/candidates.geojson")!.Open());
        Assert.Equal(3, candidates.RootElement.GetProperty("features").GetArrayLength());
        using var project = JsonDocument.Parse(zip.GetEntry("project.json")!.Open());
        Assert.Equal("Eskom letter 2", project.RootElement.GetProperty("connectionPoint").GetProperty("source").GetString());
    }

    [Fact]
    public async Task Report_sections_go_back_to_draft_when_edited()
    {
        var (client, projectId) = await factory.NewProjectAsync("Sections", Square());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/projects/{projectId}/report-sections/Bad Key", new { title = "x", text = "y" })).StatusCode);
        await client.PutAsJsonAsync($"/api/projects/{projectId}/report-sections/scope", new { title = "Scope", text = "One" });
        var approved = (await (await client.PostAsync($"/api/projects/{projectId}/report-sections/scope/approve", null)).Content.ReadFromJsonAsync<ReportSectionDto>())!;
        Assert.Equal("approved", approved.Status);
        var edited = (await (await client.PutAsJsonAsync($"/api/projects/{projectId}/report-sections/scope", new { title = "Scope", text = "Two" }))
            .Content.ReadFromJsonAsync<ReportSectionDto>())!;
        Assert.Equal(("draft", "Two"), (edited.Status, edited.Text));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/projects/{projectId}/report-sections/scope",
            new { title = "Scope", text = "Three", version = approved.Version })).StatusCode);
    }
}
