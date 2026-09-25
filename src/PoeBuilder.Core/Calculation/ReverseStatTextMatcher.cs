using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoeBuilder.Core.Calculation;

/// <summary>
/// Reverse stat-translation lookup built from the pinned RePoE stat_descriptions export.
/// A compact table ({"t": template, "ids": [...]}) is stored as Data/Game/stat_text_reverse.json,
/// generated from stat_translations__stat_descriptions.json by preserving every English template
/// that contains a numeric placeholder. Each template becomes a regex ({N} → numeric capture,
/// [markup] removed); matching an English modifier line of an imported unique yields the stat ids
/// and the numeric values in translation order (min/max pairs included, e.g. "Adds X to Y").
/// Unknown stat ids are only reported by StatInterpreter, never guessed.</summary>
public static class ReverseStatTextMatcher
{
    public const string Sha256 = "9c87f17fd2757949e24e358728348742d4b4e89cda7f74e6ccbacf834acc6108";
    private const long MaxFileSize = 8L * 1024 * 1024;

    private sealed record Entry(Regex Pattern, string[] Ids, int Placeholders, string Template);

    private static readonly object Gate = new();
    private static IReadOnlyList<Entry>? _templates;
    private static Dictionary<string, List<Entry>>? _index;

    public static bool IsReady => Volatile.Read(ref _templates) is not null;

    /// <summary>Loads the compact reverse table once (idempotent). SHA-256 is pinned.</summary>
    public static void UseFile(string path)
    {
        lock (Gate)
        {
            if (_templates is not null) return;
            var entries = Load(path);
            Volatile.Write(ref _templates, entries);
            _index = BuildIndex(entries);
        }
    }

    /// <summary>Matches one English modifier line against the reverse table, or returns null.</summary>
    public static IReadOnlyList<(string Id, decimal Value)>? TryMatch(string line)
    {
        var templates = Volatile.Read(ref _templates);
        if (templates is null || string.IsNullOrWhiteSpace(line)) return null;
        var index = _index;
        string firstWord = FirstWord(line);
        if (index is not null && firstWord.Length > 0 && index.TryGetValue(firstWord, out var candidates) &&
            Scan(line, candidates) is { } byWord) return byWord;
        return Scan(line, templates);
    }

    private static IReadOnlyList<Entry> Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxFileSize) throw new InvalidDataException("Reverse stat text table missing or too large.");
        byte[] bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Reverse stat text table checksum mismatch.");
        using var document = JsonDocument.Parse(bytes);
        var entries = new List<Entry>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            string template = element.TryGetProperty("t", out var t) ? t.GetString() ?? "" : "";
            string[] ids = element.TryGetProperty("ids", out var idsArray) && idsArray.ValueKind == JsonValueKind.Array
                ? idsArray.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
                : Array.Empty<string>();
            if (template.Length == 0 || ids.Length == 0) continue;
            int placeholders = Regex.Matches(template, @"\{(\d+)\}").Count;
            if (placeholders == 0) continue;
            entries.Add(new(PatternFromTemplate(template), ids, placeholders, template));
        }
        return entries;
    }

    private static Regex PatternFromTemplate(string template)
    {
        // PoB markup like "[Projectile]" prints as the plain word in the item text, while
        // alternatives "[Life|Energy Shield]" read as the first option; flatten both to plain text.
        string cleaned = Regex.Replace(template, @"\[([^\]|]+)(?:\|[^\]]+)?\]", m => m.Groups[1].Value.Trim());
        var sb = new System.Text.StringBuilder("^");
        int pos = 0;
        foreach (Match placeholder in Regex.Matches(cleaned, @"\{(\d+)\}"))
        {
            sb.Append(Regex.Escape(cleaned.Substring(pos, placeholder.Index - pos)));
            sb.Append(@"([+-]?\d+(?:\.\d+)?)");
            pos = placeholder.Index + placeholder.Length;
        }
        sb.Append(Regex.Escape(cleaned.Substring(pos)));
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static Dictionary<string, List<Entry>> BuildIndex(IReadOnlyList<Entry> entries)
    {
        var index = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            string word = FirstWord(entry.Template);
            if (word.Length == 0) continue;
            if (!index.TryGetValue(word, out var list)) index[word] = list = new List<Entry>();
            list.Add(entry);
        }
        return index;
    }

    private static string FirstWord(string text)
    {
        var match = Regex.Match(text, @"^[^0-9{}]*?([A-Za-z]+)");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : "";
    }

    private static IReadOnlyList<(string Id, decimal Value)>? Scan(string line, IReadOnlyList<Entry> entries)
    {
        foreach (var entry in entries)
        {
            var match = entry.Pattern.Match(line);
            if (!match.Success) continue;
            // Local ranges are weapon-slot specific; imported uniques have no pinned base to scale,
            // so prefer the global/attack/spell wording of the same line when present.
            if (entry.Ids.Length > 0 && entry.Ids.All(id => id.StartsWith("local_", StringComparison.Ordinal))) continue;
            int count = Math.Min(entry.Placeholders, Math.Min(entry.Ids.Length, match.Groups.Count - 1));
            if (count <= 0) continue;
            var values = new List<(string, decimal)>(count);
            for (int i = 0; i < count; i++)
                values.Add((entry.Ids[i], decimal.Parse(match.Groups[i + 1].Value, CultureInfo.InvariantCulture)));
            return values;
        }
        return null;
    }
}