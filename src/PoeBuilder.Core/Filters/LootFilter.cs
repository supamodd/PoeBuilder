namespace PoeBuilder.Core.Filters;

/// <summary>How a block treats the items that reach it: show and style them, or hide them outright.</summary>
public enum FilterBlockKind { Show, Hide }

/// <summary>Categories of items that can appear in a filter, grouped by rarity/tier.</summary>
public enum TierCategory
{
    Currency,
    Gem,
    Unique,
    Equipment,
    Socketables,
    Waystones
}

/// <summary>One condition of a block, in the game's own vocabulary. A filter is a list of these, AND-ed
/// inside a block and matched in block order — so the document holds the whole rule set rather than a tree.</summary>
public sealed record FilterCondition
{
    /// <summary>The keyword as the game spells it.</summary>
    public string Keyword { get; init; } = "ItemClass";
    /// <summary>The value: a quoted string for the text conditions, a bare number for the numeric ones. Empty
    /// for the flag conditions, which take no argument.</summary>
    public string Value { get; init; } = "";
    /// <summary>Numeric conditions may be read as a Min/Max pair instead of a single figure.</summary>
    public bool IsRange { get; init; }
    public int Min { get; init; }
    public int Max { get; init; }

    public static bool IsKnownKeyword(string keyword) => Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase);
    public static bool IsFlag(string keyword) => Flags.Contains(keyword, StringComparer.OrdinalIgnoreCase);
    public static bool IsTextual(string keyword) => Textual.Contains(keyword, StringComparer.OrdinalIgnoreCase);
    public static bool IsNumeric(string keyword) => Numeric.Contains(keyword, StringComparer.OrdinalIgnoreCase);
    public static readonly string[] Flags = ["HasSockets", "Corrupted", "Enchanted", "Archnemesis", "IsElder", "EnchantedPassiveNode", "Identified"];
    public static readonly string[] Textual = ["ItemClass", "BaseType", "Rarity", "Class"];
    public static readonly string[] Numeric = ["DropLevel", "ReqLevel", "Sockets", "Quality", "StackSize", "AreaLevel", "Height", "Width", "AltLevel", "ItemLevel"];
    public static readonly string[] Keywords = [.. Flags, .. Textual, .. Numeric];
    /// <summary>The rarity names the game accepts, in its own spelling.</summary>
    public static readonly string[] Rarities = ["Normal", "Magic", "Rare", "Unique", "Gem", "Currency", "Quest", "Relic"];
}

/// <summary>How an item looks when a Show block catches it. Every field is optional: an unset one is left out
/// of the file, so the game keeps its own default — which is usually what a player wants, since recolouring
/// every tier is a rarer edit than a single highlight.</summary>
public sealed record FilterStyle
{
    /// <summary>RGBA, 0–255. Null leaves the item's own rarity colour.</summary>
    public int[]? FontColor { get; init; }
    /// <summary>Background colour for the item (when not using the game's own rarity colour).</summary>
    public int[]? BackgroundColor { get; init; }
    /// <summary>Border colour for the item.</summary>
    public int[]? BorderColor { get; init; }
    /// <summary>Font size in pixels.</summary>
    public int? FontSize { get; init; }
    /// <summary>Border thickness in pixels.</summary>
    public int? BorderWidth { get; init; }
    /// <summary>The game's sound id or index for the item's sound effect.</summary>
    public string? Sound { get; init; }
    /// <summary>Whether the item has a flare effect.</summary>
    public bool EnableFlare { get; init; }
    /// <summary>Whether the item has a grey tint overlay.</summary>
    public bool EnableGrey { get; init; }
    /// <summary>Minimap marker: the index and its name. Only meaningful on a Show block.</summary>
    public int? MinimapIcon { get; init; }
    /// <summary>Minimap icon name (if the style has a minimap icon).</summary>
    public string? MinimapIconName { get; init; }

    // Static validation helpers
    public static bool IsFontSize(int size) => FontSizes.Contains(size);
    public static bool IsSound(string sound) => ValidSounds.Contains(sound, StringComparer.OrdinalIgnoreCase);
    public static bool IsMinimapIcon(int icon) => icon is >= 0 and <= 11;
    public static readonly string[] ValidSounds =
    [
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12", "13", "14", "15",
        "Alert", "Chime", "Drop", "Explosion", "Magic", "Melee", "Ranged", "Spell", "IdRes1", "IdRes2", "IdRes3"
    ];
    public static readonly string[] MinimapIcons =
    [
        "BlueCircle", "GreenCircle", "RedCircle", "WhiteCircle",
        "BlueSquare", "GreenSquare", "RedSquare", "WhiteSquare",
        "BlueStar", "GreenStar", "RedStar", "WhiteStar", "YellowStar"
    ];
    /// <summary>The font sizes the game accepts, offered by the editor as its own list.</summary>
    public static readonly int[] FontSizes = [18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 38, 40, 44, 45, 46];
    /// <summary>Alias kept for readability: the game's sound names, as the editor lists them.</summary>
    public static IReadOnlyList<string> Sounds => ValidSounds;
}

/// <summary>One rule in a filter: Show/Hide block with conditions and styling.</summary>
public sealed record FilterBlock
{
    public FilterBlockKind Kind { get; init; } = FilterBlockKind.Show;
    public string Comment { get; init; } = "";
    public bool Disabled { get; init; }
    public List<FilterCondition> Conditions { get; init; } = [];
    public FilterStyle Style { get; init; } = new();
}

/// <summary>One filter document: its own id, name, and rules. The document follows PoeBuilder Native
/// conventions — a .poefilter file in the library, a .filter file on disk.</summary>
public sealed record LootFilterDocument
{
    public const string FormatName = "PoeBuilder.Native.Filter";
    /// <summary>Creates an empty document with just the game and a name, the way a new filter or a preset starts.</summary>
    public static LootFilterDocument Create(string name) => new() { Name = name };
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Game { get; init; } = "poe2";
    public int DropLevel { get; init; }
    public bool Minimap { get; init; } = true;
    public List<FilterBlock> Blocks { get; init; } = [];
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Validation rules for filters.</summary>
public static class FilterValidation
{
    /// <summary>Largest number of rule blocks the editor will hold; the check keeps the list from growing
    /// without bound while the player adds a rule at a time.</summary>
    public const int MaximumBlocks = 200;
    public static void Validate(LootFilterDocument filter)
    {
        if (filter.Name.Length is 0 or > 80) throw new FilterFormatException("Filter name must be 1–80 characters.");
        foreach (var block in filter.Blocks) Validate(block);
    }

    public static void Validate(FilterBlock block)
    {
        foreach (var condition in block.Conditions) Validate(condition);
        Validate(block.Style, block.Kind);
        // A Show block with no conditions would match every item, which makes no sense in a
        // filter editor — the player would see nothing but that block's style everywhere.
        if (block.Kind == FilterBlockKind.Show && block.Conditions.Count == 0)
            throw new FilterFormatException("A Show block needs at least one condition, or it would style every item.");
    }

    public static void Validate(FilterCondition condition)
    {
        if (!FilterCondition.IsKnownKeyword(condition.Keyword))
            throw new FilterFormatException($"The game has no filter condition called \"{condition.Keyword}\".");
        if (FilterCondition.IsFlag(condition.Keyword))
        {
            if (!string.IsNullOrEmpty(condition.Value) || condition.IsRange)
                throw new FilterFormatException($"{condition.Keyword} takes no value.");
            return;
        }
        if (FilterCondition.IsTextual(condition.Keyword))
        {
            if (condition.IsRange) throw new FilterFormatException($"{condition.Keyword} takes a name, not a range.");
            if (string.IsNullOrWhiteSpace(condition.Value)) throw new FilterFormatException($"{condition.Keyword} needs a value.");
            // A quote inside the value would end the string early and swallow the rest of the line.
            if (condition.Value.Contains('\"')) throw new FilterFormatException($"{condition.Keyword} may not contain a quotation mark.");
            if (condition.Keyword == "Rarity" && !FilterCondition.Rarities.Contains(condition.Value, StringComparer.OrdinalIgnoreCase))
                throw new FilterFormatException($"\"{condition.Value}\" is not a rarity the game knows.");
            if (condition.Value.Length > 120) throw new FilterFormatException($"{condition.Keyword} value is too long.");
            return;
        }
        if (!condition.IsRange)
        {
            if (!int.TryParse(condition.Value, out int value) || value < 0)
                throw new FilterFormatException($"{condition.Keyword} needs a whole number of at least 0.");
            return;
        }
        if (condition.Min > condition.Max || condition.Min < 0)
            throw new FilterFormatException($"{condition.Keyword} range is empty or negative.");
    }

    public static void Validate(FilterStyle style, FilterBlockKind kind)
    {
        foreach (var (color, name) in new[] { (style.FontColor, "FontColor"), (style.BackgroundColor, "BackgroundColor") })
            if (color is not null && (color.Length != 4 || color.Any(c => c is < 0 or > 255)))
                throw new FilterFormatException($"{name} must be four values between 0 and 255.");
        if (style.FontSize is { } size && !FilterStyle.IsFontSize(size))
            throw new FilterFormatException($"{size} is not a font size the game accepts.");
        if (style.BorderWidth is { } width && width is < 0 or > 10)
            throw new FilterFormatException("Border width must be between 0 and 10.");
        if (style.Sound is { Length: > 0 } sound && !FilterStyle.IsSound(sound))
            throw new FilterFormatException($"The game has no sound called \"{sound}\".");
        if (style.MinimapIcon is { } icon && !FilterStyle.IsMinimapIcon(icon))
            throw new FilterFormatException($"Minimap icon {icon} is not one the game offers.");
        if (style.MinimapIconName is { Length: > 0 } iconName && !FilterStyle.MinimapIcons.Contains(iconName))
            throw new FilterFormatException($"The game has no minimap icon called \"{iconName}\".");
        // The game rejects a style it cannot apply, so a styled Hide block is refused at edit time instead of
        // producing a file the client will not load.
        if (kind == FilterBlockKind.Hide && (style.FontColor is not null || style.FontSize is not null ||
            style.BackgroundColor is not null || style.BorderWidth is not null || style.Sound is not null ||
            style.MinimapIcon is not null || style.EnableFlare || style.EnableGrey))
            throw new FilterFormatException("A Hide block styles nothing: it has no colour, size, border or sound.");
    }
}