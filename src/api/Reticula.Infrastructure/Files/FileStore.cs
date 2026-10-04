using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Reticula.Infrastructure.Files;

/// <summary>Binary storage for photos and generated documents. Keys are relative paths.</summary>
public interface IFileStore
{
    Task SaveAsync(string key, Stream content, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);
}

/// <summary>Stores files under Storage:Root (default ./data/files). An S3/MinIO store replaces this in production.</summary>
public sealed partial class FileSystemFileStore(IConfiguration config) : IFileStore
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9/_.-]*$")]
    private static partial Regex SafeKey();

    private string Root => config["Storage:Root"] is { Length: > 0 } r ? r : Path.Combine(AppContext.BaseDirectory, "data", "files");

    public async Task SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".partial";
        await using (var f = File.Create(tmp))
            await content.CopyToAsync(f, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    private string PathFor(string key)
    {
        if (!SafeKey().IsMatch(key) || key.Contains("..")) throw new ArgumentException($"Unsafe storage key '{key}'.", nameof(key));
        return Path.Combine(Root, key);
    }
}
