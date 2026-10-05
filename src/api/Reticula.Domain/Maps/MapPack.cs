namespace Reticula.Domain.Maps;

/// <summary>
/// A project's basemap for offline use: a PMTiles archive cut from the configured map source for the project area.
/// A project has at most one; building a new one replaces it.
/// </summary>
public sealed class MapPack
{
    private MapPack() { } // EF

    public MapPack(Guid id, Guid projectId, string storageKey, long sizeBytes, string sha256, double minLon, double minLat,
        double maxLon, double maxLat, int maxZoom, int tileCount, string source, Guid builtBy, DateTimeOffset builtAt)
    {
        Id = id;
        ProjectId = projectId;
        StorageKey = storageKey;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        MinLon = minLon;
        MinLat = minLat;
        MaxLon = maxLon;
        MaxLat = maxLat;
        MaxZoom = maxZoom;
        TileCount = tileCount;
        Source = source;
        BuiltBy = builtBy;
        BuiltAt = builtAt;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string StorageKey { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = "";
    public double MinLon { get; private set; }
    public double MinLat { get; private set; }
    public double MaxLon { get; private set; }
    public double MaxLat { get; private set; }
    public int MaxZoom { get; private set; }
    public int TileCount { get; private set; }

    /// <summary>The map source archive the pack was cut from, e.g. a Protomaps build name.</summary>
    public string Source { get; private set; } = "";
    public Guid BuiltBy { get; private set; }
    public DateTimeOffset BuiltAt { get; private set; }
}
