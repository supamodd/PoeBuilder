using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PoeBuilder.Core.Calculation;

namespace PoeBuilder.Core.Equipment;

/// <summary>Reverse translation of an English affix line to a pinned mod + integer rolls.
/// Templates and item lines are normalized to words plus '#' value slots; a match requires
/// identical shapes and the same number of value slots as the mod has stats. Lives in Core because
/// both the importer (which stores the rolls) and the calculator (which must know whether a text
/// line is already covered by a stored roll) depend on the same normalization.</summary>
public sealed class ModLineMatcher
{
    // A number or a number range, optionally wrapped in parentheses: "(11-13)", "31", "+4.86", "-30".
    // The pattern deliberately does NOT swallow the whitespace around the value: eating a trailing
    // space after a bare number but not after "(11-13)" normalised "Adds 14 to 24 Cold damage to
    // Attacks" to "adds#to#cold damage to attacks" while its own template became
    // "adds # to # cold damage to attacks", so the whole "Adds X to Y" affix family never matched.
    private static readonly Regex RangeOrNumber =
        new(@"\(?-?\d+(?:\.\d+)?(?:\s*-\s*-?\d+(?:\.\d+)?)?\)?", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled);
    /// <summary>PoE2 stat markup inside the pinned mod templates. The game renders <c>[Tag]</c> as
    /// <c>Tag</c> and <c>[Tag|Display]</c> as <c>Display</c>, so the item text the importer sees never
    /// carries the tag. Normalising both sides the same way is what lets a line such as
    /// "Adds 23 to 36 Fire damage to Attacks" match its pinned template
    /// "Adds (11-16) to (21-26) [Fire] damage to [Attack|Attacks]"; leaving the markup in place made
    /// every such affix unmatchable ("fire damage to attack attacks" vs "fire damage to attacks").</summary>
    private static readonly Regex Markup = new(@"\[([^\[\]]*)\]", RegexOptions.Compiled);
    /// <summary>Leading display tags of an item line (<c>{enchant}</c>, <c>{rune}</c>, <c>{crafted}</c>…).</summary>
    private static readonly Regex DisplayTag = new(@"\{[^}]*\}", RegexOptions.Compiled);
    private readonly List<(string Template, ItemMod Mod)> _templates;
    private ModLineMatcher(List<(string, ItemMod)> templates) => _templates = templates;

    public static ModLineMatcher Build(GameCatalog catalog)
    {
        var list = new List<(string, ItemMod)>();
        foreach (var m in catalog.Mods.Values) Add(list, m);
        foreach (var m in catalog.JewelMods) Add(list, m);
        return new(list);

        static void Add(List<(string, ItemMod)> list, ItemMod mod)
        {
            if (mod.Text.Length == 0 || mod.Stats.Length == 0 || mod.Stats.Length > 4) return;
            var template = Normalize(mod.Text);
            if (!template.Contains('#')) return;
            list.Add((template, mod));
        }
    }

    public (ModRoll roll, ItemMod mod)? Match(string line) => Match(line, null);

    /// <summary>Matches a line, preferring the mods the item's own class can actually roll
    /// (<paramref name="preferredIds"/> — the base's pool). The pinned catalog words several mods
    /// identically for different classes ("+# to maximum Mana" exists both for rings and for
    /// two-handed weapons), so an unscoped tie-break can attribute a line to another class's mod.</summary>
    public (ModRoll roll, ItemMod mod)? Match(string line, IReadOnlySet<string>? preferredIds)
    {
        var norm = Normalize(line);
        int slots = norm.Count(c => c == '#');
        if (slots == 0 || slots > 4) return null;
        var numbers = Numbers(line);
        (string Template, ItemMod Mod)? fitting = null, pooled = null, any = null;
        foreach (var (template, mod) in _templates)
        {
            if (template != norm || mod.Stats.Length != slots || numbers.Length != mod.Stats.Length) continue;
            bool inPool = preferredIds is null || preferredIds.Contains(mod.Id);
            // Prefer a template whose pinned range actually contains the rolled values.
            bool fits = numbers.Zip(mod.Stats).All(p => p.First >= p.Second.Min && p.First <= p.Second.Max);
            if (fits && inPool) return (new ModRoll { Id = mod.Id, Values = numbers }, mod);
            if (fits) fitting ??= (template, mod);
            else if (inPool) pooled ??= (template, mod);
            any ??= (template, mod);
        }
        // Text matched but the roll is outside the pinned range of every candidate: keep the observed
        // values, validation accepts them for jewels and reports honestly elsewhere.
        // Text matched but the roll is outside the pinned range of every candidate. Before falling back to an
        // arbitrary same-shaped affix — which used to hand "31% increased Critical Damage Bonus with Spears" to
        // the jewel affix "of Hunting" (rolls 5-10%) whose JewelRadius… id the calculator deliberately skips,
        // losing the whole bonus — ask the game's own stat-text table which stat the line names. It is the same
        // table the tree pass uses, so the mapping is authoritative rather than a tie-break guess.
        if (fitting is null && ReverseStatTextMatcher.IsReady &&
            ReverseStatTextMatcher.TryMatch(line) is { Count: > 0 } byText)
        {
            var stat = new ModStat(byText[0].Id, decimal.MinValue, decimal.MaxValue);
            var synthetic = new ItemMod(stat.Id, "", "", 0, [], line, [stat]);
            return (new ModRoll { Id = stat.Id, Values = numbers }, synthetic);
        }
        var chosen = fitting ?? pooled ?? any;
        return chosen is null ? null : (new ModRoll { Id = chosen.Value.Mod.Id, Values = numbers }, chosen.Value.Mod);
    }

    /// <summary>True when the line has the same normalized shape as the mod's template, i.e. the
    /// stored roll for that mod came from this very line. The calculator uses this to decide whether
    /// a text line is already accounted for and must not be interpreted a second time.</summary>
    public static bool Covers(ItemMod mod, string line)
        => mod.Text.Length != 0 && Normalize(mod.Text) == Normalize(line);

    private static decimal[] Numbers(string line)
        => Regex.Matches(line, Number.ToString()).Select(m =>
            decimal.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Round(v, 0) : 0m).ToArray();

    /// <summary>Normalizes a line or template to lowercase words plus '#' value slots and '%', so
    /// "+(31-35)% to Fire Resistance" and "+33% to Fire Resistance" compare equal.</summary>
    public static string Normalize(string text)
    {
        var lowered = text.ToLowerInvariant().Replace('’', '\'').Replace('–', '-').Replace('—', '-');
        // Resolve the game's display markup to the wording a player sees: [Tag] -> Tag,
        // [Tag|Display] -> Display. Item lines carry no markup at all, so this is one-sided on them.
        lowered = Markup.Replace(lowered, m =>
        {
            var parts = m.Groups[1].Value.Split('|');
            return parts[^1];
        });
        lowered = DisplayTag.Replace(lowered, "");
        lowered = RangeOrNumber.Replace(lowered, "#");
        var sb = new StringBuilder();
        foreach (var c in lowered)
        {
            if (char.IsWhiteSpace(c)) { if (sb.Length == 0 || sb[^1] == ' ') continue; sb.Append(' '); }
            else if (char.IsLetter(c) || c == '%' || c == '#') sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
