using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Projects;

namespace Reticula.Infrastructure.Data;

/// <summary>Archived projects are invisible everywhere: these are the only way to look a project up by id.</summary>
public static class ProjectQueries
{
    /// <summary>The project, read-only, or null when it does not exist or is archived.</summary>
    public static Task<Project?> ActiveAsync(this DbSet<Project> projects, Guid id, CancellationToken ct) =>
        projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.ArchivedAt == null, ct);

    /// <summary>The project, tracked for update, or null when it does not exist or is archived.</summary>
    public static Task<Project?> ActiveForUpdateAsync(this DbSet<Project> projects, Guid id, CancellationToken ct) =>
        projects.FirstOrDefaultAsync(p => p.Id == id && p.ArchivedAt == null, ct);

    public static Task<bool> AnyActiveAsync(this DbSet<Project> projects, Guid id, CancellationToken ct) =>
        projects.AnyAsync(p => p.Id == id && p.ArchivedAt == null, ct);
}
