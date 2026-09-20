using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PoeBuilder.Core.Equipment;

namespace PoeBuilder.App.Services;

/// <summary>
/// Resolves bundled game artwork (Data/Icons, GGG art via pinned community mirrors — see NOTICE.txt).
/// Pure path logic is separated so tests can verify coverage without touching WPF imaging.
/// </summary>
public sealed class IconService
{
    public static IconService Instance { get; } = new();
    private readonly Dictionary<string, string> _gemIcons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _classFallback = new(StringComparer.Ordinal);
    private readonly HashSet<string> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.Ordinal);
    public bool Ready { get; private set; }
    public string IconRoot { get; } = Path.Combine(AppContext.BaseDirectory, "Data", "Icons");

    public void Initialize()
    {
        Ready = false; _gemIcons.Clear(); _classFallback.Clear(); _files.Clear();
        try
        {
            var manifest = Path.Combine(IconRoot, "manifest.json");
            if (!File.Exists(manifest)) return;
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            var root = json.RootElement;
            if (root.TryGetProperty("filesSha256", out var files))
                foreach (var f in files.EnumerateObject()) _files.Add(f.Name);
            if (root.TryGetProperty("gemIcons", out var gems))
                foreach (var g in gems.EnumerateObject()) _gemIcons[g.Name] = g.Value.GetString() ?? "";
            if (root.TryGetProperty("classFallback", out var classes))
                foreach (var c in classes.EnumerateObject()) _classFallback[c.Name] = c.Value.GetString() ?? "";
            Ready = true;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Text.Json.JsonException) { }
    }

    /// <summary>"Art/2DItems/.../Name.dds" → "Items/2DItems/.../Name.png" (as stored by tools/prepare_icons.py).</summary>
    public static string? ItemRelativePath(string art)
    {
        if (art.Length <= 4 || !art.StartsWith("Art/") || !art.EndsWith(".dds")) return null;
        return "Items/" + art[4..^4] + ".png";
    }

    public string? BaseRelativePath(ItemBase b)
    {
        var direct = ItemRelativePath(b.Art);
        if (direct is not null && _files.Contains(direct)) return direct;
        return _classFallback.GetValueOrDefault(b.ClassName);
    }
    public string? GemRelativePath(string gemId) => _gemIcons.GetValueOrDefault(gemId);
    public bool FileExists(string relativePath) => _files.Contains(relativePath);
    public IReadOnlyDictionary<string, string> GemMap => _gemIcons;

    public ImageSource? ForBase(ItemBase b) => Load(BaseRelativePath(b));
    public ImageSource? ForClass(string className) => Load(_classFallback.GetValueOrDefault(className));
    public ImageSource? ForGem(string gemId) => Load(GemRelativePath(gemId));

    private ImageSource? Load(string? relativePath)
    {
        if (!Ready || relativePath is null || relativePath.Length == 0) return null;
        if (_cache.TryGetValue(relativePath, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            var path = Path.Combine(IconRoot, relativePath);
            if (File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.DecodePixelWidth = 256;
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (NotSupportedException) { }
        _cache[relativePath] = image;
        return image;
    }
}
