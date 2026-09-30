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

    private sealed record Entry(Regex Pattern, string[] Ids, int Placeholders, string Template, HashSet<string> Words);

    private static readonly object Gate = new();
    private static IReadOnlyList<Entry>? _templates;
    private static Dictionary<string, List<Entry>>? _index;

    /// <summary>Result cache: the same line comes back on every recalculation (and several times per
    /// pass — the implicit, unique and unmatched-affix passes all walk the same item text), and the
    /// table is fixed for the lifetime of the process, so a line's answer never changes. Without it
    /// every pass paid a fresh scan of all templates, which was the single largest cost of a
    /// recalculation (docs/VALIDATION.md, "Пересчёт и отзывчивость").</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<(string Id, decimal Value)>> MatchCache = new(StringComparer.Ordinal);
    private static readonly IReadOnlyList<(string Id, decimal Value)> NoMatch = Array.Empty<(string, decimal)>();
    private const int MatchCacheLimit = 4096;

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
        if (MatchCache.TryGetValue(line, out var cached))
            return ReferenceEquals(cached, NoMatch) ? null : cached;
        var result = Match(line, templates);
        if (MatchCache.Count >= MatchCacheLimit) MatchCache.Clear();
        MatchCache[line] = result ?? NoMatch;
        return result;
    }

    private static IReadOnlyList<(string Id, decimal Value)>? Match(string line, IReadOnlyList<Entry> templates)
    {
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
            entries.Add(new(PatternFromTemplate(template), ids, placeholders, template, WordsOf(PatternText(template))));
        }
        return entries;
    }

    /// <summary>The template text as it is printed (markup aliases resolved to their display text).</summary>
    private static string PatternText(string template) => Regex.Replace(template, @"\[([^\]]+)\]", m =>
    {
        var parts = m.Groups[1].Value.Split('|');
        return parts[^1].Trim();
    });

    /// <summary>The distinct words of a text, used as a cheap necessary condition before a regex runs.
    /// A template can only match a line that contains every one of the template's own words, so the
    /// word test rejects the overwhelming majority of the 12 217 templates without a regex at all
    /// (the scan used to run a regex per template for every unrecognised line).</summary>
    private static HashSet<string> WordsOf(string text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match word in Regex.Matches(text, @"[A-Za-z0-9]{2,}")) words.Add(word.Value);
        return words;
    }

    private static bool ContainsAllWords(HashSet<string> lineWords, HashSet<string> templateWords)
    {
        foreach (string word in templateWords) if (!lineWords.Contains(word)) return false;
        return true;
    }

    private static Regex PatternFromTemplate(string template)
    {
        // PoB markup like "[Projectile]" prints as the plain word in the item text, and an alternative
        // "[Tag|Display]" prints as the DISPLAY text — the LAST part, not the tag. Keeping the first part
        // (the tag) made every aliased template unmatchable: "31% increased Critical Damage Bonus with Spears"
        // was looked up as "31% increased criticaldamagebonus with spear" and never found, which silently
        // dropped the jewel's whole crit-damage bonus and every tree line worded with an alias.
        string cleaned = Regex.Replace(template, @"\[([^\]]+)\]", m =>
        {
            var parts = m.Groups[1].Value.Split('|');
            return parts[^1].Trim();
        });
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
        // The word test is computed once per line, not once per template: the scan below touches
        // thousands of templates for a line the table cannot explain (an item's name, a base type).
        HashSet<string>? lineWords = null;
        foreach (var entry in entries)
        {
            lineWords ??= WordsOf(line);
            if (!ContainsAllWords(lineWords, entry.Words)) continue;
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