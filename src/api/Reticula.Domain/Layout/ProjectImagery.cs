namespace Reticula.Domain.Layout;

public static class ImagerySources
{
    public const string Orthophoto = "orthophoto";
    public const string Google = "google";
}

public enum ImageryStatus { Fetching, Ready, Failed }

/// <summary>
/// Aerial or satellite imagery of the project for the rooftop classifier (plan 1.3): an orthophoto GeoTIFF the engineer
/// uploads (NGI, municipal or drone survey) or Google satellite tiles where the installation's licence allows derived
/// use. The licence it is used under is recorded with it. One imagery is active per project.
/// </summary>
public sealed class ProjectImagery
{
    private ProjectImagery() { } // EF

    public ProjectImagery(Guid id, Guid projectId, string source, string format, string label, string licence, Guid createdBy, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        Source = source;
        Format = format;
        Label = label;
        Licence = licence;
        CreatedBy = createdBy;
        CreatedAt = now;
        Status = source == ImagerySources.Google ? ImageryStatus.Fetching : ImageryStatus.Ready;
        Active = true;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Source { get; private set; } = "";
    /// <summary>geotiff or tiles (a zip of z/x/y images).</summary>
    public string Format { get; private set; } = "";
    public string Label { get; private set; } = "";
    /// <summary>The licence or agreement that allows deriving data from the imagery.</summary>
    public string Licence { get; private set; } = "";
    public ImageryStatus Status { get; private set; }
    public bool Active { get; private set; }
    public string? StorageKey { get; private set; }
    public long SizeBytes { get; private set; }
    public string? Sha256 { get; private set; }
    public string? Error { get; private set; }
    public Guid? JobId { get; private set; }
    /// <summary>The last classification's model report (accuracy, types, whether it was used).</summary>
    public string? ModelJson { get; private set; }
    public DateTimeOffset? ClassifiedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public void Stored(string key, long size, string sha256)
    {
        StorageKey = key;
        SizeBytes = size;
        Sha256 = sha256;
        Status = ImageryStatus.Ready;
    }

    public void Fail(string error)
    {
        Status = ImageryStatus.Failed;
        Error = error;
        Active = false;
    }

    public void Queue(Guid jobId) => JobId = jobId;
    public void Activate() => Active = true;
    public void Deactivate() => Active = false;

    public void Classified(string modelJson, DateTimeOffset now)
    {
        ModelJson = modelJson;
        ClassifiedAt = now;
    }
}
