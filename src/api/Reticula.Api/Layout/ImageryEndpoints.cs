using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reticula.Api.Jobs;
using Reticula.Domain.Auth;
using Reticula.Domain.Layout;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;
using Reticula.Infrastructure.Layout;

namespace Reticula.Api.Layout;

public sealed record ImageryDto(Guid Id, string Source, string Format, string Label, string Licence, string Status, bool Active, long SizeBytes, string? Error,
    JsonElement? Model, DateTimeOffset? ClassifiedAt, DateTimeOffset CreatedAt);

public sealed record GoogleImageryInfo(bool Available, string? Licence, int Zoom);

public sealed record ImageryIndex(IReadOnlyList<ImageryDto> Items, GoogleImageryInfo Google);

public sealed record StartedImagery(ImageryDto Imagery, JobDto Job);

/// <summary>Rooftop imagery and the rooftop classifier (plan 1.3).</summary>
public static class ImageryEndpoints
{
    public const long MaxOrthophotoBytes = 600L * 1024 * 1024;

    public static IEndpointRouteBuilder MapImageryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}/imagery").WithTags("Layout").RequireAuthorization(Policies.FieldUser);
        g.MapGet("/", List);
        g.MapPost("/orthophoto", Upload).RequireAuthorization(Policies.Engineer).DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxOrthophotoBytes + 1024 * 1024))
            .WithFormOptions(multipartBodyLengthLimit: MaxOrthophotoBytes + 1024 * 1024);
        g.MapPost("/google", FetchGoogle).RequireAuthorization(Policies.Engineer);
        g.MapPost("/{imageryId:guid}/activate", Activate).RequireAuthorization(Policies.Engineer);
        g.MapPost("/classify", Classify).RequireAuthorization(Policies.Engineer);
        return app;
    }

    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task<Results<Ok<ImageryIndex>, NotFound>> List(Guid projectId, ReticulaDbContext db, GoogleImageryOptions google, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        var items = await db.ProjectImagery.AsNoTracking().Where(i => i.ProjectId == projectId).OrderByDescending(i => i.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(new ImageryIndex([.. items.Select(ToDto)], new GoogleImageryInfo(google.IsOn, google.IsOn ? google.LicenceReference : null, google.Zoom)));
    }

    /// <summary>An orthophoto GeoTIFF with its source and licence; the engineer declares the licence allows deriving data from it.</summary>
    private static async Task<Results<Created<ImageryDto>, NotFound, ValidationProblem>> Upload(Guid projectId, IFormFile file, [FromForm] string label,
        [FromForm] string licence, [FromForm] bool declaration, ReticulaDbContext db, IFileStore files, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        var errors = new Dictionary<string, string[]>();
        if (file.Length is 0 or > MaxOrthophotoBytes) errors["file"] = ["Upload an orthophoto GeoTIFF of up to 600 MB (crop it to the project)."];
        if (string.IsNullOrWhiteSpace(label) || label.Length > 80) errors["label"] = ["Name the imagery, e.g. \"NGI 2023 0.25 m\" (up to 80 characters)."];
        if (string.IsNullOrWhiteSpace(licence) || licence.Trim().Length < 5 || licence.Length > 500) errors["licence"] = ["State the licence or agreement the imagery is used under."];
        if (!declaration) errors["declaration"] = ["Confirm the licence allows deriving data from the imagery."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
        await using var input = file.OpenReadStream();
        var head = new byte[4];
        var read = await input.ReadAsync(head, ct);
        if (read < 4 || !(head is [0x49, 0x49, 0x2A, 0x00] or [0x4D, 0x4D, 0x00, 0x2A] or [0x49, 0x49, 0x2B, 0x00] or [0x4D, 0x4D, 0x00, 0x2B]))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["The file is not a TIFF."] });
        input.Position = 0;
        var imagery = new ProjectImagery(Guid.CreateVersion7(), projectId, ImagerySources.Orthophoto, "geotiff", label.Trim(), licence.Trim(), UserId(user), time.GetUtcNow());
        var key = $"projects/{projectId:N}/imagery/{imagery.Id:N}.tif";
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
        input.Position = 0;
        await files.SaveAsync(key, input, ct);
        imagery.Stored(key, file.Length, sha);
        await DeactivateOthersAsync(db, projectId, ct);
        db.ProjectImagery.Add(imagery);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/projects/{projectId}/imagery", ToDto(imagery));
    }

    private static async Task<Results<Accepted<StartedImagery>, NotFound, ValidationProblem>> FetchGoogle(Guid projectId, ReticulaDbContext db, GoogleImageryOptions google,
        IJobQueue queue, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        if (!google.IsOn)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["google"] = ["Google imagery is not enabled: it needs an API key and the agreement that allows deriving data from the imagery."],
            });
        await DeactivateOthersAsync(db, projectId, ct);
        var imagery = new ProjectImagery(Guid.CreateVersion7(), projectId, ImagerySources.Google, "tiles", $"Google satellite z{google.Zoom}", google.LicenceReference!,
            UserId(user), time.GetUtcNow());
        db.ProjectImagery.Add(imagery);
        await db.SaveChangesAsync(ct);
        var job = await queue.EnqueueAsync(GoogleImageryJob.JobKind, new { imageryId = imagery.Id }, UserId(user), projectId, ct);
        imagery.Queue(job.Id);
        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted($"/api/projects/{projectId}/imagery", new StartedImagery(ToDto(imagery), JobDto.From(job)));
    }

    private static async Task<Results<Ok<ImageryDto>, NotFound, ValidationProblem>> Activate(Guid projectId, Guid imageryId, ReticulaDbContext db, CancellationToken ct)
    {
        var imagery = await db.ProjectImagery.FirstOrDefaultAsync(i => i.Id == imageryId && i.ProjectId == projectId, ct);
        if (imagery is null) return TypedResults.NotFound();
        if (imagery.Status != ImageryStatus.Ready) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Only ready imagery can be used."] });
        await DeactivateOthersAsync(db, projectId, ct);
        imagery.Activate();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(imagery));
    }

    private static async Task<Results<Accepted<StartedImagery>, NotFound, ValidationProblem>> Classify(Guid projectId, ReticulaDbContext db, IJobQueue queue,
        ClaimsPrincipal user, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId && p.ArchivedAt == null, ct)) return TypedResults.NotFound();
        var imagery = await db.ProjectImagery.FirstOrDefaultAsync(i => i.ProjectId == projectId && i.Active && i.Status == ImageryStatus.Ready, ct);
        if (imagery is null)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["imagery"] = ["Upload an orthophoto or fetch imagery first."] });
        var job = await queue.EnqueueAsync(RooftopClassifyJob.JobKind, new { imageryId = imagery.Id }, UserId(user), projectId, ct);
        return TypedResults.Accepted($"/api/projects/{projectId}/imagery", new StartedImagery(ToDto(imagery), JobDto.From(job)));
    }

    private static async Task DeactivateOthersAsync(ReticulaDbContext db, Guid projectId, CancellationToken ct)
    {
        foreach (var other in await db.ProjectImagery.Where(i => i.ProjectId == projectId && i.Active).ToListAsync(ct)) other.Deactivate();
    }

    private static ImageryDto ToDto(ProjectImagery i) => new(i.Id, i.Source, i.Format, i.Label, i.Licence, i.Status.ToString().ToLowerInvariant(), i.Active, i.SizeBytes, i.Error,
        i.ModelJson is null ? null : JsonDocument.Parse(i.ModelJson).RootElement.Clone(), i.ClassifiedAt, i.CreatedAt);
}
