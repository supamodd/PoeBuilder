using System.Text.Json;

namespace PoeBuilder.Core.Localization;

/// <summary>One entity's Russian text, as produced by build/extract-poe2db-ru.ps1.</summary>
public sealed record LocalizedName(string En, string Ru);

/// <summary>Russian text for the game data, keyed by the English name we already store. This is NOT a
/// translation of the application UI (that is Localization); it is the Russian side of the pinned GGG
/// data, which ships in English only. Anything not present here falls back to the English text, so a
/// missing entry shows the real English name instead of an invented Russian one.</summary>
public sealed class GameStrings
{
    private static readonly GameStrings Empty = new(new Dictionary<string, LocalizedName>(StringComparer.Ordinal),
        new Dictionary<string, TreeText>(StringComparer.Ordinal), new Provenance());

    /// <summary>Normalises a name to the key the extractor used: lowercase, apostrophes dropped, every
    /// run of non-alphanumerics removed. "Ancestor's Cry" and "Ancestor S Cry" therefore meet.</summary>
    public static string Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name.Trim().ToLowerInvariant())
            if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
            else if (c == '\'' || c == '\u2019') continue;
        return sb.ToString();
    }

    /// <summary>Strips the machine-readable GGG stat id out of a "[Id|Readable text]" placeholder,
    /// leaving only the text a player should read. Our own tree and the Russian tree both carry those
    /// placeholders so the calculation can identify a stat, but the id is not something to paint: the
    /// owner's screenshot read "[Allies|Союзники]" because the English id was printed next to the
    /// Russian word. A placeholder with no "|", or a bracket that is not one, is left untouched.</summary>
    public static string Clean(string? line)
    {
        if (string.IsNullOrEmpty(line) || line.IndexOf('|') < 0) return line ?? "";
        return System.Text.RegularExpressions.Regex.Replace(line, @"\[([A-Za-z0-9_]+)\|([^\]]*)\]", "$2");
    }

    private readonly Dictionary<string, LocalizedName> _names;
    private readonly Dictionary<string, TreeText> _tree;
    // Modifier text is keyed by its own English template, folded the same way the archive folds it, so the
    // lookup does not care whether one side wrote "(1-2) %" and the other "(1—2)%".
    private readonly Dictionary<string, string> _mods = new(StringComparer.Ordinal);

    public Provenance Source { get; }
    public bool IsEmpty => _names.Count == 0 && _tree.Count == 0 && _mods.Count == 0;

    public GameStrings(Dictionary<string, LocalizedName> names, Dictionary<string, TreeText> tree, Provenance provenance)
    {
        _names = names; _tree = tree; Source = provenance;
    }

    /// <summary>The Russian name for an English one, or null when this entity has no translation. A null
    /// is meaningful: it tells the caller to fall back rather than to invent.</summary>
    public LocalizedName? Find(string? english)
    {
        var key = Key(english);
        return key.Length != 0 && _names.TryGetValue(key, out var hit) ? hit : null;
    }

    /// <summary>Russian name when the interface is Russian and a translation exists; English otherwise.
    /// Falls back to the English input in both cases, so this is always safe to display.</summary>
    public string Name(string? english, bool russian)
    {
        if (string.IsNullOrWhiteSpace(english)) return "";
        if (!russian) return english;
        var hit = Find(english);
        return hit is null ? english : hit.Ru;
    }

    /// <summary>Russian text for a tree node, matched on OUR node id. Tree ids are the one join key in
    /// this data that is an identity rather than a name, so it is used directly.</summary>
    public TreeText? Node(string? nodeId) =>
        nodeId is not null && _tree.TryGetValue(nodeId, out var hit) ? hit : null;

    /// <summary>The Russian text of a modifier, looked up by the English template we ship, or null when
    /// this modifier has no translation (the caller then shows the English text unchanged).</summary>
    public string? Mod(string? englishTemplate) =>
        englishTemplate is not null && _mods.TryGetValue(FoldKey(englishTemplate), out var ru) ? ru : null;

    /// <summary>Folds a modifier template to a lookup key: one dash for every dash-like character, no
    /// whitespace at all, lower case. The two sources spell the same modifier differently ("+(25-50)%" vs
    /// "+(25—50) %"), and this is the whole difference between a hit and a miss.</summary>
    public static string FoldKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is >= '\u2010' and <= '\u2015' or '\u2212') { sb.Append('-'); continue; }
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static GameStrings Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        using var stream = File.OpenRead(path);
        var doc = JsonSerializer.Deserialize<LocaleFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (doc?.Names is null || doc.Tree is null) return Empty;
        var names = new Dictionary<string, LocalizedName>(StringComparer.Ordinal);
        foreach (var (key, pair) in doc.Names)
        {
            // The extractor already writes normalised keys; re-normalising keeps a hand-edited file safe.
            var k = Key(pair.En is { Length: > 0 } ? pair.En : key);
            if (k.Length != 0 && pair.Ru is { Length: > 0 }) names[k] = new(pair.En ?? key, pair.Ru);
        }
        var tree = new Dictionary<string, TreeText>(StringComparer.Ordinal);
        foreach (var (id, text) in doc.Tree) if (text?.Name is { Length: > 0 }) tree[id] = text;
        var loaded = new GameStrings(names, tree, doc.Provenance ?? new Provenance());
        if (doc.Mods is not null)
            foreach (var pair in doc.Mods)
            {
                var key = FoldKey(pair.Value?.En);
                if (key.Length != 0 && pair.Value?.Ru is { Length: > 0 } ru) loaded._mods[key] = ru;
            }
        return loaded;
    }

    private sealed class LocaleFile
    {
        public Provenance? Provenance { get; set; }
        public Dictionary<string, TreeText>? Tree { get; set; }
        public Dictionary<string, LocalizedName>? Names { get; set; }
        public Dictionary<string, LocalizedName>? Mods { get; set; }
    }
}

/// <summary>Russian name and stat lines of one tree node. Stats keep the English id skeleton so the
/// calculation and the tree parser are unaffected; only the readable half is translated.</summary>
public sealed record TreeText(string Name, string[]? Stats, string? En);

/// <summary>Where the Russian text came from and how much of it arrived. Surfaced in the UI because the
/// source is a community wiki, not GGG, and that is not something to hide.</summary>
public sealed record Provenance
{
    public string Source { get; init; } = "";
    public string Note { get; init; } = "";
    public int TreeNodes { get; init; }
    public int StatLinesJoined { get; init; }
    public int StatLinesKeptEn { get; init; }
    public int NamesJoined { get; init; }
    public int Gems { get; init; }
    public int Bases { get; init; }
    public int Uniques { get; init; }
    public int GemsUnmatched { get; init; }
    public int BasesUnmatched { get; init; }
    public int UniquesUnmatched { get; init; }
}