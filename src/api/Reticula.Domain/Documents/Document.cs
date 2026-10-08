namespace Reticula.Domain.Documents;

public static class DocumentKinds
{
    public const string Drawing = "drawing_dxf";
    public const string Report = "report_pdf";
    public const string BoqXlsx = "boq_xlsx";
    public const string BoqPdf = "boq_pdf";
    public const string LoadSchedule = "load_schedule_xlsx";
    public const string GeoJson = "geojson";
    public const string Kml = "kml";
    public const string Shapefile = "shapefile_zip";
    public const string Pack = "pack";

    /// <summary>Generated one by one, in this order; the pack is made from them last.</summary>
    public static readonly IReadOnlyList<string> Rendered = [Drawing, Report, BoqXlsx, BoqPdf, LoadSchedule, GeoJson, Kml, Shapefile];
    public static readonly IReadOnlyList<string> All = [.. Rendered, Pack];

    public static string Title(string kind) => kind switch
    {
        Drawing => "Reticulation layout drawing",
        Report => "Design report",
        BoqXlsx => "Bill of quantities (spreadsheet)",
        BoqPdf => "Bill of quantities",
        LoadSchedule => "Load schedule",
        GeoJson => "GIS export (GeoJSON)",
        Kml => "GIS export (KML)",
        Shapefile => "GIS export (Shapefile)",
        Pack => "Submission pack",
        _ => kind,
    };
}

/// <summary>
/// A generated document (Phase 6), stored in the file store and stamped with what it was made from (6.8): the design run, the
/// revision, the rules, the rate date and the design date. Documents of a signed-off revision are locked.
/// </summary>
public sealed class Document
{
    private Document() { } // EF

    public Document(Guid id, Guid projectId, Guid designRunId, Guid? revisionId, string kind, string title, string number, string fileName,
        string storageKey, string contentType, long sizeBytes, string sha256, string rulesRef, string rulesHash, string rateDate, DateOnly designDate,
        int revisionNumber, string inputsHash, Guid createdBy, DateTimeOffset now)
    {
        Id = id;
        ProjectId = projectId;
        DesignRunId = designRunId;
        RevisionId = revisionId;
        Kind = kind;
        Title = title;
        Number = number;
        FileName = fileName;
        StorageKey = storageKey;
        ContentType = contentType;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        RulesRef = rulesRef;
        RulesHash = rulesHash;
        RateDate = rateDate;
        DesignDate = designDate;
        RevisionNumber = revisionNumber;
        InputsHash = inputsHash;
        CreatedBy = createdBy;
        CreatedAt = now;
    }

    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid DesignRunId { get; private set; }
    public Guid? RevisionId { get; private set; }
    public string Kind { get; private set; } = "";
    public string Title { get; private set; } = "";
    /// <summary>Drawing or document number in the registers.</summary>
    public string Number { get; private set; } = "";
    public string FileName { get; private set; } = "";
    public string StorageKey { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = "";
    public string RulesRef { get; private set; } = "";
    public string RulesHash { get; private set; } = "";
    public string RateDate { get; private set; } = "";
    public DateOnly DesignDate { get; private set; }
    /// <summary>0 for a draft made before any revision.</summary>
    public int RevisionNumber { get; private set; }
    /// <summary>The design run's inputs hash: a later run with other inputs makes this document stale.</summary>
    public string InputsHash { get; private set; } = "";
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public bool Locked { get; private set; }
    public DateTimeOffset? SupersededAt { get; private set; }

    public void Lock() => Locked = true;
    public void Supersede(DateTimeOffset now) => SupersededAt = now;
}
