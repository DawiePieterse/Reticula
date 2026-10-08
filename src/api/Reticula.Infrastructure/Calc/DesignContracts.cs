using System.Text.Json;

namespace Reticula.Infrastructure.Calc;

/// <summary>
/// The serializer settings of the calc service's contracts: snake_case properties, dictionary keys as given. Design and
/// document requests are built as JSON with these settings; their results are kept as the calc service's JSON, unchanged.
/// </summary>
public static class CalcJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

/// <summary>A file the calc service rendered, with the file name it gave in Content-Disposition.</summary>
public sealed record CalcFile(byte[] Content, string ContentType, string FileName);

/// <summary>What a document's stamp says about the project and revision (calc documents.DocumentMeta).</summary>
public sealed record DocumentMeta(
    string ProjectName, string? ProjectCode, string? Area, string Authority, string? Client, int RevisionNumber, string RevisionLabel,
    string DesignDate, string? DocumentNumber, string? EngineerName, string? EngineerRegistration, bool SignedOff, string? SignedOffAt);

public sealed record DocumentSectionIn(string Title, string Text);

/// <summary>A generated document handed to the pack (calc documents.PackFile).</summary>
public sealed record PackFileIn(string Name, string Title, string Kind, string? Number, string ContentBase64);
