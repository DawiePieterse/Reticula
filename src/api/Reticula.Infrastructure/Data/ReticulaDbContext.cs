using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Reticula.Domain.Field;
using Reticula.Domain.Jobs;
using Reticula.Domain.Layout;
using Reticula.Domain.Maps;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Identity;

namespace Reticula.Infrastructure.Data;

public sealed class ReticulaDbContext(DbContextOptions<ReticulaDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<Stand> Stands => Set<Stand>();
    public DbSet<Building> Buildings => Set<Building>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<Inspection> Inspections => Set<Inspection>();
    public DbSet<Photo> Photos => Set<Photo>();
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<LoadPoint> LoadPoints => Set<LoadPoint>();
    public DbSet<Assumption> Assumptions => Set<Assumption>();
    public DbSet<MapPack> MapPacks => Set<MapPack>();
    public DbSet<Road> Roads => Set<Road>();
    public DbSet<Contour> Contours => Set<Contour>();
    public DbSet<NetworkAsset> NetworkAssets => Set<NetworkAsset>();

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

        b.Entity<ImportBatch>(e =>
        {
            e.ToTable("import_batches");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.Format).HasMaxLength(20).IsRequired();
            e.Property(x => x.SourceCrs).HasMaxLength(20);
            e.Property(x => x.CrsReason).HasMaxLength(500);
            e.Property(x => x.IssuesJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.ProjectId, x.CreatedAt });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Stand>(e =>
        {
            e.ToTable("stands");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceRef).HasMaxLength(200).IsRequired();
            e.Property(x => x.ErfNumber).HasMaxLength(50);
            e.Property(x => x.Zoning).HasMaxLength(100);
            e.Property(x => x.Geometry).HasColumnType($"geometry(Polygon,{ProjectRules.Srid})").IsRequired();
            e.Property(x => x.AttributesJson).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => x.Geometry).HasMethod("gist");
            e.HasIndex(x => new { x.ProjectId, x.ErfNumber });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.ImportBatchId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Building>(e =>
        {
            e.ToTable("buildings");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceRef).HasMaxLength(200).IsRequired();
            e.Property(x => x.OsmId).HasMaxLength(50);
            e.Property(x => x.Footprint).HasColumnType($"geometry(Polygon,{ProjectRules.Srid})");
            e.Property(x => x.Location).HasColumnType($"geometry(Point,{ProjectRules.Srid})").IsRequired();
            e.Property(x => x.Version).IsRowVersion();
            e.Ignore(x => x.EffectiveType);
            e.Property(x => x.TagsJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.Zoning).HasMaxLength(100);
            e.Property(x => x.PredictedType).HasMaxLength(20).IsRequired();
            e.Property(x => x.PredictionSource).HasMaxLength(200).IsRequired();
            e.Property(x => x.PredictionSignalsJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.PredictionRulesHash).HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ConfirmedType).HasMaxLength(20);
            e.HasIndex(x => x.Footprint).HasMethod("gist");
            e.HasIndex(x => x.Location).HasMethod("gist");
            e.HasIndex(x => new { x.ProjectId, x.Status, x.PredictedConfidence });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.ImportBatchId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Stand>().WithMany().HasForeignKey(x => x.StandId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Inspection>(e =>
        {
            e.ToTable("inspections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).HasMaxLength(30).IsRequired();
            e.Property(x => x.Value).HasMaxLength(50);
            e.Property(x => x.Position).HasColumnType($"geometry(Point,{ProjectRules.Srid})");
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasIndex(x => new { x.ProjectId, x.CapturedAt });
            e.HasIndex(x => x.BuildingId);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Candidate>().WithMany().HasForeignKey(x => x.CandidateId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.InspectorId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Photo>(e =>
        {
            e.ToTable("photos");
            e.HasKey(x => x.Id);
            e.Property(x => x.ContentType).HasMaxLength(50).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.StorageKey).HasMaxLength(300).IsRequired();
            e.HasIndex(x => x.BuildingId);
            e.HasIndex(x => x.CandidateId);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Candidate>().WithMany().HasForeignKey(x => x.CandidateId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Inspection>().WithMany().HasForeignKey(x => x.InspectionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UploadedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Candidate>(e =>
        {
            e.ToTable("candidates");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.Geometry).HasColumnType($"geometry(Geometry,{ProjectRules.Srid})").IsRequired();
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => x.Geometry).HasMethod("gist");
            e.HasIndex(x => new { x.ProjectId, x.Kind });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<LoadPoint>(e =>
        {
            e.ToTable("load_points");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.BuildingId).IsUnique();
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.SpecialLoad).HasMaxLength(50);
            e.Property(x => x.ObservationsJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.IncomeBand).HasMaxLength(50);
            e.Property(x => x.ClassOverride).HasMaxLength(50);
            e.Property(x => x.Category).HasMaxLength(50);
            e.Property(x => x.OverrideReason).HasMaxLength(1000);
            e.Property(x => x.MissingJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.TraceJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.RulesHash).HasMaxLength(16).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Version).IsRowVersion();
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Building>().WithMany().HasForeignKey(x => x.BuildingId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Road>(e =>
        {
            e.ToTable("roads");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceRef).HasMaxLength(200).IsRequired();
            e.Property(x => x.OsmId).HasMaxLength(50);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.RoadClass).HasMaxLength(50);
            e.Property(x => x.Geometry).HasColumnType($"geometry(LineString,{ProjectRules.Srid})").IsRequired();
            e.Property(x => x.AttributesJson).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => x.Geometry).HasMethod("gist");
            e.HasIndex(x => x.ProjectId);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.ImportBatchId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Contour>(e =>
        {
            e.ToTable("contours");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceRef).HasMaxLength(200).IsRequired();
            e.Property(x => x.Geometry).HasColumnType($"geometry(LineString,{ProjectRules.Srid})").IsRequired();
            e.HasIndex(x => x.Geometry).HasMethod("gist");
            e.HasIndex(x => x.ProjectId);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.ImportBatchId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<NetworkAsset>(e =>
        {
            e.ToTable("network_assets");
            e.HasKey(x => x.Id);
            e.Property(x => x.SourceRef).HasMaxLength(200).IsRequired();
            e.Property(x => x.AssetType).HasMaxLength(30).IsRequired();
            e.Property(x => x.Label).HasMaxLength(100);
            e.Property(x => x.Geometry).HasColumnType($"geometry(Geometry,{ProjectRules.Srid})").IsRequired();
            e.Property(x => x.MissingJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.AttributesJson).HasColumnType("jsonb").IsRequired();
            e.HasIndex(x => x.Geometry).HasMethod("gist");
            e.HasIndex(x => new { x.ProjectId, x.AssetType });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ImportBatch>().WithMany().HasForeignKey(x => x.ImportBatchId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<MapPack>(e =>
        {
            e.ToTable("map_packs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ProjectId).IsUnique();
            e.Property(x => x.StorageKey).HasMaxLength(300).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.Source).HasMaxLength(300).IsRequired();
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.BuiltBy).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Assumption>(e =>
        {
            e.ToTable("assumptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.SubjectType).HasMaxLength(30).IsRequired();
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Text).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ClearNote).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.ProjectId, x.SubjectType, x.SubjectId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.Status });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
