using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Design;
using Reticula.Domain.Documents;
using Reticula.Domain.Projects;
using Reticula.Domain.Review;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Field;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Infrastructure.Documents;

/// <param name="DesignRunId">The design the documents show; the project's current design when null.</param>
/// <param name="RevisionId">The revision they are issued under; a draft (revision 0) when null.</param>
public sealed record DocumentPayload(Guid? DesignRunId = null, Guid? RevisionId = null);

/// <summary>
/// Generates every design document (Phase 6) in the calc service from one design run and stores them, stamped (plan 6.8) with the
/// run, the revision, the rules, the rate date and the design date; then the submission pack from them (plan 6.6). The documents of a
/// signed-off revision are locked. Earlier unlocked documents of the project are superseded, never deleted.
/// </summary>
public sealed class DocumentJob(ReticulaDbContext db, ICalcClient calc, IFileStore files, TimeProvider time) : IJobHandler
{
    public const string JobKind = "documents.generate";
    public string Kind => JobKind;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var project = (context.ProjectId is { } projectId ? await db.Projects.ActiveAsync(projectId, ct) : null)
                      ?? throw new InvalidOperationException("The project no longer exists.");
        var payload = context.Payload.Deserialize<DocumentPayload>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new DocumentPayload();
        var revision = payload.RevisionId is { } rid
            ? await db.Revisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid && r.ProjectId == project.Id, ct)
              ?? throw new InvalidOperationException("The revision no longer exists.")
            : null;
        var runId = revision?.DesignRunId ?? payload.DesignRunId;
        var run = runId is { } id
            ? await db.DesignRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.ProjectId == project.Id, ct)
            : await DesignRuns.CurrentAsync(db, project.Id, ct);
        if (run is not { HasDesign: true }) throw new InvalidOperationException("There is no design to make documents from: run the design first.");

        var user = await db.Users.AsNoTracking().Where(u => u.Id == context.RequestedBy).Select(u => new { u.DisplayName, u.RegistrationNo }).FirstOrDefaultAsync(ct);
        var signed = revision?.SignedOffAt is not null;
        var prefix = Prefix(project.Name);
        DocumentMeta Meta(string? number) => new(project.Name, prefix, null, Authority(project), null, revision?.Number ?? 0,
            revision?.Label ?? "draft", run.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), number,
            signed ? revision!.EngineerName : user?.DisplayName, signed ? revision!.RegistrationNo : user?.RegistrationNo, signed,
            revision?.SignedOffAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        await context.Progress.ReportAsync(5, "Reading the load schedule and report sections", ct);
        var schedule = await LoadSchedules.BuildAsync(db, calc, time, project.Id, ct);
        var sections = await db.ReportSections.AsNoTracking()
            .Where(s => s.ProjectId == project.Id && s.Status == ReportSectionStatus.Approved)
            .OrderBy(s => s.Order).ThenBy(s => s.Key).Select(s => new DocumentSectionIn(s.Title, s.Text)).ToListAsync(ct);
        var rows = ScheduleRows(schedule);
        var totals = ScheduleTotals(schedule);
        var rateDate = RateDate(run);

        var now = time.GetUtcNow();
        var made = new List<(Document Doc, byte[] Content)>();
        var kinds = DocumentKinds.Rendered;
        for (var i = 0; i < kinds.Count; i++)
        {
            var kind = kinds[i];
            await context.Progress.ReportAsync(10 + 75 * i / kinds.Count, $"Rendering the {DocumentKinds.Title(kind).ToLowerInvariant()}", ct);
            var number = Number(prefix, kind, i);
            var body = Body(Meta(number), run.ResultJson!, kind == DocumentKinds.LoadSchedule ? rows : null,
                kind == DocumentKinds.LoadSchedule ? totals : null, kind == DocumentKinds.Report ? sections : null);
            var file = await calc.RenderDocumentAsync(kind, body, ct);
            made.Add((await StoreAsync(project, run, revision, kind, number, file, rateDate, context.RequestedBy, now, ct), file.Content));
        }

        await context.Progress.ReportAsync(88, "Making the submission pack", ct);
        var pack = new
        {
            meta = Meta(Number(prefix, DocumentKinds.Pack, kinds.Count)),
            files = made.Select(m => new PackFileIn(m.Doc.FileName, m.Doc.Title, m.Doc.Kind, m.Doc.Number, Convert.ToBase64String(m.Content))).ToList(),
        };
        var packBody = JsonSerializer.SerializeToNode(pack, CalcJson.Options)!.AsObject();
        var packJson = packBody.ToJsonString().Insert(1, $"\"design\":{run.ResultJson},");
        var packFile = await calc.PackDocumentsAsync(packJson, ct);
        var packDoc = await StoreAsync(project, run, revision, DocumentKinds.Pack, pack.meta.DocumentNumber!, packFile, rateDate, context.RequestedBy, now, ct);

        await context.Progress.ReportAsync(96, "Saving", ct);
        var ids = made.Select(m => m.Doc.Id).Append(packDoc.Id).ToHashSet();
        var older = await db.Documents.Where(d => d.ProjectId == project.Id && d.SupersededAt == null && !d.Locked && !ids.Contains(d.Id)).ToListAsync(ct);
        foreach (var d in older) d.Supersede(now);
        if (signed)
            foreach (var d in await db.Documents.Where(d => ids.Contains(d.Id)).ToListAsync(ct)) d.Lock();
        await db.SaveChangesAsync(ct);
        return new { documents = ids.Count, designRun = run.Number, revision = revision?.Number ?? 0, locked = signed, superseded = older.Count };
    }

    private async Task<Document> StoreAsync(Project project, DesignRun run, Revision? revision, string kind, string number, CalcFile file, string rateDate,
        Guid by, DateTimeOffset now, CancellationToken ct)
    {
        var id = Guid.CreateVersion7();
        var ext = Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant() is { Length: > 0 and <= 8 } e && e.All(char.IsAsciiLetterOrDigit) ? e : "bin";
        var key = $"documents/{project.Id:n}/{id:n}.{ext}";
        using (var ms = new MemoryStream(file.Content, writable: false))
            await files.SaveAsync(key, ms, ct);
        var doc = new Document(id, project.Id, run.Id, revision?.Id, kind, DocumentKinds.Title(kind), number, file.FileName, key, file.ContentType,
            file.Content.LongLength, Convert.ToHexStringLower(SHA256.HashData(file.Content)), run.RulesRef, run.RulesHash, rateDate,
            DateOnly.FromDateTime(run.CreatedAt.UtcDateTime), revision?.Number ?? 0, run.InputsHash, by, now);
        db.Documents.Add(doc);
        return doc;
    }

    /// <summary>{meta, design, rows?, totals?, sections?}, with the design as the calc service gave it.</summary>
    public static string Body(DocumentMeta meta, string designJson, JsonArray? rows, JsonObject? totals, IReadOnlyList<DocumentSectionIn>? sections)
    {
        var sb = new StringBuilder("{\"meta\":").Append(JsonSerializer.Serialize(meta, CalcJson.Options)).Append(",\"design\":").Append(designJson);
        if (rows is not null) sb.Append(",\"rows\":").Append(rows.ToJsonString());
        if (totals is not null) sb.Append(",\"totals\":").Append(totals.ToJsonString());
        if (sections is not null) sb.Append(",\"sections\":").Append(JsonSerializer.Serialize(sections, CalcJson.Options));
        return sb.Append('}').ToString();
    }

    private static JsonArray ScheduleRows(LoadSchedule? s) => new([.. (s?.Rows ?? []).Select(r => (JsonNode)new JsonObject
    {
        ["erf"] = r.Erf, ["building_type"] = r.BuildingType, ["building_status"] = r.BuildingStatus, ["load_kind"] = r.LoadKind,
        ["category"] = r.Category, ["income_band"] = r.IncomeBand, ["kva"] = r.Kva, ["estimated_kva"] = r.EstimatedKva,
        ["overridden"] = r.Overridden, ["override_reason"] = r.OverrideReason, ["load_status"] = r.LoadStatus,
    })]);

    /// <summary>The calc service's diversified totals, as they appear under the schedule; nothing is summed here.</summary>
    private static JsonObject ScheduleTotals(LoadSchedule? s)
    {
        if (s?.Totals is not { } t) return new JsonObject { ["Loads"] = 0 };
        return new JsonObject
        {
            ["Residential loads"] = t.ResidentialCount, ["Residential kVA after diversity"] = t.ResidentialKva,
            ["Special loads"] = t.SpecialCount, ["Special kVA"] = t.SpecialKva, ["Total kVA after diversity"] = t.TotalKva,
            ["Diversity factor"] = t.DiversityFactor, ["Method"] = t.Method, ["Formula"] = t.Formula, ["Clause"] = t.Clause,
            ["Rules"] = $"{s.RulesRef} ({s.RulesHash})",
        };
    }

    private static string RateDate(DesignRun run)
    {
        if (run.SummaryJson is null) return "";
        using var doc = JsonDocument.Parse(run.SummaryJson);
        return doc.RootElement.TryGetProperty("rate_date", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";
    }

    public static string Authority(Project p) => p.Authority.Length == 0 ? "" : char.ToUpperInvariant(p.Authority[0]) + p.Authority[1..];

    /// <summary>Initials of the project's name and its numbers, e.g. "Soshanguve Ext 19" gives SE19: Reticula's numbering until the authority assigns its own.</summary>
    public static string Prefix(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var p = string.Concat(parts.Select(w => w.All(char.IsAsciiDigit) ? w : w[..1])).ToUpperInvariant();
        p = new string([.. p.Where(char.IsAsciiLetterOrDigit)]);
        return p.Length == 0 ? "RET" : p.Length > 8 ? p[..8] : p;
    }

    public static string Number(string prefix, string kind, int index) =>
        kind == DocumentKinds.Drawing ? $"{prefix}-DWG-001" : $"{prefix}-DOC-{index:000}";
}
