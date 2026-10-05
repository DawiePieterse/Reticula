using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Reticula.Domain.Auth;
using Reticula.Domain.Field;
using Reticula.Domain.Layout;
using Reticula.Domain.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Field;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Geo;
using Reticula.Api.Layout;

namespace Reticula.Api.Field;

/// <summary>Field inspection, candidates, loads, assumptions, progress and the load schedule.</summary>
public static class FieldEndpoints
{
    private const long MaxPhotoBytes = 15L * 1024 * 1024;
    private static readonly Dictionary<string, string> PhotoTypes = new()
    {
        ["image/jpeg"] = "jpg", ["image/png"] = "png", ["image/webp"] = "webp",
    };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // About 100 m at South African latitudes: GPS fixes just outside the drawn area are still accepted.
    private const double AreaTolerance = 0.001;

    public static IEndpointRouteBuilder MapFieldEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{projectId:guid}").WithTags("Field").RequireAuthorization(Policies.FieldUser);

        g.MapGet("/buildings/{buildingId:guid}", GetBuilding);
        g.MapPut("/buildings/{buildingId:guid}/inspection", InspectBuilding);
        g.MapPost("/buildings/new", AddBuilding);

        g.MapPost("/photos", UploadPhoto).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(MaxPhotoBytes + 1024 * 1024));
        g.MapGet("/photos", ListPhotos);
        g.MapGet("/photos/{photoId:guid}", GetPhoto);

        g.MapGet("/candidates", ListCandidates);
        g.MapPut("/candidates/{candidateId:guid}", SaveCandidate);
        g.MapDelete("/candidates/{candidateId:guid}", ArchiveCandidate);

        g.MapGet("/admd-form", AdmdForm);
        g.MapGet("/load-points", ListLoadPoints);
        g.MapPut("/buildings/{buildingId:guid}/load", SaveLoad);
        g.MapPost("/load-points/{loadPointId:guid}/confirm", ConfirmLoad).RequireAuthorization(Policies.Engineer);

        g.MapGet("/assumptions", ListAssumptions);
        g.MapPost("/assumptions/{assumptionId:guid}/clear", ClearAssumption).RequireAuthorization(Policies.Engineer);

        g.MapGet("/field-progress", Progress);
        g.MapGet("/load-schedule", Schedule);
        g.MapGet("/load-schedule.csv", ScheduleCsv);
        return app;
    }

    // ---------- buildings ----------

    private static async Task<Results<Ok<BuildingFieldDto>, NotFound>> GetBuilding(Guid projectId, Guid buildingId, ReticulaDbContext db, CancellationToken ct)
    {
        var b = await db.Buildings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == buildingId && x.ProjectId == projectId, ct);
        return b is null ? TypedResults.NotFound() : TypedResults.Ok(await DtoAsync(db, b, ct));
    }

    private static async Task<Results<Ok<BuildingFieldDto>, NotFound, ValidationProblem, Conflict<BuildingFieldDto>>> InspectBuilding(
        Guid projectId, Guid buildingId, BuildingInspectionRequest req, ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var building = await db.Buildings.FirstOrDefaultAsync(b => b.Id == buildingId && b.ProjectId == projectId, ct);
        if (building is null) return TypedResults.NotFound();

        // A record synced twice is applied once.
        if (await db.Inspections.AnyAsync(i => i.Id == req.InspectionId, ct))
            return TypedResults.Ok(await DtoAsync(db, building, ct));

        var errors = new Dictionary<string, string[]>();
        if (!InspectionActions.BuildingActions.Contains(req.Action))
            errors["action"] = [$"Action must be one of: {string.Join(", ", InspectionActions.BuildingActions)}."];
        var type = req.Action == InspectionActions.Confirm ? req.Type ?? building.PredictedType : req.Type;
        if (req.Action is InspectionActions.Confirm or InspectionActions.Correct && (type is null || !BuildingTypes.All.Contains(type)))
            errors["type"] = [$"Type must be one of: {string.Join(", ", BuildingTypes.All)}."];
        if (req.Version is null) errors["version"] = ["Send the building version you saw."];
        var position = ToPoint(req.Position, errors);
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        if (req.Version != building.Version) return TypedResults.Conflict(await DtoAsync(db, building, ct));

        var userId = UserId(user);
        var now = time.GetUtcNow();
        if (req.Action == InspectionActions.NotPresent) building.MarkNotPresent(userId, now);
        else building.Confirm(type!, userId, now);
        db.Inspections.Add(new Inspection(req.InspectionId, projectId, req.Action, building.Id, null, type, position,
            req.Position?.AccuracyM, req.CapturedAt, Trim(req.Notes, 2000), userId, now));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var current = await db.Buildings.AsNoTracking().FirstAsync(b => b.Id == buildingId, ct);
            return TypedResults.Conflict(await DtoAsync(db, current, ct));
        }
        return TypedResults.Ok(await DtoAsync(db, building, ct));
    }

    private static async Task<Results<Created<BuildingFieldDto>, Ok<BuildingFieldDto>, NotFound, ValidationProblem>> AddBuilding(
        Guid projectId, NewBuildingRequest req, ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await ProjectAsync(db, projectId, ct);
        if (project is null) return TypedResults.NotFound();

        var existing = await db.Buildings.FirstOrDefaultAsync(b => b.Id == req.Id, ct);
        if (existing is not null)
            return existing.ProjectId == projectId ? TypedResults.Ok(await DtoAsync(db, existing, ct)) : TypedResults.NotFound();

        var errors = new Dictionary<string, string[]>();
        if (!BuildingTypes.All.Contains(req.Type)) errors["type"] = [$"Type must be one of: {string.Join(", ", BuildingTypes.All)}."];
        var position = ToPoint(req.Position, errors, required: true);
        if (position is not null && !InArea(project, position)) errors["position"] = ["The position is outside the project area."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var userId = UserId(user);
        var now = time.GetUtcNow();
        var building = Building.CreateNew(req.Id, projectId, position!, req.Type, userId, now);
        db.Buildings.Add(building);
        db.Inspections.Add(new Inspection(req.InspectionId, projectId, InspectionActions.NewBuilding, building.Id, null, req.Type,
            position, req.Position.AccuracyM, req.CapturedAt, Trim(req.Notes, 2000), userId, now));
        await db.SaveChangesAsync(ct);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE buildings b SET stand_id = s.id, zoning = s.zoning
            FROM stands s WHERE b.id = {building.Id} AND s.project_id = b.project_id AND ST_Contains(s.geometry, b.location)
            """, ct);
        db.ChangeTracker.Clear();
        var saved = await db.Buildings.AsNoTracking().FirstAsync(b => b.Id == building.Id, ct);
        return TypedResults.Created($"/api/projects/{projectId}/buildings/{building.Id}", await DtoAsync(db, saved, ct));
    }

    // ---------- photos ----------

    private static async Task<Results<Created<PhotoDto>, Ok<PhotoDto>, NotFound, ValidationProblem>> UploadPhoto(
        Guid projectId, IFormFile file, [FromForm] Guid id, [FromForm] Guid? buildingId, [FromForm] Guid? candidateId,
        [FromForm] Guid? inspectionId, [FromForm] DateTimeOffset capturedAt,
        ReticulaDbContext db, IFileStore store, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        if (await ProjectAsync(db, projectId, ct) is null) return TypedResults.NotFound();
        var existing = await db.Photos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (existing is not null) return TypedResults.Ok(ToDto(existing));

        var errors = new Dictionary<string, string[]>();
        if (!PhotoTypes.TryGetValue(file.ContentType, out var ext)) errors["file"] = ["Photos must be JPEG, PNG or WebP."];
        if (file.Length is 0 or > MaxPhotoBytes) errors["file"] = ["Photos must be between 1 byte and 15 MB."];
        if (buildingId is null && candidateId is null) errors["subject"] = ["Attach the photo to a building or a candidate."];
        if (buildingId is not null && !await db.Buildings.AnyAsync(b => b.Id == buildingId && b.ProjectId == projectId, ct))
            errors["buildingId"] = ["Unknown building."];
        if (candidateId is not null && !await db.Candidates.AnyAsync(c => c.Id == candidateId && c.ProjectId == projectId, ct))
            errors["candidateId"] = ["Unknown candidate."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var sha = Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
        var key = $"projects/{projectId:N}/photos/{id:N}.{ext}";
        ms.Position = 0;
        await store.SaveAsync(key, ms, ct);

        var photo = new Photo(id, projectId, buildingId, candidateId, inspectionId, file.ContentType, file.Length, sha, key,
            capturedAt, UserId(user), time.GetUtcNow());
        db.Photos.Add(photo);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/projects/{projectId}/photos/{id}", ToDto(photo));
    }

    private static async Task<Ok<List<PhotoDto>>> ListPhotos(Guid projectId, Guid? buildingId, Guid? candidateId, ReticulaDbContext db, CancellationToken ct)
    {
        var q = db.Photos.AsNoTracking().Where(p => p.ProjectId == projectId);
        if (buildingId is not null) q = q.Where(p => p.BuildingId == buildingId);
        if (candidateId is not null) q = q.Where(p => p.CandidateId == candidateId);
        return TypedResults.Ok((await q.OrderBy(p => p.CapturedAt).ToListAsync(ct)).Select(ToDto).ToList());
    }

    private static async Task<Results<FileStreamHttpResult, NotFound>> GetPhoto(Guid projectId, Guid photoId, ReticulaDbContext db, IFileStore store, CancellationToken ct)
    {
        var photo = await db.Photos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == photoId && p.ProjectId == projectId, ct);
        if (photo is null) return TypedResults.NotFound();
        var stream = await store.OpenReadAsync(photo.StorageKey, ct);
        return stream is null ? TypedResults.NotFound() : TypedResults.File(stream, photo.ContentType);
    }

    // ---------- candidates ----------

    private static async Task<Ok<GeoFeatureCollection<CandidateProps>>> ListCandidates(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        var rows = await db.Candidates.AsNoTracking().Where(c => c.ProjectId == projectId && c.ArchivedAt == null).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(GeoFeatureCollection<CandidateProps>.Of(rows.Select(c =>
            new GeoFeature<CandidateProps>("Feature", c.Id.ToString(), GeometryInput.ToDto(c.Geometry), new CandidateProps(c.Kind, c.Notes, c.CreatedAt, c.Version)))));
    }

    private static async Task<Results<Created<GeoFeature<CandidateProps>>, Ok<GeoFeature<CandidateProps>>, NotFound, ValidationProblem, Conflict<GeoFeature<CandidateProps>>>> SaveCandidate(
        Guid projectId, Guid candidateId, CandidateRequest req, ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await ProjectAsync(db, projectId, ct);
        if (project is null) return TypedResults.NotFound();

        // A change synced twice is applied once.
        if (req.OpId is { } opId && await db.Inspections.AnyAsync(i => i.Id == opId && i.ProjectId == projectId, ct))
        {
            var current = await db.Candidates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == candidateId && c.ProjectId == projectId && c.ArchivedAt == null, ct);
            return current is null ? TypedResults.NotFound() : TypedResults.Ok(Feature(current));
        }

        var errors = new Dictionary<string, string[]>();
        if (!CandidateKinds.All.Contains(req.Kind)) errors["kind"] = [$"Kind must be one of: {string.Join(", ", CandidateKinds.All)}."];
        if (!req.Geometry.TryToGeometry(out var geometry, out var geomError)) errors["geometry"] = [geomError!];
        else if (CandidateKinds.IsRoute(req.Kind) != geometry is LineString)
            errors["geometry"] = [CandidateKinds.IsRoute(req.Kind) ? "Routes must be lines." : "Sites must be points."];
        else if (!InArea(project, geometry!)) errors["geometry"] = ["The candidate is outside the project area."];
        var position = ToPoint(req.Position, errors);
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var userId = UserId(user);
        var now = time.GetUtcNow();
        var candidate = await db.Candidates.FirstOrDefaultAsync(c => c.Id == candidateId, ct);
        var created = candidate is null;
        if (candidate is null)
        {
            candidate = new Candidate(candidateId, projectId, req.Kind, geometry!, Trim(req.Notes, 2000), userId, now);
            db.Candidates.Add(candidate);
        }
        else
        {
            if (candidate.ProjectId != projectId || candidate.ArchivedAt is not null) return TypedResults.NotFound();
            if (req.Version != candidate.Version) return TypedResults.Conflict(Feature(candidate));
            candidate.Update(geometry!, Trim(req.Notes, 2000), now);
        }
        db.Inspections.Add(new Inspection(req.OpId ?? Guid.CreateVersion7(), projectId, InspectionActions.Candidate, null, candidateId, req.Kind,
            position, req.Position?.AccuracyM, req.CapturedAt ?? now, null, userId, now));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return TypedResults.Conflict(Feature(await db.Candidates.AsNoTracking().FirstAsync(c => c.Id == candidateId, ct)));
        }
        return created ? TypedResults.Created($"/api/projects/{projectId}/candidates/{candidateId}", Feature(candidate)) : TypedResults.Ok(Feature(candidate));
    }

    private static async Task<Results<NoContent, NotFound>> ArchiveCandidate(Guid projectId, Guid candidateId, ReticulaDbContext db, TimeProvider time, CancellationToken ct)
    {
        var c = await db.Candidates.FirstOrDefaultAsync(x => x.Id == candidateId && x.ProjectId == projectId, ct);
        if (c is null) return TypedResults.NotFound();
        // Removing twice (a synced change sent again) is not an error.
        if (c.ArchivedAt is not null) return TypedResults.NoContent();
        c.Archive(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------- loads ----------

    private static async Task<Results<Ok<JsonElement>, NotFound>> AdmdForm(Guid projectId, ReticulaDbContext db, ICalcClient calc, CancellationToken ct)
    {
        var project = await ProjectAsync(db, projectId, ct);
        return project is null ? TypedResults.NotFound() : TypedResults.Ok(await calc.GetAdmdFormAsync(project.RulesRef, ct));
    }

    private static async Task<Ok<List<LoadPointDto>>> ListLoadPoints(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        var rows = await db.LoadPoints.AsNoTracking().Where(l => l.ProjectId == projectId).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<LoadPointDto>, NotFound, ValidationProblem, Conflict<LoadPointDto>>> SaveLoad(
        Guid projectId, Guid buildingId, LoadRequest req, ReticulaDbContext db, FieldService field, ClaimsPrincipal user, CancellationToken ct)
    {
        var project = await ProjectAsync(db, projectId, ct);
        var building = await db.Buildings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == buildingId && b.ProjectId == projectId, ct);
        if (project is null || building is null) return TypedResults.NotFound();

        var existing = await db.LoadPoints.AsNoTracking().FirstOrDefaultAsync(l => l.BuildingId == buildingId, ct);
        // A change synced twice is applied once.
        if (req.OpId is { } opId && await db.Inspections.AnyAsync(i => i.Id == opId && i.ProjectId == projectId, ct))
            return existing is null ? TypedResults.NotFound() : TypedResults.Ok(ToDto(existing));
        // A save made without seeing the current load (none, or another version) must not overwrite it.
        if (existing is not null && req.Version != existing.Version) return TypedResults.Conflict(ToDto(existing));

        var errors = new Dictionary<string, string[]>();
        if (req.Kind is not (LoadKinds.Residential or LoadKinds.Special)) errors["kind"] = ["Kind must be residential or special."];
        if (req.Kind == LoadKinds.Special && string.IsNullOrWhiteSpace(req.SpecialLoad)) errors["specialLoad"] = ["Choose the special load."];
        if (req.OverrideKva is not null && string.IsNullOrWhiteSpace(req.OverrideReason)) errors["overrideReason"] = ["Give a reason for the override."];
        if (req.OverrideKva is <= 0) errors["overrideKva"] = ["Override must be more than 0 kVA."];
        if (building.Status == BuildingStatus.NotPresent) errors["building"] = ["The building was marked not present."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        var erf = building.StandId is null ? null : await db.Stands.Where(s => s.Id == building.StandId).Select(s => s.ErfNumber).FirstOrDefaultAsync(ct);
        try
        {
            var lp = await field.EstimateLoadAsync(project, building,
                new AdmdEstimateRequest(project.RulesRef, req.Kind, req.Observations ?? [], req.SpecialLoad, req.OverrideKva, Trim(req.OverrideReason, 1000),
                    req.Kind == LoadKinds.Residential && !string.IsNullOrWhiteSpace(req.LoadClass) ? req.LoadClass : null),
                erf, req.Version, req.OpId, req.CapturedAt, UserId(user), ct);
            return TypedResults.Ok(ToDto(lp));
        }
        catch (CalcRejectedException e)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["observations"] = [e.Message] });
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return TypedResults.Conflict(ToDto(await db.LoadPoints.AsNoTracking().FirstAsync(l => l.BuildingId == buildingId, ct)));
        }
    }

    private static async Task<Results<Ok<LoadPointDto>, NotFound>> ConfirmLoad(Guid projectId, Guid loadPointId, ReticulaDbContext db, FieldService field, ClaimsPrincipal user, CancellationToken ct)
    {
        var lp = await db.LoadPoints.FirstOrDefaultAsync(l => l.Id == loadPointId && l.ProjectId == projectId, ct);
        if (lp is null) return TypedResults.NotFound();
        await field.ConfirmLoadAsync(lp, UserId(user), ct);
        return TypedResults.Ok(ToDto(lp));
    }

    // ---------- assumptions ----------

    private static async Task<Ok<List<AssumptionDto>>> ListAssumptions(Guid projectId, string? status, ReticulaDbContext db, CancellationToken ct)
    {
        var q = db.Assumptions.AsNoTracking().Where(a => a.ProjectId == projectId);
        if (Enum.TryParse<AssumptionStatus>(status, ignoreCase: true, out var s)) q = q.Where(a => a.Status == s);
        var rows = await q.OrderBy(a => a.Status).ThenBy(a => a.CreatedAt).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(a => new AssumptionDto(a.Id, a.SubjectType, a.SubjectId, a.Code, a.Text,
            a.Status.ToString().ToLowerInvariant(), a.CreatedAt, a.ClearedAt, a.ClearNote)).ToList());
    }

    private static async Task<Results<NoContent, NotFound>> ClearAssumption(Guid projectId, Guid assumptionId, ClearAssumptionRequest req,
        ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var a = await db.Assumptions.FirstOrDefaultAsync(x => x.Id == assumptionId && x.ProjectId == projectId, ct);
        if (a is null) return TypedResults.NotFound();
        a.Clear(UserId(user), Trim(req.Note, 1000), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    // ---------- progress and schedule ----------

    private static async Task<Results<Ok<FieldProgress>, NotFound>> Progress(Guid projectId, ReticulaDbContext db, CancellationToken ct)
    {
        if (await ProjectAsync(db, projectId, ct) is null) return TypedResults.NotFound();
        var b = db.Buildings.Where(x => x.ProjectId == projectId);
        var present = b.Where(x => x.Status != BuildingStatus.NotPresent);
        var withLoad = db.LoadPoints.Where(l => l.ProjectId == projectId).Select(l => l.BuildingId);
        var candidates = await db.Candidates.Where(c => c.ProjectId == projectId && c.ArchivedAt == null)
            .GroupBy(c => c.Kind).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return TypedResults.Ok(new FieldProgress(
            await b.CountAsync(ct),
            await b.CountAsync(x => x.Status == BuildingStatus.Confirmed, ct),
            await b.CountAsync(x => x.Status == BuildingStatus.NotPresent, ct),
            await b.CountAsync(x => x.Status == BuildingStatus.New, ct),
            await b.CountAsync(x => x.Status == BuildingStatus.Predicted, ct),
            await b.CountAsync(x => x.Status == BuildingStatus.Predicted && x.LowConfidence, ct),
            await db.LoadPoints.CountAsync(l => l.ProjectId == projectId && l.Status == LoadPointStatus.Estimated, ct),
            await db.LoadPoints.CountAsync(l => l.ProjectId == projectId && l.Status == LoadPointStatus.Confirmed, ct),
            await present.CountAsync(x => !withLoad.Contains(x.Id), ct),
            await db.Assumptions.CountAsync(a => a.ProjectId == projectId && a.Status == AssumptionStatus.Open, ct),
            candidates));
    }

    private static async Task<Results<Ok<LoadSchedule>, NotFound>> Schedule(Guid projectId, ReticulaDbContext db, ICalcClient calc, TimeProvider time, CancellationToken ct)
    {
        var schedule = await BuildScheduleAsync(projectId, db, calc, time, ct);
        return schedule is null ? TypedResults.NotFound() : TypedResults.Ok(schedule);
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> ScheduleCsv(Guid projectId, ReticulaDbContext db, ICalcClient calc, TimeProvider time, CancellationToken ct)
    {
        var s = await BuildScheduleAsync(projectId, db, calc, time, ct);
        if (s is null) return TypedResults.NotFound();

        var sb = new StringBuilder();
        sb.AppendLine(Csv($"Reticula load schedule: {s.Project}", $"rules {s.RulesRef} ({s.RulesHash})", $"generated {s.GeneratedAt:yyyy-MM-dd HH:mm} UTC", "values are estimates until confirmed"));
        sb.AppendLine(Csv("erf", "building_id", "building_type", "building_status", "load_kind", "category", "income_band", "kva", "estimated_kva", "overridden", "override_reason", "load_status"));
        foreach (var r in s.Rows)
            sb.AppendLine(Csv(r.Erf, r.BuildingId.ToString(), r.BuildingType, r.BuildingStatus, r.LoadKind, r.Category, r.IncomeBand,
                Num(r.Kva), Num(r.EstimatedKva), r.Overridden ? "yes" : "no", r.OverrideReason, r.LoadStatus));
        if (s.Totals is { } t)
        {
            var method = t.Method == "herman_beta"
                ? $"{t.ResidentialCount} loads, Herman-Beta {Num(t.ConfidencePct)} % over {t.Phases} phase(s), {Num(t.DesignCurrentA)} A per phase, factor {Num(t.DiversityFactor)} on Σ ADMD"
                : $"{t.ResidentialCount} loads, diversity factor {Num(t.DiversityFactor)}";
            sb.AppendLine(Csv("TOTAL residential", null, null, null, null, null, null, Num(t.ResidentialKva), null, null, method, null));
            sb.AppendLine(Csv("TOTAL special", null, null, null, null, null, null, Num(t.SpecialKva), null, null, $"{t.SpecialCount} loads", null));
            sb.AppendLine(Csv("TOTAL after diversity", null, null, null, null, null, null, Num(t.TotalKva), null, null, $"{t.Formula}; {t.Clause}", null));
        }
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return TypedResults.File(bytes, "text/csv", $"load-schedule-{projectId:N}.csv");
    }

    private static async Task<LoadSchedule?> BuildScheduleAsync(Guid projectId, ReticulaDbContext db, ICalcClient calc, TimeProvider time, CancellationToken ct)
    {
        var project = await ProjectAsync(db, projectId, ct);
        if (project is null) return null;

        var rows = await (
            from b in db.Buildings.AsNoTracking()
            where b.ProjectId == projectId && b.Status != BuildingStatus.NotPresent
            join s in db.Stands.AsNoTracking() on b.StandId equals s.Id into ss
            from s in ss.DefaultIfEmpty()
            join l in db.LoadPoints.AsNoTracking() on b.Id equals l.BuildingId into ls
            from l in ls.DefaultIfEmpty()
            orderby s.ErfNumber, b.Id
            select new { b, Erf = s == null ? null : s.ErfNumber, l }).ToListAsync(ct);

        var scheduleRows = rows.Select(x => new LoadScheduleRow(
            x.Erf, x.b.Id, x.b.ConfirmedType ?? x.b.PredictedType, x.b.Status.ToString().ToLowerInvariant(),
            x.l?.Kind, x.l?.Category, x.l?.IncomeBand, x.l?.Kva, x.l?.EstimatedKva, x.l?.Overridden ?? false, x.l?.OverrideReason,
            x.l?.Status.ToString().ToLowerInvariant())).ToList();

        var loads = rows.Where(x => x.l is not null)
            .Select(x => new AdmdGroupLoad(x.l!.Id.ToString(), x.l.Kind, x.l.Kva, x.l.Kind == LoadKinds.Residential ? x.l.Category : null)).ToList();
        LoadScheduleTotals? totals = null;
        var rulesHash = rows.FirstOrDefault(x => x.l is not null)?.l!.RulesHash ?? "";
        if (loads.Count > 0)
        {
            var g = await calc.GroupAdmdAsync(project.RulesRef, loads, ct);
            rulesHash = g.RulesHash;
            totals = new LoadScheduleTotals(g.ResidentialCount, g.SpecialCount, g.DiversityFactor?.Value, g.ResidentialKva.Value,
                g.SpecialKva, g.TotalKva.Value, g.TotalKva.Formula, g.TotalKva.Clause, g.Method, g.Phases, g.ConfidencePct, g.DesignCurrentA?.Value);
        }
        return new LoadSchedule(project.Name, project.RulesRef, rulesHash, time.GetUtcNow(), scheduleRows, totals);
    }

    // ---------- helpers ----------

    private static Task<Project?> ProjectAsync(ReticulaDbContext db, Guid projectId, CancellationToken ct) =>
        db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId && p.ArchivedAt == null, ct);

    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static bool InArea(Project project, Geometry g)
    {
        var env = new Envelope(project.Area.EnvelopeInternal);
        env.ExpandBy(AreaTolerance);
        return env.Contains(g.EnvelopeInternal);
    }

    private static Point? ToPoint(PositionDto? p, Dictionary<string, string[]> errors, bool required = false)
    {
        if (p is null)
        {
            if (required) errors["position"] = ["A position is required."];
            return null;
        }
        if (!double.IsFinite(p.Lon) || !double.IsFinite(p.Lat) || p.Lon is < -180 or > 180 || p.Lat is < -90 or > 90)
        {
            errors["position"] = ["Position must be a valid longitude and latitude."];
            return null;
        }
        return PolygonDto.Factory.CreatePoint(new Coordinate(p.Lon, p.Lat));
    }

    private static async Task<BuildingFieldDto> DtoAsync(ReticulaDbContext db, Building b, CancellationToken ct)
    {
        var erf = b.StandId is null ? null : await db.Stands.AsNoTracking().Where(s => s.Id == b.StandId).Select(s => s.ErfNumber).FirstOrDefaultAsync(ct);
        return new BuildingFieldDto(b.Id, b.Status.ToString().ToLowerInvariant(), b.PredictedType, b.ConfirmedType, b.EffectiveType,
            b.PredictedConfidence, erf, PointDto.From(b.Location), b.InspectedAt, b.Version);
    }

    private static GeoFeature<CandidateProps> Feature(Candidate c) =>
        new("Feature", c.Id.ToString(), GeometryInput.ToDto(c.Geometry), new CandidateProps(c.Kind, c.Notes, c.CreatedAt, c.Version));

    private static LoadPointDto ToDto(LoadPoint l) => new(
        l.Id, l.BuildingId, l.Kind, l.SpecialLoad, JsonDocument.Parse(l.ObservationsJson).RootElement.Clone(), l.ClassOverride, l.IncomeBand, l.Category,
        l.EstimatedKva, l.Kva, l.Overridden, l.OverrideReason, JsonSerializer.Deserialize<List<string>>(l.MissingJson, Json) ?? [],
        l.Status.ToString().ToLowerInvariant(), l.UpdatedAt, l.Version);

    private static PhotoDto ToDto(Photo p) => new(p.Id, p.BuildingId, p.CandidateId, p.ContentType, p.SizeBytes, p.CapturedAt);

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Length > max ? s[..max] : s.Trim();

    private static string Num(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";

    private static string Csv(params string?[] cells) => string.Join(",", cells.Select(c =>
    {
        if (string.IsNullOrEmpty(c)) return "";
        // Neutralise spreadsheet formula injection, then quote when needed.
        var v = c[0] is '=' or '+' or '-' or '@' && !double.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? "'" + c : c;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
    }));
}
