using System.Text.Json;
using Reticula.Infrastructure.Geo;

namespace Reticula.Infrastructure.Calc;

public sealed record CalcImportRequest(Stream File, string FileName, string Kind, string? SourceCrs, string? Layer, string? AreaGeoJson, double? ContourInterval = null);

public sealed record CalcIssue(string Severity, string Code, string Message, int Count, IReadOnlyList<string> Samples);

public sealed record CalcLayer(string Name, int ClosedPolylines, int OpenPolylines, int Texts, int Points = 0);

/// <summary>One imported feature. Geometry is a polygon (stands, buildings), a line (roads, contours, network lines) or a point (network equipment).</summary>
public sealed record CalcFeature(
    string Ref, GeometryInput Geometry, double AreaM2, string? Erf, string? Zoning, string? OsmId,
    Dictionary<string, string> Tags, Dictionary<string, JsonElement> Attributes,
    double LengthM = 0, string? Name = null, string? Subtype = null, double? ElevationM = null);

public sealed record CalcImportResult(
    string Kind, string Format, string? SourceCrs, string CrsReason,
    IReadOnlyList<CalcFeature> Features, IReadOnlyList<CalcIssue> Issues, IReadOnlyList<CalcLayer> Layers)
{
    public bool HasErrors => Issues.Any(i => i.Severity == "error");
}

public sealed record BuildingPredictionInput(string Id, double? AreaM2, Dictionary<string, string> Tags, string? Zoning);

public sealed record PredictionSignal(string Source, string Type, double Confidence);

public sealed record BuildingPrediction(string Id, string Type, double Confidence, string Source, bool LowConfidence, IReadOnlyList<PredictionSignal> Signals);

public sealed record PredictionResult(string RulesHash, string Method, IReadOnlyList<BuildingPrediction> Predictions);

/// <summary>The calc service understood the request but refused it (HTTP 422), e.g. an unreadable file.</summary>
public sealed class CalcRejectedException(string message) : Exception(message);
