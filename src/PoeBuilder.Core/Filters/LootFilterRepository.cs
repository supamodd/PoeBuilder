using System.Text.Json;
using PoeBuilder.Core.Storage;

namespace PoeBuilder.Core.Filters;

/// <summary>Local files only, exactly like the build library: ids, not user-controlled names, decide paths.
/// A filter and a build are separate documents in separate folders, so neither can read or damage the other.</summary>
public sealed class LootFilterRepository(string rootDirectory)
{
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);
    public const int MaximumFileBytes = 2 * 1024 * 1024;

    public string PathFor(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("Empty ID.", nameof(id));
        return Path.Combine(RootDirectory, id.ToString("N") + ".poefilter");
    }

    public static async Task<LootFilterDocument> ReadAsync(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumFileBytes) throw new FilterFormatException("Filter file exceeds 2 MiB.");
        try
        {
            using var json = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) ||
                format.ValueKind != JsonValueKind.String || format.GetString() != LootFilterDocument.FormatName)
                throw new FilterFormatException("This is not a PoeBuilder Native filter.");
            if (!root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version != 1)
                throw new FilterFormatException("Unsupported native filter schema version.");
            return root.Deserialize<LootFilterDocument>(BuildRepository.JsonOptions)
                ?? throw new FilterFormatException("Empty filter.");
        }
        catch (JsonException ex)
        {
            throw new FilterFormatException("The filter file is damaged: " + ex.Message);
        }
    }

    /// <summary>The library, newest first, plus the files that could not be read. A damaged file is reported
    /// rather than deleted: the player may want to recover it by hand.</summary>
    public async Task<(IReadOnlyList<LootFilterDocument> Filters, IReadOnlyList<string> Unreadable)> ReadLibraryAsync()
    {
        Directory.CreateDirectory(RootDirectory);
        var filters = new List<LootFilterDocument>();
        var errors = new List<string>();
        foreach (var path in Directory.EnumerateFiles(RootDirectory, "*.poefilter", SearchOption.TopDirectoryOnly)
                         .Where(p => !p.Contains(Path.Combine(RootDirectory, "Trash"))))
        {
            try
            {
                var filter = await ReadAsync(path);
                if (!Path.GetFileName(path).Equals(filter.Id.ToString("N") + ".poefilter", StringComparison.OrdinalIgnoreCase))
                    throw new FilterFormatException("File name and filter ID do not match.");
                filters.Add(filter);
            }
            catch (Exception ex) when (ex is FilterFormatException or IOException or UnauthorizedAccessException)
            { errors.Add(Path.GetFileName(path)); }
        }
        return (filters.OrderByDescending(f => f.UpdatedUtc).ToArray(), errors);
    }

    public async Task<LootFilterDocument> SaveAsync(LootFilterDocument filter)
    {
        FilterValidation.Validate(filter);
        var normalized = filter with { Name = filter.Name.Trim(), UpdatedUtc = DateTimeOffset.UtcNow };
        FilterValidation.Validate(normalized);
        await AtomicJson.WriteAsync(PathFor(normalized.Id), normalized, BuildRepository.JsonOptions);
        return normalized;
    }

    public async Task<LootFilterDocument> DuplicateAsync(LootFilterDocument source, string name)
    {
        var now = DateTimeOffset.UtcNow;
        return await SaveAsync(source with { Id = Guid.NewGuid(), Name = name, CreatedUtc = now, UpdatedUtc = now });
    }

    public Task<string> MoveToTrashAsync(Guid id)
    {
        var source = PathFor(id);
        var trash = Path.Combine(RootDirectory, "Trash");
        Directory.CreateDirectory(trash);
        var name = $"{id:N}-{Guid.NewGuid():N}.poefilter";
        var destination = Path.Combine(trash, name);
        File.Move(source, destination);
        return Task.FromResult(destination);
    }

    /// <summary>Writes the filter where the game will pick it up. The folder is the one the game itself
    /// reports in its own settings; when it is missing the caller decides where the file goes instead.</summary>
    public static void ExportTo(string directory, string fileName, string contents)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new FilterFormatException("The file name is not valid on this system.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), contents, new System.Text.UTF8Encoding(false));
    }
}
