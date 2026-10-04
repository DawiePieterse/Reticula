using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Field;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Documents;

namespace Reticula.Infrastructure.Review;

/// <summary>
/// The assumptions register (plan 7.1). Besides the load estimates raised in the field, the latest design runs raise
/// assumptions of their own: rules values not yet verified, warnings from the layout (unconnected routes, buildings
/// not inspected, a transformer moved onto a route, an assumed supply point), and the bulk study's modelling
/// assumptions. They are kept as register rows so the engineer can clear or accept each one; a row no longer raised by
/// the latest runs is withdrawn. Sign-off needs no open row.
/// </summary>
public sealed class AssumptionRegister(ReticulaDbContext db, TimeProvider time)
{
    public const string DesignSubject = "design";

    public async Task SyncAsync(Guid projectId, CancellationToken ct)
    {
        // One sync per project at a time (pages ask for the register and the readiness together).
        var owned = db.Database.CurrentTransaction is null;
        await using var tx = owned ? await db.Database.BeginTransactionAsync(ct) : null;
        var lockKey = BitConverter.ToInt64(projectId.ToByteArray(), 0);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);
        var sources = await DocumentSourceSet.CurrentAsync(db, projectId, ct);
        var ids = sources.Lv.Values.Select(r => r.RunId).Concat(new[] { sources.Mv, sources.Bulk }.OfType<SourceRun>().Select(r => r.RunId)).ToList();
        var runs = await db.DesignRuns.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var sites = await db.Candidates.AsNoTracking().Where(c => c.ProjectId == projectId).ToDictionaryAsync(c => c.Id.ToString(), c => c.Notes, ct);
        var desired = new Dictionary<(Guid Subject, string Code), string>();
        var unverified = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (siteId, src) in sources.Lv)
        {
            using var doc = JsonDocument.Parse(runs[src.RunId].ResultJson ?? "{}");
            var subject = Guid.TryParse(siteId, out var g) ? g : projectId;
            var label = sites.TryGetValue(siteId, out var notes) && !string.IsNullOrWhiteSpace(notes) ? notes : "LV site";
            Warnings(doc.RootElement, "lv", subject, label, desired);
            Unverified(doc.RootElement, unverified);
        }
        if (sources.Mv is { } mv && runs.TryGetValue(mv.RunId, out var mvRun))
        {
            using var doc = JsonDocument.Parse(mvRun.ResultJson ?? "{}");
            Warnings(doc.RootElement, "mv", projectId, "MV design", desired);
            Unverified(doc.RootElement, unverified);
        }
        if (sources.Bulk is { } bulk && runs.TryGetValue(bulk.RunId, out var bulkRun))
        {
            using var doc = JsonDocument.Parse(bulkRun.ResultJson ?? "{}");
            if (doc.RootElement.TryGetProperty("assumptions", out var list))
                foreach (var a in list.EnumerateArray())
                    desired[(projectId, $"bulk:{Short(a.GetString()!)}")] = $"Bulk supply study: {a.GetString()}";
            Unverified(doc.RootElement, unverified);
        }
        foreach (var section in unverified)
            desired[(projectId, Code($"rules:{section}"))] = $"Rules values not yet verified against the current standards: {section} (rules {sources.RulesRef}).";

        var now = time.GetUtcNow();
        var existing = await db.Assumptions.Where(a => a.ProjectId == projectId && a.SubjectType == DesignSubject).ToListAsync(ct);
        var byKey = existing.ToDictionary(a => (a.SubjectId, a.Code));
        foreach (var ((subject, code), text) in desired)
        {
            if (!byKey.TryGetValue((subject, code), out var row))
                db.Assumptions.Add(new Assumption(Guid.CreateVersion7(), projectId, DesignSubject, subject, code, Trim(text), now));
            else if (row.Status == AssumptionStatus.Withdrawn || row.Text != Trim(text))
                row.Reopen(Trim(text), now);
        }
        foreach (var row in existing.Where(r => r.Status is AssumptionStatus.Open or AssumptionStatus.Accepted && !desired.ContainsKey((r.SubjectId, r.Code))))
            row.Withdraw(now);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);
    }

    private static void Warnings(JsonElement result, string source, Guid subject, string label, Dictionary<(Guid, string), string> desired)
    {
        if (!result.TryGetProperty("issues", out var issues)) return;
        foreach (var i in issues.EnumerateArray().Where(i => i.GetProperty("severity").GetString() == "warning"))
        {
            var samples = i.TryGetProperty("samples", out var s) && s.GetArrayLength() > 0 ? $" (e.g. {string.Join(", ", s.EnumerateArray().Take(5).Select(x => x.GetString()))})" : "";
            desired[(subject, Code($"{source}:{i.GetProperty("code").GetString()}"))] = $"{label}: {i.GetProperty("message").GetString()}{samples}";
        }
    }

    private static void Unverified(JsonElement result, SortedSet<string> into)
    {
        if (result.TryGetProperty("unverified", out var u))
            foreach (var x in u.EnumerateArray()) into.Add(x.GetString()!);
    }

    private static string Short(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    private static string Code(string code) => code.Length <= 50 ? code : code[..37] + Short(code);

    private static string Trim(string text) => text.Length <= 1000 ? text : text[..999] + "…";
}
