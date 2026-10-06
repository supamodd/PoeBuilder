using System.Globalization;
using System.Text.RegularExpressions;

namespace PoeBuilder.Core.Filters;

/// <summary>
/// Reads a hand-written or third-party <c>.filter</c> file back into the editor.
/// <para>
/// This is the direction that has to be forgiving. A file the game itself loaded is known to be valid, so
/// the reader never rejects it for an unfamiliar line: anything it cannot place is collected as
/// <see cref="Unknown"/> and shown to the player, rather than thrown away. Throwing on the first unknown
/// keyword would make the editor useless for exactly the popular filters it is meant to open.
/// </para>
/// </summary>
public sealed record FilterReadResult(
    string Name,
    List<FilterBlock> Blocks,
    int DropLevel,
    bool Minimap,
    List<string> Unknown);

public static class FilterReader
{
    private static readonly Regex BlockStart = new(@"^\s*(Show|Hide)\s*(?:#\s*(.*))?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Quoted = new("^([A-Za-z]+)\\s+\"([^\"]*)\"\\s*$", RegexOptions.Compiled);
    private static readonly Regex Numbers = new(@"^([A-Za-z]+)\s+(-?\d+)(?:\s+(-?\d+))?\s*$", RegexOptions.Compiled);
    private static readonly Regex Icon = new(@"^MinimapIcon\s+(-?\d+)\s+(.+)$", RegexOptions.Compiled);

    public static FilterReadResult Read(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024)
            throw new FilterFormatException("Filter file is larger than 2 MiB.");
        return Read(File.ReadLines(path));
    }

    public static FilterReadResult Read(IEnumerable<string> lines)
    {
        var blocks = new List<FilterBlock>();
        var unknown = new List<string>();
        var name = "";
        var dropLevel = 0;
        var minimap = true;
        FilterBlock? current = null;
        List<FilterCondition>? conditions = null;
        FilterStyle? style = null;
        var disabled = false;
        // True while the block being read is an unmarked Show at the very top: it may turn out to be the file's
        // own header rather than a rule, which is only known once its conditions have been read.
        var unmarked = false;

        void Flush()
        {
            if (current is null) return;
            // A candidate header carries no condition by now — its DropLevel and minimap switch were lifted out
            // as they were read — so it is the document's own settings block and not a rule. Everything else is
            // kept, including a first block that did carry conditions: that is how most third-party filters open.
            if (unmarked) { current = null; conditions = null; style = null; disabled = false; unmarked = false; return; }
            blocks.Add(current with { Conditions = [.. conditions ?? []], Style = style ?? new(), Disabled = disabled });
            current = null; conditions = null; style = null; disabled = false;
        }

        foreach (var raw in lines)
        {
            // A commented-out block header is a parked rule: the writer emits them, so the reader has to accept
            // them back rather than reading a disabled block as active.
            var commented = raw.TrimStart().StartsWith('#');
            var text = raw.TrimStart().TrimStart('#').Trim();
            if (text.Length == 0) continue;
            if (name.Length == 0 && raw.TrimStart().StartsWith('#') && raw.TrimStart().StartsWith("##"))
            {
                name = raw.TrimStart('#').Trim();
                continue;
            }

            var start = BlockStart.Match(text);
            if (start.Success)
            {
                // The writer marks its own header "Show # settings". A file from elsewhere opens with a plain
                // unconditional Show, which is only a header if it turns out to carry no real condition — so an
                // unmarked first block stays a candidate until its conditions have been read.
                bool marked = start.Groups[2].Value.Trim().Equals("settings", StringComparison.OrdinalIgnoreCase);
                Flush();
                // Marked: the writer's own header, always settings. Unmarked at the very top: a header too, but
                // only confirmed once nothing that could be a condition turns up under it.
                unmarked = marked || (blocks.Count == 0 && start.Groups[1].Value.Equals("Show", StringComparison.OrdinalIgnoreCase));
                current = new FilterBlock
                {
                    Kind = start.Groups[1].Value.Equals("Hide", StringComparison.OrdinalIgnoreCase) ? FilterBlockKind.Hide : FilterBlockKind.Show,
                    Comment = start.Groups[2].Success ? start.Groups[2].Value.Trim() : ""
                };
                disabled = commented;
                conditions = []; style = null;
                continue;
            }
            if (current is null) continue;

            // The header carries DropLevel and the minimap switch, which the parser would otherwise read as
            // conditions. They are settings, so they are lifted out — but only they: a first block that carries
            // a real condition is a rule after all, which is how most third-party filters begin.
            if (unmarked)
            {
                if (Numbers.Match(text) is { Success: true } header && header.Groups[1].Value.Equals("DropLevel", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(header.Groups[2].Value, out int level)) { dropLevel = level; continue; }
                if (Icon.Match(text) is { Success: true } marker)
                {
                    if (int.TryParse(marker.Groups[1].Value, out int index) && index < 0) minimap = false;
                    continue;
                }
                // Anything else here is a condition, so this block is a rule and not the header after all.
                unmarked = false;
            }

            conditions ??= []; style ??= new();
            var quoted = Quoted.Match(text);
            var numbers = Numbers.Match(text);
            if (quoted.Success && FilterCondition.IsKnownKeyword(quoted.Groups[1].Value))
            {
                conditions.Add(new FilterCondition { Keyword = quoted.Groups[1].Value, Value = quoted.Groups[2].Value });
                continue;
            }
            if (numbers.Success && FilterCondition.IsKnownKeyword(numbers.Groups[1].Value))
            {
                var keyword = numbers.Groups[1].Value;
                if (FilterCondition.IsFlag(keyword)) { conditions.Add(new FilterCondition { Keyword = keyword }); continue; }
                conditions.Add(numbers.Groups[3].Success
                    ? new FilterCondition { Keyword = keyword, IsRange = true, Min = int.Parse(numbers.Groups[2].Value, CultureInfo.InvariantCulture), Max = int.Parse(numbers.Groups[3].Value, CultureInfo.InvariantCulture) }
                    : new FilterCondition { Keyword = keyword, Value = numbers.Groups[2].Value });
                continue;
            }
            if (FilterCondition.IsFlag(text))
            {
                conditions.Add(new FilterCondition { Keyword = text });
                continue;
            }
            var read = ReadStyle(text, style);
            // Anything the reader could not place is kept for the player to look at, never dropped: a rule
            // that vanishes on import is worse than one shown as unsupported.
            if (read is null) unknown.Add(text);
            else style = read;
        }
        Flush();
        return new(name, blocks, dropLevel, minimap, unknown);
    }

    private static FilterStyle? ReadStyle(string text, FilterStyle style)
    {
        if (Icon.Match(text) is { Success: true } icon)
        {
            var index = int.Parse(icon.Groups[1].Value, CultureInfo.InvariantCulture);
            // "-1 <icon>" is the document's own minimap-off switch, not a rule's marker.
            return index < 0 ? style : style with { MinimapIcon = index, MinimapIconName = icon.Groups[2].Value.Trim() };
        }
        if (text.StartsWith("FontColor", StringComparison.OrdinalIgnoreCase))
            return Channel(text[9..]) is { } font ? style with { FontColor = font } : null;
        if (text.StartsWith("BorderColor", StringComparison.OrdinalIgnoreCase))
            return Channel(text[11..]) is { } border ? style with { BorderColor = border } : null;
        if (text.StartsWith("FontSize", StringComparison.OrdinalIgnoreCase) && int.TryParse(text[8..].Trim(), out var size))
            return style with { FontSize = size };
        if (text.StartsWith("BorderWidth", StringComparison.OrdinalIgnoreCase) && int.TryParse(text[11..].Trim(), out var width))
            return style with { BorderWidth = width };
        if (text.StartsWith("Sound", StringComparison.OrdinalIgnoreCase))
            return style with { Sound = Quoted.Match(text) is { Success: true } q ? q.Groups[2].Value : text[5..].Trim().Trim('"') };
        if (text.Equals("EnableFlare", StringComparison.OrdinalIgnoreCase)) return style with { EnableFlare = true };
        if (text.Equals("EnableGrey", StringComparison.OrdinalIgnoreCase)) return style with { EnableGrey = true };
        return null;
    }

    private static int[]? Channel(string text)
    {
        var values = new List<int>();
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) values.Add(value);
        return values.Count == 4 ? [.. values] : null;
    }
}
