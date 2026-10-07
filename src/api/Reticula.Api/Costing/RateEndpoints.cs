using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reticula.Api.Infrastructure;
using Reticula.Domain.Auth;
using Reticula.Domain.Costing;
using Reticula.Infrastructure.Costing;
using Reticula.Infrastructure.Data;

namespace Reticula.Api.Costing;

public sealed record RateOverrideDto(string ItemCode, double Rate, DateOnly RateDate, string Source, DateTimeOffset CreatedAt);

/// <param name="Items">The library's items (calc cost.RateItem, snake_case) with the overrides applied.</param>
public sealed record RateLibraryDto(Guid Id, string Name, DateOnly RateDate, string Source, string Currency, bool Indicative, DateTimeOffset ImportedAt,
    int ItemCount, int AssemblyCount, JsonElement Items, JsonElement Assemblies, IReadOnlyList<RateOverrideDto> Overrides);

public sealed record RateImportDto(RateLibraryDto Library, int Replaced, int Added, int StillIndicative);

public sealed record SaveRateOverrideRequest(double Rate, DateOnly RateDate, string Source);

/// <summary>The rate library (plan 6.1): one active price list, imported from CSV, with the engineer's own rates over it.</summary>
public static class RateEndpoints
{
    private const long MaxCsvBytes = 5 * 1024 * 1024;

    public static IEndpointRouteBuilder MapRateEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/rates").WithTags("Rates").RequireAuthorization(Policies.Engineer);
        g.MapGet("/", Get);
        g.MapPost("/import", Import).DisableAntiforgery();
        g.MapPut("/overrides/{code}", SaveOverride);
        g.MapDelete("/overrides/{code}", RemoveOverride);
        return app;
    }

    private static async Task<Ok<RateLibraryDto>> Get(RateService rates, ClaimsPrincipal user, CancellationToken ct) =>
        TypedResults.Ok(await DtoAsync(rates, await rates.ActiveAsync(user.UserId(), ct), ct));

    private static async Task<Results<Ok<RateImportDto>, ValidationProblem>> Import(IFormFile? file, [FromForm] string? name, [FromForm] string? rateDate,
        [FromForm] string? source, RateService rates, ClaimsPrincipal user, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (file is null || file.Length == 0) errors["file"] = ["Choose a CSV price list."];
        else if (file.Length > MaxCsvBytes) errors["file"] = [$"The file is larger than {MaxCsvBytes / 1024 / 1024} MB."];
        if (string.IsNullOrWhiteSpace(name)) errors["name"] = ["Name the price list."];
        if (!DateOnly.TryParseExact(rateDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            errors["rateDate"] = ["The date of the prices, as yyyy-mm-dd."];
        if (string.IsNullOrWhiteSpace(source)) errors["source"] = ["Say where the prices come from: the supplier's quotation or contract."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

        string text;
        using (var reader = new StreamReader(file!.OpenReadStream()))
            text = await reader.ReadToEndAsync(ct);
        var result = await rates.ImportCsvAsync(text, name!.Trim(), date, source!.Trim(), user.UserId(), ct);
        if (result.Errors.Count > 0) return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [.. result.Errors.Take(50)] });
        return TypedResults.Ok(new RateImportDto(await DtoAsync(rates, result.Library, ct), result.Replaced, result.Added, result.StillIndicative));
    }

    private static async Task<Results<Ok<RateLibraryDto>, ValidationProblem>> SaveOverride(string code, SaveRateOverrideRequest req, RateService rates,
        ReticulaDbContext db, TimeProvider time, ClaimsPrincipal user, CancellationToken ct)
    {
        var lib = await rates.ActiveAsync(user.UserId(), ct);
        var errors = new Dictionary<string, string[]>();
        using (var items = JsonDocument.Parse(lib.ItemsJson))
            if (!items.RootElement.EnumerateArray().Any(i => i.GetProperty("code").GetString() == code))
                errors["code"] = [$"The active rate library has no item {code}."];
        if (!double.IsFinite(req.Rate) || req.Rate < 0) errors["rate"] = ["Must be zero or more."];
        if (string.IsNullOrWhiteSpace(req.Source)) errors["source"] = ["Say where this rate comes from."];
        if (errors.Count > 0) return TypedResults.ValidationProblem(errors);
        var now = time.GetUtcNow();
        foreach (var old in await db.RateOverrides.Where(o => o.ItemCode == code && o.RemovedAt == null).ToListAsync(ct)) old.Remove(now);
        await db.SaveChangesAsync(ct);
        db.RateOverrides.Add(new RateOverride(Guid.CreateVersion7(), code, req.Rate, req.RateDate, req.Source.Trim(), user.UserId(), now));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await DtoAsync(rates, lib, ct));
    }

    private static async Task<Results<Ok<RateLibraryDto>, NotFound>> RemoveOverride(string code, RateService rates, ReticulaDbContext db, TimeProvider time,
        ClaimsPrincipal user, CancellationToken ct)
    {
        var live = await db.RateOverrides.Where(o => o.ItemCode == code && o.RemovedAt == null).ToListAsync(ct);
        if (live.Count == 0) return TypedResults.NotFound();
        foreach (var o in live) o.Remove(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await DtoAsync(rates, await rates.ActiveAsync(user.UserId(), ct), ct));
    }

    private static async Task<RateLibraryDto> DtoAsync(RateService rates, RateLibrary lib, CancellationToken ct)
    {
        var merged = await rates.MergedAsync(lib, ct);
        var overrides = (await rates.OverridesAsync(ct)).Select(o => new RateOverrideDto(o.ItemCode, o.Rate, o.RateDate, o.Source, o.CreatedAt)).ToList();
        var items = JsonDocument.Parse(merged["items"]!.ToJsonString()).RootElement.Clone();
        var assemblies = JsonDocument.Parse(lib.AssembliesJson).RootElement.Clone();
        return new RateLibraryDto(lib.Id, lib.Name, lib.RateDate, lib.Source, lib.Currency, lib.Indicative, lib.ImportedAt, items.GetArrayLength(),
            assemblies.GetArrayLength(), items, assemblies, overrides);
    }
}
