using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using Reticula.Domain.Assistant;
using Reticula.Domain.Audit;
using Reticula.Domain.Costs;
using Reticula.Domain.Design;
using Reticula.Domain.Documents;
using Reticula.Domain.Field;
using Reticula.Domain.Layout;
using Reticula.Domain.Projects;
using Reticula.Domain.Review;

namespace Reticula.Infrastructure.Audit;

/// <summary>
/// Who is making changes in this scope: the user who started a background job (set by the job executor), else the
/// signed-in user from the host (the API passes a lookup of the request's user).
/// </summary>
public sealed class AuditActor(Func<Guid?>? signedIn = null)
{
    private Guid? _userId;

    public Guid? UserId
    {
        get => _userId ?? signedIn?.Invoke();
        set => _userId = value;
    }
}

/// <summary>
/// Writes an audit entry for every change to the audited entities in the same SaveChanges (plan 7.4). Bulk imports
/// (new buildings, stands, map features) are audited as their import batch; large stored results are left out.
/// </summary>
public sealed class AuditInterceptor(AuditActor actor, TimeProvider time) : SaveChangesInterceptor
{
    /// <summary>Entity type → audit created rows too (false: only changes and deletions).</summary>
    private static readonly Dictionary<Type, bool> Audited = new()
    {
        [typeof(Project)] = true, [typeof(Candidate)] = true, [typeof(LoadPoint)] = true, [typeof(Building)] = false, [typeof(Inspection)] = true,
        [typeof(Assumption)] = true, [typeof(ConnectionPoint)] = true, [typeof(RateList)] = true, [typeof(DesignRun)] = true, [typeof(ImportBatch)] = true,
        [typeof(DocumentSet)] = true, [typeof(Revision)] = true, [typeof(AssistantDraft)] = true, [typeof(ReportSection)] = true,
    };

    private static readonly HashSet<string> Skipped =
    [
        "Version", "ResultJson", "InputJson", "ContentJson", "TraceJson", "SummaryJson", "SourcesJson", "ChecklistJson", "WarningsJson",
        "PredictionSignalsJson", "TagsJson", "AttributesJson", "SnapshotJson",
    ];

    private const int MaxText = 500;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Write(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Write(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Write(DbContext? db)
    {
        if (db is null) return;
        db.ChangeTracker.DetectChanges();
        var now = time.GetUtcNow();
        var entries = new List<AuditEntry>();
        foreach (var e in db.ChangeTracker.Entries().ToList())
        {
            if (!Audited.TryGetValue(e.Metadata.ClrType, out var auditAdds)) continue;
            if (e.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (e.State == EntityState.Added && !auditAdds) continue;
            var changes = new JsonObject();
            foreach (var p in e.Properties)
            {
                var name = p.Metadata.Name;
                if (Skipped.Contains(name) || p.Metadata.IsShadowProperty()) continue;
                switch (e.State)
                {
                    case EntityState.Added:
                        if (p.CurrentValue is not null) changes[name] = new JsonArray(null, Value(p.CurrentValue));
                        break;
                    case EntityState.Deleted:
                        changes[name] = new JsonArray(Value(p.OriginalValue), null);
                        break;
                    default:
                        if (p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)) changes[name] = new JsonArray(Value(p.OriginalValue), Value(p.CurrentValue));
                        break;
                }
            }
            if (e.State == EntityState.Modified && changes.Count == 0) continue;
            var key = e.Metadata.FindPrimaryKey()!.Properties.Select(k => e.Property(k.Name).CurrentValue?.ToString()).FirstOrDefault() ?? "";
            var projectId = e.Metadata.ClrType == typeof(Project) ? (Guid?)e.Property("Id").CurrentValue
                : e.Metadata.FindProperty("ProjectId") is not null ? e.Property("ProjectId").CurrentValue as Guid? : null;
            var action = e.State switch { EntityState.Added => "created", EntityState.Deleted => "deleted", _ => "updated" };
            entries.Add(new AuditEntry(Guid.CreateVersion7(), projectId, actor.UserId, e.Metadata.ClrType.Name, key, action, changes.ToJsonString(), now));
        }
        if (entries.Count > 0) db.Set<AuditEntry>().AddRange(entries);
    }

    private static JsonNode? Value(object? v) => v switch
    {
        null => null,
        Geometry g => Truncate(g.AsText()),
        string s => Truncate(s),
        DateTimeOffset d => d.ToString("O"),
        DateOnly d => d.ToString("yyyy-MM-dd"),
        Enum en => en.ToString(),
        bool b => b,
        int or long or double or float or decimal or uint => JsonValue.Create(Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)),
        _ => Truncate(v.ToString() ?? ""),
    };

    private static string Truncate(string s) => s.Length <= MaxText ? s : s[..MaxText] + "…";
}
