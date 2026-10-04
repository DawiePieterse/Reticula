using System.Globalization;
using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;

namespace Reticula.Infrastructure.Layout;

public sealed class OverpassException(string message) : Exception(message);

/// <summary>
/// Fetches OpenStreetMap buildings or roads for a project area from an Overpass API server (Overpass:Url, default the
/// public overpass-api.de instance). The answer is the same `out geom` JSON an export from overpass-turbo gives.
/// </summary>
public sealed class OverpassClient(HttpClient http, IConfiguration config)
{
    public const string DefaultUrl = "https://overpass-api.de/api/interpreter";

    public static string Query(Envelope e, string kind)
    {
        // Overpass bounding boxes are (south, west, north, east).
        var bbox = string.Join(',', new[] { e.MinY, e.MinX, e.MaxY, e.MaxX }.Select(v => v.ToString("0.#######", CultureInfo.InvariantCulture)));
        var body = kind == "roads"
            ? $"way[\"highway\"]({bbox});"
            : $"(way[\"building\"]({bbox});relation[\"building\"]({bbox}););";
        return $"[out:json][timeout:90];{body}out geom;";
    }

    public async Task<byte[]> FetchAsync(Polygon area, string kind, CancellationToken ct)
    {
        var url = config["Overpass:Url"] is { Length: > 0 } u ? u : DefaultUrl;
        var envelope = area.EnvelopeInternal.Copy();
        envelope.ExpandBy(0.001); // about 100 m, so roads just outside the boundary come too
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("data", Query(envelope, kind))]);
        HttpResponseMessage r;
        try
        {
            r = await http.PostAsync(url, content, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new OverpassException($"OpenStreetMap (Overpass) could not be reached: {e.Message}");
        }
        using (r)
        {
            if (!r.IsSuccessStatusCode)
                throw new OverpassException($"OpenStreetMap (Overpass) answered {(int)r.StatusCode}; try again later or import an export file.");
            return await r.Content.ReadAsByteArrayAsync(ct);
        }
    }
}
