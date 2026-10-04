using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Jobs;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Identity;

namespace Reticula.Infrastructure.Data;

public sealed class ReticulaDbContext(DbContextOptions<ReticulaDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasPostgresExtension("postgis");

        // Identity's default table names are PascalCase; keep the schema consistently snake_case.
        b.Entity<IdentityRole<Guid>>().ToTable("roles");
        b.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        b.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        b.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        b.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
        b.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");

        b.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(u => u.RegistrationNo).HasMaxLength(50);
        });

        b.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).HasMaxLength(ProjectRules.NameMaxLength).IsRequired();
            e.Property(p => p.RulesRef).HasMaxLength(100).IsRequired();
            e.Ignore(p => p.Authority);
            e.Property(p => p.Area).HasColumnType($"geometry(Polygon,{ProjectRules.Srid})").IsRequired();
            e.HasIndex(p => p.Area).HasMethod("gist");
            e.Property(p => p.Version).IsRowVersion();
            e.HasIndex(p => p.ArchivedAt);
            e.HasOne<AppUser>().WithMany().HasForeignKey(p => p.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<JobRun>(e =>
        {
            e.ToTable("job_runs");
            e.HasKey(j => j.Id);
            e.Property(j => j.Kind).HasMaxLength(100).IsRequired();
            e.Property(j => j.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(j => j.Message).HasMaxLength(500);
            e.Property(j => j.PayloadJson).HasColumnType("jsonb").IsRequired();
            e.Property(j => j.ResultJson).HasColumnType("jsonb");
            e.Property(j => j.BackgroundJobId).HasMaxLength(100);
            e.Ignore(j => j.IsFinished);
            e.HasIndex(j => new { j.ProjectId, j.CreatedAt });
            e.HasIndex(j => j.Status);
            e.HasOne<Project>().WithMany().HasForeignKey(j => j.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AppUser>().WithMany().HasForeignKey(j => j.RequestedBy).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
