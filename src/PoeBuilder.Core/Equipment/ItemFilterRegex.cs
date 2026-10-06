using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PoeBuilder.Core.Equipment;

/// <summary>How one picked modifier takes part in the finished search string.</summary>
public enum RegexRequirement { None, Required, Optional, Excluded }

/// <summary>How the picked modifiers combine with each other.</summary>
public enum RegexLogic { All, Any, Mixed }

/// <summary>One modifier as the generator sees it: the game's own template text and the numeric bounds the
/// player typed for it. A null bound means "any value".</summary>
public sealed record RegexClause(string Template, decimal? Min, decimal? Max, RegexRequirement Requirement);

/// <summary>The finished search string plus everything the tab has to say about it.</summary>
public sealed record RegexResult(string Text, int Length, int Limit, bool Overflow, IReadOnlyList<string> Notes)
{
    public int Remaining => Limit - Length;
}

/// <summary>
/// Builds the search strings PoE2's item filter understands: a simplified regex typed straight into the
/// game's search box, to pick items off a trader, a stash or a map device.
/// <para>
/// The whole feature rests on one rule — the string is built from the game's <b>own</b> modifier templates
/// (the pinned catalog) and never from hand-written fragments, so it cannot describe a modifier the data
/// does not have. What this class adds on top of the raw text is the three things the game leaves to the
/// player: turning a rolled value into a pattern, honouring a numeric bound, and packing several modifiers
/// into the search box's character limit.
/// </para>
/// <para>
/// Number handling deserves a note, because the pinned templates carry the generator's own range notation
/// rather than a rolled line: <c>"Adds 1 to (2-3) Cold damage to Attacks"</c> rolls as
/// <c>"Adds 8 to 12 Cold damage to Attacks"</c>. A template is therefore read as a shape — its digits
/// become <c>\d+</c> (or the pattern for a typed bound), its bracketed ranges are unwrapped, and the doubled
/// "to" they leave behind is folded away. What comes out matches the rolled line, not the template.
/// </para>
/// </summary>
public static class ItemFilterRegex
{
    /// <summary>The game refuses a longer search string outright, so the generator has to fit inside it.</summary>
    public const int CharacterLimit = 250;

    private static readonly Regex RangeNotation = new(@"\(\s*(\d+(?:[.,]\d+)?)\s*-\s*(\d+(?:[.,]\d+)?)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    /// <summary>A rolled figure reads "N to M", while the template spells the same thing several ways:
    /// "Adds (5-6) to (9-11)", "Adds 1 to (2-3)", "Adds 10 to 20". Every run of numbers joined by "to" is one
    /// rolled figure, so it collapses to its first and last number — exactly the two the item shows.</summary>
    private static readonly Regex Figure = new(@"\d+(?:[.,]\d+)?(?:\s+to\s+\d+(?:[.,]\d+)?)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Number = new(@"\d+(?:[.,]\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Characters the game's parser gives meaning to. Everything else — including the Russian text —
    /// is a literal.</summary>
    private static readonly char[] Special = ['\\', '^', '$', '.', '|', '?', '*', '+', '(', ')', '[', ']', '{', '}', '/', '\'', '!', '#'];

    /// <summary>Assembles the search string from the picked modifiers.</summary>
    public static RegexResult Build(IReadOnlyList<RegexClause> clauses, RegexLogic logic)
    {
        var required = new List<string>();
        var optional = new List<string>();
        var excluded = new List<string>();
        foreach (var clause in clauses)
        {
            if (string.IsNullOrWhiteSpace(clause.Template)) continue;
            var pattern = TextPattern(clause.Template, clause.Min, clause.Max);
            switch (clause.Requirement)
            {
                case RegexRequirement.Required: required.Add(pattern); break;
                case RegexRequirement.Optional: optional.Add(pattern); break;
                case RegexRequirement.Excluded: excluded.Add(pattern); break;
            }
        }

        var parts = new List<string>();
        switch (logic)
        {
            case RegexLogic.Any:
                // Everything picked is one alternative: "a , b , c" matches an item carrying any of them.
                var all = required.Concat(optional).ToList();
                if (all.Count > 0) parts.Add(string.Join(" , ", all));
                break;
            case RegexLogic.Mixed:
                if (required.Count > 0) parts.Add(string.Join(" ", required));
                // The optional ones become one group: the item must carry every required modifier and at
                // least one of these.
                if (optional.Count > 0) parts.Add("(" + string.Join(" , ", optional) + ")");
                break;
            default:
                // "All": the optional markers mean "this one too", so both lists are simply joined.
                if (required.Count + optional.Count > 0) parts.Add(string.Join(" ", required.Concat(optional)));
                break;
        }
        // A "!" prefix removes an item from the result, which is why it is AND-ed rather than OR-ed in.
        foreach (var pattern in excluded) parts.Add("!" + pattern);

        var text = string.Join(" ", parts.Where(p => p.Length > 0));
        var notes = new List<string>();
        if (text.Length == 0) notes.Add("Empty");
        if (text.Length > CharacterLimit) notes.Add("Overflow");
        return new(text, text.Length, CharacterLimit, text.Length > CharacterLimit, notes);
    }

    /// <summary>
    /// One modifier as a pattern: the template's fixed words escaped, its rolled digits turned into a
    /// matcher, and the Russian "е"/"ё" pair folded together.
    /// </summary>
    /// <param name="min">The lowest value the first number of the line may take, or null for any.</param>
    /// <param name="max">The highest, or null. Both null leaves every number open.</param>
    public static string TextPattern(string template, decimal? min = null, decimal? max = null)
    {
        var shape = Fold(template);
        // The template is walked once, splitting it into literal words and rolled numbers: the words are
        // escaped so they stay literal, the numbers become matchers. Escaping the whole string instead
        // would escape the matchers this very method has just written.
        var result = new StringBuilder(shape.Length + 16);
        int position = 0;
        bool first = true;
        foreach (Match match in Number.Matches(shape))
        {
            result.Append(Escape(shape[position..match.Index]));
            // The first number of the line is the one the bounds apply to, which is how a player reads
            // "Adds damage": its lower figure is the figure they care about. Anything after it stays open,
            // so a bound never silently constrains a number the player did not mean.
            result.Append(first ? NumberPattern(min, max) : Any);
            first = false;
            position = match.Index + match.Length;
        }
        result.Append(Escape(shape[position..]));
        return FoldYo(result.ToString());
    }

    /// <summary>The open pattern for a number of any size. Item lines carry whole numbers, and a shorter
    /// pattern is worth real characters against the game's 250-character limit.</summary>
    public const string Any = @"\d+";

    /// <summary>A regex matching an integer in the closed range, or the pattern for the whole number line
    /// when no usable bound was given. A fractional or absurdly wide bound narrows nothing: the number
    /// stays open, because a filter that silently matches the wrong items is worse than a broad one.</summary>
    public static string NumberPattern(decimal? min, decimal? max)
    {
        if (min is null && max is null) return Any;
        if (Fractional(min) || Fractional(max)) return Any;
        decimal low = min ?? 0m, high = max ?? int.MaxValue;
        if (low > high) (low, high) = (high, low);
        if (high < 0m) return Any;
        if (min is not null && max is not null && high == low) return Format(low);
        // A bound that reaches into digits the game cannot roll would only waste the limit.
        if (low > 0m && high - low > 100_000m) return Any;
        return RangePattern((int)Math.Floor(low), (int)Math.Ceiling(high));
    }

    private static bool Fractional(decimal? value) => value is { } v && v != decimal.Truncate(v);

    private static string Format(decimal value) => value == decimal.Truncate(value)
        ? ((long)value).ToString(CultureInfo.InvariantCulture)
        : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The shortest readable pattern for every integer between <paramref name="lo"/> and
    /// <paramref name="hi"/>. A window of one digit length collapses into one character class per position
    /// ("10-79" becomes "1[0-7]"), a span of several lengths becomes an alternation of those windows — which
    /// is shorter than spelling out a quantifier per digit and is easier to read back in the game.
    /// </summary>
    private static string RangePattern(int lo, int hi)
    {
        if (hi < lo) (lo, hi) = (hi, lo);
        if (lo == hi) return lo.ToString(CultureInfo.InvariantCulture);
        var windows = new List<string>();
        for (int digits = Digits(lo); digits <= Digits(hi); digits++)
        {
            int from = Math.Max(lo, Pow10(digits - 1));
            int to = Math.Min(hi, Pow10(digits) - 1);
            if (from > to) continue;
            windows.Add(Window(from, to, digits));
        }
        if (windows.Count == 0) return Any;
        return windows.Count == 1 ? windows[0] : string.Join("|", windows);
    }

    /// <summary>One window of a single digit length, as the shortest pattern that matches exactly it.
    /// <para>
    /// A character class is only correct when the digits after the one that changes are free to be anything:
    /// "10-79" is <c>[1-7]\d</c>, because every 10-19, 20-29 … 70-79 is in range. "10-20" is not — its last
    /// digit is 0 on both ends, and a class per position would collapse the whole window to "10 or 20", silently
    /// dropping 11-19. Such a window is therefore split at the digit that changes, one alternative per value
    /// of it, and each piece folded on its own.
    /// </para></summary>
    private static string Window(int from, int to, int digits)
    {
        if (from > to) (from, to) = (to, from);
        if (from == to) return from.ToString(CultureInfo.InvariantCulture);
        string low = from.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
        string high = to.ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
        int split = 0;
        while (split < digits && low[split] == high[split]) split++;
        // Every digit agrees: the window is one literal number.
        if (split == digits) return low;

        int tail = digits - split - 1;
        bool freeTail = low[(split + 1)..].All(c => c == '0') && high[(split + 1)..].All(c => c == '9');
        if (freeTail)
        {
            // One class for the digit that changes, then "anything" for the ones behind it.
            string head = low[..split] + Class(low[split], high[split]);
            return head + (tail == 0 ? "" : tail == 1 ? @"\d" : $@"\d{{{tail}}}");
        }

        // The tail is pinned, so each value of the changing digit gets its own branch and is folded again.
        var alternatives = new List<string>();
        for (char c = low[split]; c <= high[split]; c++)
        {
            int block = int.Parse(low[..split] + c, CultureInfo.InvariantCulture) * Pow10(tail);
            int pieceFrom = Math.Max(from, block);
            int pieceTo = Math.Min(to, block + Pow10(tail) - 1);
            if (pieceFrom > pieceTo) continue;
            alternatives.Add(Window(pieceFrom, pieceTo, digits));
        }
        return string.Join("|", alternatives);
    }

    /// <summary>One character class, or the plain digit when both ends are the same.</summary>
    private static string Class(char low, char high)
    {
        if (low == high) return low.ToString();
        if (low == '0' && high == '9') return @"\d";
        return $"[{low}-{high}]";
    }

    private static int Digits(int value) => Math.Max(1, value.ToString(CultureInfo.InvariantCulture).Length);

    private static int Pow10(int exponent)
    {
        int value = 1;
        for (int i = 0; i < exponent; i++) value *= 10;
        return value;
    }

    /// <summary>The template reduced to the shape a rolled line has.
    /// <para>
    /// Two rewrites do it. A bracketed range is one rolled value, so "+(10-20)%" loses its brackets and its
    /// upper figure and becomes a single number. A "N to M" run is one rolled figure, so "Adds 1 to (2-3)"
    /// becomes the two numbers the item shows — its lower figure and the range's lower figure.
    /// </para></summary>
    public static string Fold(string template)
    {
        var unwrapped = RangeNotation.Replace(template, match =>
        {
            // Brackets may be what separates the number from a neighbouring word or a sign, so a space is put
            // back wherever dropping them would glue two pieces of text together.
            string before = match.Index > 0 && Glues(template[match.Index - 1]) ? " " : "";
            string after = match.Index + match.Length < template.Length && Glues(template[match.Index + match.Length]) ? " " : "";
            return before + match.Groups[1].Value + after;
        });
        var collapsed = Figure.Replace(unwrapped, run =>
        {
            var numbers = Number.Matches(run.Value);
            return numbers.Count > 2
                ? numbers[0].Value + " to " + numbers[^1].Value
                : run.Value;
        });
        return Whitespace.Replace(collapsed, " ").Trim();
    }

    /// <summary>Would dropping a bracket here join two words into one? Only a letter, a digit or the "#"
    /// placeholder can do that, so a sign or a space already separates the parts.</summary>
    private static bool Glues(char c) => char.IsLetterOrDigit(c) || c == '#';

    /// <summary>
    /// Russian affix text comes in both spellings and the game writes either, so one letter becomes a
    /// two-letter class. It walks the finished pattern and skips escapes and character classes, so only
    /// literal words are ever folded.
    /// </summary>
    private static string FoldYo(string pattern)
    {
        var result = new StringBuilder(pattern.Length + 8);
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '[')
            {
                int end = pattern.IndexOf(']', i);
                if (end < 0) { result.Append(pattern[i..]); break; }
                result.Append(pattern, i, end - i + 1); i = end; continue;
            }
            if (c == '\\')
            {
                if (i + 1 >= pattern.Length) { result.Append(c); break; }
                result.Append(pattern, i, 2); i++; continue;
            }
            if (c is 'е' or 'ё') { result.Append("[её]"); continue; }
            result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>Escapes the characters the game's parser gives meaning to, so literal affix words stay
    /// literal.</summary>
    public static string Escape(string literal)
    {
        var result = new StringBuilder(literal.Length + 8);
        foreach (char c in literal)
        {
            if (Array.IndexOf(Special, c) >= 0) result.Append('\\');
            result.Append(c);
        }
        return result.ToString();
    }
}

