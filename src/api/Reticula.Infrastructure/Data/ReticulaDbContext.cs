using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Identity;

namespace Reticula.Infrastructure.Data;

public sealed class ReticulaDbContext(DbContextOptions<ReticulaDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Project> Projects => Set<Project>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.HasPostgresExtension("postgis");

        b.Entity<AppUser>(e =>
        {
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
    }
}
