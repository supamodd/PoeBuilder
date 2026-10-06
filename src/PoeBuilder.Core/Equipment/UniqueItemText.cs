using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PoeBuilder.Core.Equipment;

/// <summary>How one line of a unique item's tooltip is drawn. The game colours and orders them:
/// the name, the base type, the implicit modifiers, then the explicit ones (and any property line the
/// text carries, which is not a modifier at all).</summary>
public enum UniqueLineKind { Name, BaseType, Implicit, Modifier, Note }

/// <summary>One line of a unique item's text. <see cref="Text"/> is the data's own template, which may
/// still carry the game's range notation (<c>+(30-40) to maximum Life</c>); <see cref="Resolved"/> is that
/// template with every range resolved to its maximum, which is what a planner shows before a real roll is
/// known. <see cref="Variants"/> is PoB2's own filter (empty = the line applies to every variant) and
/// <see cref="Tags"/> its tag filter (empty = unconditional).</summary>
public sealed record UniqueTextLine(string Text, string Resolved, UniqueLineKind Kind, int[] Variants, string[] Tags)
{
    public bool IsVariantFiltered => Variants.Length > 0;
    public bool IsTagged => Tags.Length > 0;
}

/// <summary>
/// A unique item's modifiers, from PoB2's own data (<c>src/Data/Uniques/*.lua</c> → <c>uniques.json</c>).
/// The pinned RePoE export carries unique identities but no modifiers, so this is the only verified
/// source of what a unique actually does.
/// <para>
/// Two things this class gets right that a plain text dump does not:
/// <list type="number">
/// <item>a unique can have <b>variants</b> (Morior Invictus: 29) and the lines are filtered by the
/// variant the game currently uses — PoB2 records that as <c>Selected Variant: N</c> (1-based; 0 means
/// "no explicit choice", which is the last listed variant, the game's current one);</item>
/// <item>the first <c>Implicits: N</c> modifiers of the selected variant are <b>implicits</b>, which is
/// how the game prints them and why the count sits above the implicit block.</item>
/// </list>
/// </para>
/// </summary>
public static class UniqueItemText
{
    public const string RarityHeader = "Rarity: UNIQUE";

    /// <summary>The game's range notation, e.g. <c>+(10-20) to Strength</c>. Same expression and same
    /// convention (the maximum roll) as <c>UniqueTextParser.ResolveRanges</c>, which delegates here.</summary>
    private static readonly Regex RangeNotation = new(@"\((-?\d+(?:\.\d+)?)-(-?\d+(?:\.\d+)?)\)", RegexOptions.Compiled);
    private static readonly Regex ImplicitCountLine = new(@"^Implicits?\s*:\s*(\d+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PropertyLine = new(@"^(?:Quality|Sockets?|Item Level|Requires|Level|League|Note|Radius)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Resolves the game's range notation to its maximum roll — the same convention the pinned
    /// implicits use, so a planner-built unique shows the ceiling of every range.</summary>
    public static string Resolve(string line) => RangeNotation.Replace(line ?? "", match => match.Groups[2].Value);

    /// <summary>The first numeric range (<c>(min-max)</c>) a template carries, if any. A unique line with
    /// exactly one range (e.g. <c>+(30-40) to maximum Life</c>) can be rolled in the editor; a line with
    /// none (a static mod) or more than one is best edited as free text.</summary>
    public static bool TryGetRange(string? line, out decimal min, out decimal max)
    {
        min = max = 0;
        if (string.IsNullOrEmpty(line)) return false;
        var match = RangeNotation.Match(line);
        if (!match.Success) return false;
        if (!decimal.TryParse(match.Groups[1].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out min)
            || !decimal.TryParse(match.Groups[2].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out max))
            return false;
        // The game writes a negative stat with the sign outside the parens: "-(15-10)% to Cold Resistance"
        // means the range runs from -15% to -10%. When the char before the opening paren is a '-' both
        // bounds share that sign and must be negated, otherwise min lands above max and the range inverts.
        if (match.Index > 0 && line[match.Index - 1] == '-') { min = -min; max = -max; }
        // Defence-in-depth: data may still be unordered, and callers clamp into [min, max], so never
        // hand back an inverted band.
        if (min > max) (min, max) = (max, min);
        return true;
    }

    /// <summary>Writes a concrete roll into a range-bearing template, replacing <c>(min-max)</c> with the
    /// value (e.g. <c>+(30-40) to maximum Life</c> + 34 → <c>+34 to maximum Life</c>). Every range in the
    /// line is replaced, which is correct because the roll editor only enables lines with one range.</summary>
    public static string ApplyRoll(string template, decimal roll)
    {
        return RangeNotation.Replace(template ?? "", match =>
        {
            decimal value = match.Index > 0 && template[match.Index - 1] == '-' ? Math.Abs(roll) : roll;
            return value.ToString(CultureInfo.InvariantCulture);
        });
    }

    /// <summary>The first bare number of a concrete line, used to read the current roll back out of the
    /// text the editor shows (the resolved <c>+(40) to maximum Life</c> → 40).</summary>
    public static bool TryFirstNumber(string? line, out decimal value)
    {
        value = 0;
        if (string.IsNullOrEmpty(line)) return false;
        var match = Regex.Match(line, @"-?\d+(?:\.\d+)?");
        return match.Success && decimal.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }


    /// <summary>The variant PoB2 says the game uses: its <c>Selected Variant</c> when it recorded one,
    /// otherwise the last listed variant (the data's "Current").</summary>
    /// <summary>A variant name that marks an old version of the item. PoB2 writes these either as a bare
    /// "Pre 0.4.0" or as a descriptive suffix — Morior Invictus' variants are "Spirit (Pre 0.2.0)",
    /// "Life (Pre 0.4.0)", … — so the <c>Pre &lt;version&gt;</c> marker is matched anywhere in the name,
    /// not just at its start. Such historical variant lines are dropped so only the current version stays,
    /// matching the request to keep just the current game version. Descriptive current variants (plain
    /// "Spirit", "Life", "All Resistances" with no "(Pre …)" suffix) are not touched.</summary>
    private static readonly Regex OldVersionVariant = new(@"\bPre\s+\d", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True when a variant name marks an old version of the item ("Pre 0.4.0"): the rolling of
    /// that item's mods changed since then, so the planner keeps only the current version.</summary>
    public static bool IsOldVersion(string? name) => !string.IsNullOrEmpty(name) && OldVersionVariant.IsMatch(name);

    public static int CurrentVariant(UniqueData data) =>
        data.SelectedVariant > 0 && data.SelectedVariant <= data.Variants.Length
            ? data.SelectedVariant
            : Math.Max(1, data.Variants.Length);

    /// <summary>Every variant number the data lists (1-based), or a single unnamed one when it lists none.
    /// Old-version variants (\"Pre 0.4.0\") are dropped — only the current version of the item is kept.</summary>
    public static IReadOnlyList<int> Variants(UniqueData data)
    {
        if (data.Variants.Length == 0) return [1];
        var kept = Enumerable.Range(1, data.Variants.Length).Where(v => !IsOldVersion(data.Variants[v - 1])).ToArray();
        return kept.Length == 0 ? [Math.Max(1, data.Variants.Length)] : kept;
    }

    /// <summary>The variant's own name ("Current", "Life"), with a fallback for a variant number the data
    /// does not name — a unique without variants has exactly one, unnamed, variant.</summary>
    public static string VariantName(UniqueData data, int variant) =>
        variant >= 1 && variant <= data.Variants.Length ? data.Variants[variant - 1]
        : data.Variants.Length == 0 && variant == 1 ? "Current" : "Variant " + variant;

    /// <summary>The modifier lines of one variant, in the data's own order, with the implicit block split
    /// off and every range resolved. <paramref name="includeAllVariants"/> keeps the lines of every other
    /// variant too (the editor offers that so nothing looks lost), flagging them by
    /// <see cref="UniqueTextLine.Variants"/>.</summary>
    public static IReadOnlyList<UniqueTextLine> Lines(UniqueData data, int variant, bool includeAllVariants = false)
    {
        var lines = new List<UniqueTextLine>();
        int implicits = Math.Max(0, data.Implicits), index = 0;
        foreach (var mod in data.Mods)
        {
            // Old-version variant lines ("Pre 0.4.0") are dropped entirely — only the current version of
            // the item is kept. This also keeps them out of the "all variants" personal-mod offer list.
            if (mod.Variants.Length > 0 && mod.Variants.All(v => v >= 1 && v <= data.Variants.Length && IsOldVersion(data.Variants[v - 1]))) continue;
            if (mod.Variants.Length > 0 && !includeAllVariants && !mod.Variants.Contains(variant)) continue;
            // PoB2's "Implicits: N" counts the first N lines of the block: the game prints them above the
            // explicit ones. A line dropped by the variant filter therefore does not consume a slot.
            var kind = index < implicits ? UniqueLineKind.Implicit : UniqueLineKind.Modifier;
            lines.Add(new(mod.Line, Resolve(mod.Line), kind, mod.Variants, mod.Tags));
            index++;
        }
        return lines;
    }

    /// <summary>
    /// The unique's own modifier lines that the item does not carry yet — its "personal mods". Every variant
    /// is searched, which is what makes a line the item does not show reachable: a Morior Invictus rolls one of
    /// thirty modifiers ("+10 to Spirit per Socket filled", "+60 to maximum Life per Socket filled", …) and the
    /// game prints only the live variant's. <paramref name="present"/> carries the templates already on the
    /// item, so nothing is offered twice; the item's text stays the single source of truth until a line is
    /// explicitly taken.
    /// </summary>
    public static IReadOnlyList<UniqueTextLine> PersonalMods(UniqueData data, IEnumerable<string> present)
    {
        var seen = new HashSet<string>(present ?? [], StringComparer.Ordinal);
        var lines = new List<UniqueTextLine>();
        foreach (var line in Lines(data, 1, includeAllVariants: true))
            if (seen.Add(line.Text)) lines.Add(line);
        return lines;
    }

    /// <summary>The variant(s) a line belongs to, as the names the data lists ("Spirit", "Current"), or an
    /// empty string for a line that applies to every variant.</summary>
    public static string VariantLabel(UniqueData data, UniqueTextLine line) =>
        line.Variants.Length == 0 ? "" : string.Join(", ", line.Variants.Select(v => VariantName(data, v)));

    /// <summary>A unique's full tooltip layout: its name, its base type, then the modifier lines with the
    /// implicit block first — exactly the order the game prints them in.</summary>
    public static IReadOnlyList<UniqueTextLine> Layout(string name, UniqueData data, int variant, string? baseType = null)
    {
        var lines = new List<UniqueTextLine>
        {
            new(name, name, UniqueLineKind.Name, [], []),
            new(baseType ?? data.BaseType, baseType ?? data.BaseType, UniqueLineKind.BaseType, [], [])
        };
        lines.AddRange(Lines(data, variant));
        return lines;
    }

    /// <summary>The item text of a planner-built unique (the same shape our importer reads back), with
    /// every range at its maximum. An empty string when the data has no modifier lines at all, so a
    /// caller can keep whatever the user typed instead of replacing it with a stub.
    /// <para>Lines end with <c>\n</c>, not the platform's newline: this text is stored in a build file and
    /// read back by a parser that must behave the same on every system.</para></summary>
    public static string Build(string name, UniqueData data, int variant, string? baseType = null)
    {
        var lines = Lines(data, variant);
        if (lines.Count == 0) return "";
        var text = new StringBuilder();
        foreach (var line in new[] { RarityHeader, name, baseType ?? data.BaseType }) text.Append(line).Append('\n');
        int implicits = lines.Count(l => l.Kind == UniqueLineKind.Implicit);
        if (implicits > 0) text.Append("Implicits: ").Append(implicits).Append('\n');
        foreach (var line in lines) text.Append(line.Resolved).Append('\n');
        return text.ToString().TrimEnd('\n');
    }

    /// <summary>Reads an item text (an imported unique, or one this class built) back into typed lines,
    /// so the editor can show it in game colours instead of a plain note. The text's own lines are
    /// authoritative: nothing is rewritten, and a line the text marks as a property stays a
    /// <see cref="UniqueLineKind.Note"/> rather than pretending to be a modifier.</summary>
    public static IReadOnlyList<UniqueTextLine> Parse(string? text)
    {
        var lines = new List<UniqueTextLine>();
        if (string.IsNullOrWhiteSpace(text)) return lines;
        bool header = true, name = true, baseType = true;
        int implicits = 0;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.All(c => c == '-')) continue;
            if (header && line.StartsWith("Rarity:", StringComparison.OrdinalIgnoreCase)) { header = false; continue; }
            header = false;
            var implicitMatch = ImplicitCountLine.Match(line);
            if (implicitMatch.Success) { implicits = int.Parse(implicitMatch.Groups[1].Value); continue; }
            if (name) { name = false; lines.Add(new(line, line, UniqueLineKind.Name, [], [])); continue; }
            if (baseType && !PropertyLine.IsMatch(line)) { baseType = false; lines.Add(new(line, line, UniqueLineKind.BaseType, [], [])); continue; }
            baseType = false;
            lines.Add(new(line, line, PropertyLine.IsMatch(line) ? UniqueLineKind.Note : UniqueLineKind.Modifier, [], []));
        }
        // Implicits are printed first, so the declared count converts that many leading modifiers.
        int converted = 0;
        for (int i = 0; i < lines.Count && converted < implicits; i++)
        {
            if (lines[i].Kind != UniqueLineKind.Modifier) continue;
            lines[i] = lines[i] with { Kind = UniqueLineKind.Implicit };
            converted++;
        }
        return lines;
    }
}
