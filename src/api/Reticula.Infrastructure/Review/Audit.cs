using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using Reticula.Domain.Design;
using Reticula.Domain.Jobs;
using Reticula.Domain.Layout;
using Reticula.Domain.Maps;
using Reticula.Domain.Projects;
using Reticula.Domain.Review;

namespace Reticula.Infrastructure.Review;

/// <summary>Who is making the current changes: the signed-in user, or the user who started the background job. Scoped.</summary>
public sealed class AuditActor
{
    public Guid? UserId { get; set; }
}

/// <summary>
/// The audit trail (plan 7.4): every add, change and removal of a project's records is written as an <see cref="AuditEntry"/> in the
/// same save, with who, when and the values before and after. Bulk set-based statements (ExecuteUpdate/ExecuteDelete) bypass it; they
/// are used only to replace derived records (a rebuilt LV network), which are not audited.
/// </summary>
public sealed class AuditInterceptor(AuditActor actor, TimeProvider time) : SaveChangesInterceptor
{
    /// <summary>Derived or bulk-imported records: their import batch or design run is audited instead.</summary>
    private static readonly HashSet<Type> Skipped =
    [
        typeof(AuditEntry), typeof(JobRun), typeof(LvNode), typeof(LvBranch), typeof(LvLoad), typeof(LvNetwork), typeof(Placement),
        typeof(Stand), typeof(Road), typeof(Contour), typeof(NetworkAsset), typeof(MapPack),
    ];

    /// <summary>Imported in bulk, but a change from the field is worth recording.</summary>
    private static readonly HashSet<Type> ChangesOnly = [typeof(Building)];

    private const int MaxValue = 2000;

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } db) Record(db);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } db) Record(db);
        return base.SavingChanges(eventData, result);
    }

    private void Record(DbContext db)
    {
        db.ChangeTracker.DetectChanges();
        var now = time.GetUtcNow();
        var entries = new List<AuditEntry>();
        foreach (var e in db.ChangeTracker.Entries())
        {
            var type = e.Entity.GetType();
            if (type.Namespace?.StartsWith("Reticula.Domain", StringComparison.Ordinal) != true || Skipped.Contains(type)) continue;
            if (e.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (e.State == EntityState.Added && ChangesOnly.Contains(type)) continue;
            var props = e.Properties.Where(p => p.Metadata.Name != "Version").ToList();
            string? before = null, after = null;
            switch (e.State)
            {
                case EntityState.Added:
                    after = Serialize(props.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue));
                    break;
                case EntityState.Deleted:
                    before = Serialize(props.ToDictionary(p => p.Metadata.Name, p => p.OriginalValue));
                    break;
                default:
                    var changed = props.Where(p => p.IsModified && !Equals(p.OriginalValue, p.CurrentValue)).ToList();
                    if (changed.Count == 0) continue;
                    before = Serialize(changed.ToDictionary(p => p.Metadata.Name, p => p.OriginalValue));
                    after = Serialize(changed.ToDictionary(p => p.Metadata.Name, p => p.CurrentValue));
                    break;
            }
            entries.Add(new AuditEntry(Guid.CreateVersion7(), now, actor.UserId, ProjectOf(e), type.Name, Key(e),
                e.State.ToString().ToLowerInvariant(), before, after));
        }
        if (entries.Count > 0) db.Set<AuditEntry>().AddRange(entries);
    }

    private static Guid? ProjectOf(EntityEntry e) => e.Entity switch
    {
        Project p => p.Id,
        _ => e.Properties.FirstOrDefault(p => p.Metadata.Name == "ProjectId")?.CurrentValue as Guid?,
    };

    private static string Key(EntityEntry e) =>
        string.Join(",", e.Metadata.FindPrimaryKey()?.Properties.Select(p => e.Property(p.Name).CurrentValue?.ToString()) ?? []);

    private static string Serialize(Dictionary<string, object?> values) =>
        JsonSerializer.Serialize(values.ToDictionary(kv => kv.Key, kv => Value(kv.Value)));

    private static object? Value(object? v) => v switch
    {
        null => null,
        Geometry g => Cap(g.AsText()),
        string s => Cap(s),
        DateTimeOffset d => d.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        Guid or bool or Enum => v.ToString(),
        _ when v.GetType().IsPrimitive || v is decimal => v,
        _ => Cap(v.ToString() ?? ""),
    };

    private static string Cap(string s) => s.Length <= MaxValue ? s : $"{s[..MaxValue]}… ({s.Length} characters)";
}
