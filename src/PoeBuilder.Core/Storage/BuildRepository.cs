using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using PoeBuilder.Core.Models;

namespace PoeBuilder.Core.Storage;

public sealed record LibraryReadResult(IReadOnlyList<BuildDocument> Builds, IReadOnlyList<string> UnreadableFiles);

/// <summary>Local files only. IDs, not user-controlled names, determine storage paths.</summary>
public sealed class BuildRepository(string rootDirectory)
{
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string PathFor(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Empty ID.", nameof(id));
        return Path.Combine(RootDirectory, id.ToString("N") + ".poebuild");
    }

    public static async Task<BuildDocument> ReadDocumentAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new BuildFormatException("Build file exceeds 2 MiB.");
        try
        {
            using var json = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) ||
                format.ValueKind != JsonValueKind.String || format.GetString() != BuildDocument.FormatName)
                throw new BuildFormatException("This is not a PoeBuilder Native build. PoB share codes are not supported yet.");
            if (!root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version is not (1 or 2 or 3 or 4))
                throw new BuildFormatException("Unsupported native schema version.");
            // Explicit nulls count as absent: legacy writers may emit null placeholders.
            static bool Present(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null;
            if (version == 1 && Present(root, "tree"))
                throw new BuildFormatException("Schema 1 cannot contain a passive tree.");
            if (version < 4 && (Present(root, "equipment") || Present(root, "skills")))
                throw new BuildFormatException("Legacy schema cannot contain equipment or skill plans.");
            var build = root.Deserialize<BuildDocument>(JsonOptions) ?? throw new BuildFormatException("Empty build.");
            if (version == 2 && root.TryGetProperty("tree", out var tree) && tree.ValueKind == JsonValueKind.Object && tree.TryGetProperty("ascendancy", out _))
                throw new BuildFormatException("Schema 2 cannot contain ascendancy data.");
            // Migration is in memory only. The original file is untouched until explicit Save.
            if (version == 1) build = build with { SchemaVersion = 4, Tree = null };
            else if (version is 2 or 3) build = build with { SchemaVersion = 4 };
            BuildValidation.Validate(build);
            return build;
        }
        catch (JsonException exception) { throw new BuildFormatException("Invalid JSON: " + exception.Message); }
        catch (InvalidOperationException exception) { throw new BuildFormatException("Invalid JSON field: " + exception.Message); }
    }

    public async Task<LibraryReadResult> ReadLibraryAsync()
    {
        Directory.CreateDirectory(RootDirectory);
        var builds = new List<BuildDocument>();
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(RootDirectory, "*.poebuild", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var build = await ReadDocumentAsync(path);
                if (!Path.GetFileName(path).Equals(build.Id.ToString("N") + ".poebuild", StringComparison.OrdinalIgnoreCase))
                    throw new BuildFormatException("File name and build ID do not match.");
                builds.Add(build);
            }
            catch (Exception ex) when (ex is BuildFormatException or IOException or UnauthorizedAccessException)
            { errors.Add(Path.GetFileName(path)); }
        }
        return new(builds.OrderByDescending(b => b.UpdatedUtc).ToArray(), errors);
    }

    public async Task<BuildDocument> SaveAsync(BuildDocument build)
    {
        BuildValidation.Validate(build);
        var normalized = build with { Name = build.Name.Trim(), UpdatedUtc = DateTimeOffset.UtcNow };
        BuildValidation.Validate(normalized);
        await WriteDocumentAsync(PathFor(normalized.Id), normalized);
        return normalized;
    }

    public async Task<BuildDocument> ImportAsNewAsync(string source)
    {
        var imported = await ReadDocumentAsync(source);
        var now = DateTimeOffset.UtcNow;
        return await SaveAsync(imported with { Id = Guid.NewGuid(), CreatedUtc = now, UpdatedUtc = now });
    }

    public async Task<BuildDocument> DuplicateAsync(BuildDocument source, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return await SaveAsync(source with { Id = Guid.NewGuid(), Name = name, CreatedUtc = now, UpdatedUtc = now });
    }

    public Task<string> MoveToTrashAsync(Guid id)
    {
        var source = PathFor(id);
        var trash = Path.Combine(RootDirectory, "Trash");
        Directory.CreateDirectory(trash);
        var destination = Path.Combine(trash, $"{id:N}-{Guid.NewGuid():N}.poebuild");
        File.Move(source, destination);
        return Task.FromResult(destination);
    }

    public static async Task WriteDocumentAsync(string destination, BuildDocument document)
    {
        BuildValidation.Validate(document);
        if (JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions).Length > MaximumFileBytes) throw new BuildFormatException("Build exceeds the readable file size limit (2 MiB).");
        await AtomicJson.WriteAsync(destination, document, JsonOptions);
    }
}

public static class AtomicJson
{
    public static async Task WriteAsync<T>(string destination, T document, JsonSerializerOptions options)
    {
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, document, options);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(destination)) File.Replace(temporary, destination, destination + ".bak");
            else File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
