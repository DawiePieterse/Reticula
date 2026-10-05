using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;
using Reticula.Domain.Layout;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Infrastructure.Layout;

/// <summary>
/// Google satellite tiles for the rooftop classifier (plan 1.3). Off unless the installation has an API key and records
/// the agreement that allows deriving data from the imagery: Google's standard terms do not.
/// </summary>
public sealed class GoogleImageryOptions
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    /// <summary>The agreement with Google that allows derived use (required before Google imagery can be fetched).</summary>
    public string? LicenceReference { get; set; }
    public string BaseUrl { get; set; } = "https://tile.googleapis.com";
    public int Zoom { get; set; } = 19;
    public int MaxTiles { get; set; } = 4000;

    public bool IsOn => Enabled && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(LicenceReference);

    public static GoogleImageryOptions From(IConfiguration config) => new()
    {
        Enabled = config.GetValue("Imagery:Google:Enabled", false),
        ApiKey = config["Imagery:Google:ApiKey"],
        LicenceReference = config["Imagery:Google:LicenceReference"],
        BaseUrl = config["Imagery:Google:BaseUrl"] is { Length: > 0 } b ? b : "https://tile.googleapis.com",
        Zoom = config.GetValue("Imagery:Google:Zoom", 19),
        MaxTiles = config.GetValue("Imagery:Google:MaxTiles", 4000),
    };
}

/// <summary>Google Map Tiles API: a satellite session, then 2D tiles.</summary>
public sealed class GoogleTilesClient(HttpClient http, GoogleImageryOptions options)
{
    public async Task<string> SessionAsync(CancellationToken ct)
    {
        using var r = await http.PostAsJsonAsync($"{options.BaseUrl.TrimEnd('/')}/v1/createSession?key={Uri.EscapeDataString(options.ApiKey!)}",
            new { mapType = "satellite", language = "en-ZA", region = "ZA" }, ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Google refused the tile session ({(int)r.StatusCode}).");
        var body = await r.Content.ReadFromJsonAsync<JsonObject>(ct);
        return body?["session"]?.GetValue<string>() ?? throw new InvalidOperationException("Google gave no tile session.");
    }

    public async Task<byte[]> TileAsync(string session, int z, int x, int y, CancellationToken ct)
    {
        using var r = await http.GetAsync($"{options.BaseUrl.TrimEnd('/')}/v1/2dtiles/{z}/{x}/{y}?session={Uri.EscapeDataString(session)}&key={Uri.EscapeDataString(options.ApiKey!)}", ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"Google refused tile {z}/{x}/{y} ({(int)r.StatusCode}).");
        return await r.Content.ReadAsByteArrayAsync(ct);
    }
}

/// <summary>Fetches Google satellite tiles over the project's buildings into a z/x/y zip.</summary>
public sealed class GoogleImageryJob(ReticulaDbContext db, GoogleTilesClient google, GoogleImageryOptions options, IFileStore files) : IJobHandler
{
    public const string JobKind = "imagery.google";

    public string Kind => JobKind;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var imagery = await db.ProjectImagery.FirstAsync(i => i.Id == context.Payload.GetProperty("imageryId").GetGuid(), ct);
        try
        {
            if (!options.IsOn) throw new InvalidOperationException("Google imagery is not enabled for this installation.");
            var tiles = await TilesAsync(imagery.ProjectId, options.Zoom, ct);
            if (tiles.Count > options.MaxTiles)
                throw new InvalidOperationException($"The buildings need {tiles.Count} tiles at zoom {options.Zoom}; the limit is {options.MaxTiles}.");
            var session = await google.SessionAsync(ct);
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var done = 0;
                foreach (var (x, y) in tiles)
                {
                    var bytes = await google.TileAsync(session, options.Zoom, x, y, ct);
                    await using var s = zip.CreateEntry($"{options.Zoom}/{x}/{y}.png", CompressionLevel.NoCompression).Open();
                    await s.WriteAsync(bytes, ct);
                    if (++done % 50 == 0) await context.Progress.ReportAsync(5 + 90 * done / tiles.Count, $"{done} of {tiles.Count} tiles", ct);
                }
            }
            var key = $"projects/{imagery.ProjectId:N}/imagery/{imagery.Id:N}.zip";
            buffer.Position = 0;
            var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(buffer, ct));
            buffer.Position = 0;
            await files.SaveAsync(key, buffer, ct);
            imagery.Stored(key, buffer.Length, sha);
            await db.SaveChangesAsync(ct);
            return new { imageryId = imagery.Id, tiles = tiles.Count };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            imagery.Fail(e is InvalidOperationException ? e.Message : "The tiles could not be fetched; see the job log.");
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Tiles covering the buildings (or the project area when there are none), with a small margin.</summary>
    private async Task<List<(int X, int Y)>> TilesAsync(Guid projectId, int zoom, CancellationToken ct)
    {
        var rows = await db.Buildings.AsNoTracking().Where(b => b.ProjectId == projectId).Select(b => new { b.Footprint, b.Location }).ToListAsync(ct);
        var env = new Envelope();
        foreach (var r in rows) env.ExpandToInclude(((Geometry?)r.Footprint ?? r.Location).EnvelopeInternal);
        if (env.IsNull) env = (await db.Projects.AsNoTracking().FirstAsync(p => p.Id == projectId, ct)).Area.EnvelopeInternal;
        env.ExpandBy(0.0003);
        var (x0, y0) = Tile(env.MinX, env.MaxY, zoom);
        var (x1, y1) = Tile(env.MaxX, env.MinY, zoom);
        var list = new List<(int, int)>();
        for (var x = x0; x <= x1; x++)
            for (var y = y0; y <= y1; y++)
                list.Add((x, y));
        return list;
    }

    public static (int X, int Y) Tile(double lon, double lat, int zoom)
    {
        var n = Math.Pow(2, zoom);
        var latR = lat * Math.PI / 180;
        return ((int)Math.Floor((lon + 180) / 360 * n), (int)Math.Floor((1 - Math.Log(Math.Tan(latR) + 1 / Math.Cos(latR)) / Math.PI) / 2 * n));
    }
}

/// <summary>
/// Runs the rooftop classifier on the active imagery (plan 1.3): trains on the buildings confirmed in the field, stores
/// each unconfirmed building's signal and re-predicts their types with it.
/// </summary>
public sealed class RooftopClassifyJob(ReticulaDbContext db, ICalcClient calc, LayoutService layout, IFileStore files, TimeProvider time) : IJobHandler
{
    public const string JobKind = "imagery.classify";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => JobKind;

    public async Task<object?> RunAsync(JobContext context, CancellationToken ct)
    {
        var imagery = await db.ProjectImagery.FirstAsync(i => i.Id == context.Payload.GetProperty("imageryId").GetGuid(), ct);
        var project = await db.Projects.AsNoTracking().FirstAsync(p => p.Id == imagery.ProjectId, ct);
        if (imagery.Status != ImageryStatus.Ready || imagery.StorageKey is null) throw new InvalidOperationException("The imagery is not ready.");
        await context.Progress.ReportAsync(10, "Reading the buildings", ct);
        var buildings = await db.Buildings.Where(b => b.ProjectId == project.Id && b.Footprint != null).ToListAsync(ct);
        var request = new
        {
            Rules = project.RulesRef,
            ImageryKind = imagery.Format,
            ImageryLabel = imagery.Label,
            Buildings = buildings.Select(b => new
            {
                Id = b.Id.ToString(),
                Footprint = new[] { b.Footprint!.ExteriorRing }.Concat(b.Footprint.InteriorRings).Select(r => r.Coordinates.Select(c => new[] { c.X, c.Y })),
                ConfirmedType = b.Status is BuildingStatus.Confirmed or BuildingStatus.New ? b.ConfirmedType : null,
            }),
        };
        await context.Progress.ReportAsync(25, "Training on the confirmed buildings and classifying the rest", ct);
        await using var stream = await files.OpenReadAsync(imagery.StorageKey, ct) ?? throw new InvalidOperationException("The imagery file is missing.");
        var result = await calc.ClassifyRooftopsAsync(request, stream, imagery.Format == "tiles" ? "tiles.zip" : "orthophoto.tif", ct);
        var signals = result.GetProperty("signals").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("signal"));
        foreach (var b in buildings)
            b.SetRooftopSignal(signals.TryGetValue(b.Id.ToString(), out var sig)
                ? JsonSerializer.Serialize(new PredictionSignal(sig.GetProperty("source").GetString()!, sig.GetProperty("type").GetString()!, sig.GetProperty("confidence").GetDouble()), Json)
                : null);
        var model = result.GetProperty("model");
        imagery.Classified(JsonSerializer.Serialize(new
        {
            model,
            signals = signals.Count,
            outside_imagery = result.GetProperty("outside_imagery").GetInt32(),
            gsd_m = result.GetProperty("gsd_m").GetDouble(),
        }), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await context.Progress.ReportAsync(80, "Re-predicting building types", ct);
        await layout.RefreshBuildingsAsync(project, ct);
        return new { imageryId = imagery.Id, used = model.GetProperty("used").GetBoolean(), signals = signals.Count };
    }
}
