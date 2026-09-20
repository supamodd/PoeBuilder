using System.Text.Json;

namespace PoeBuilder.Core.Storage;

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public string Language { get; init; } = "ru";
    public string TargetGameVersion { get; init; } = "0.5.5c";
}

public sealed record SettingsReadResult(AppSettings Settings, bool RecoveredFromError);

public sealed class SettingsRepository(string path)
{
    public string FilePath { get; } = Path.GetFullPath(path);

    public async Task<SettingsReadResult> ReadAsync()
    {
        if (!File.Exists(FilePath)) return new(new(), false);
        try
        {
            var info = new FileInfo(FilePath);
            if (info.Length > 64 * 1024) return new(new(), true);
            var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(FilePath), BuildRepository.JsonOptions);
            if (settings is null || settings.SchemaVersion != 1 || settings.Language is not ("ru" or "en") ||
                string.IsNullOrWhiteSpace(settings.TargetGameVersion) || settings.TargetGameVersion.Length > 32)
                return new(new(), true);
            return new(settings, false);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        { return new(new(), true); }
    }

    public Task SaveAsync(AppSettings settings)
    {
        if (settings.SchemaVersion != 1 || settings.Language is not ("ru" or "en")) throw new ArgumentException("Invalid settings.");
        return AtomicJson.WriteAsync(FilePath, settings, BuildRepository.JsonOptions);
    }
}
