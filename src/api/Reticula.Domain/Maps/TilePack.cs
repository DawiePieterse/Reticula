namespace Reticula.Domain.Maps;

/// <summary>
/// The offline base map for one project: a PMTiles archive covering the project area, kept in the file store.
/// Rebuilding replaces it.
/// </summary>
public sealed class TilePack
{
    private TilePack() { } // EF

    public TilePack(Guid projectId, string storageKey, int minZoom, int maxZoom, int tileCount, long sizeBytes, string sha256,
        string source, string attribution, double west, double south, double east, double north, Guid builtBy, DateTimeOffset builtAt)
    {
        ProjectId = projectId;
        Replace(storageKey, minZoom, maxZoom, tileCount, sizeBytes, sha256, source, attribution, west, south, east, north, builtBy, builtAt);
    }

    public Guid ProjectId { get; private set; }
    public string StorageKey { get; private set; } = "";
    public int MinZoom { get; private set; }
    public int MaxZoom { get; private set; }
    public int TileCount { get; private set; }
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = "";
    /// <summary>Tile server the tiles came from, without credentials.</summary>
    public string Source { get; private set; } = "";
    public string Attribution { get; private set; } = "";
    public double West { get; private set; }
    public double South { get; private set; }
    public double East { get; private set; }
    public double North { get; private set; }
    public Guid BuiltBy { get; private set; }
    public DateTimeOffset BuiltAt { get; private set; }

    public void Replace(string storageKey, int minZoom, int maxZoom, int tileCount, long sizeBytes, string sha256,
        string source, string attribution, double west, double south, double east, double north, Guid builtBy, DateTimeOffset builtAt)
    {
        StorageKey = storageKey;
        MinZoom = minZoom;
        MaxZoom = maxZoom;
        TileCount = tileCount;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        Source = source;
        Attribution = attribution;
        West = west;
        South = south;
        East = east;
        North = north;
        BuiltBy = builtBy;
        BuiltAt = builtAt;
    }
}
