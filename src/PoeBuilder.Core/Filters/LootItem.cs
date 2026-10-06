namespace PoeBuilder.Core.Filters;

/// <summary>
/// A simulated dropped item that the simulation renders like an item on the ground in Path of Exile 2.
/// Properties mirror what the game actually reads when an item spawns: rarity, type, level, sockets and
/// modifiers. The simulation applies the currently active filter rule's style to this object.
/// </summary>
public sealed class LootItem
{
    /// <summary>The item's internal class name — also the value used in <c>Class</c> / <c>ItemClass</c> rules.</summary>
    public string Class { get; init; } = "Currency";
    /// <summary>The base type / specific item name shown as the item title.</summary>
    public string BaseType { get; init; } = "Currency";
    /// <summary>Rarity drives the default colour when no FontColour rule matches.</summary>
    public string Rarity { get; init; } = "Currency";
    /// <summary>Item level — matched by ItemLevel / DropLevel / ReqLevel rules.</summary>
    public int ItemLevel { get; init; }
    /// <summary>Area level of the map where the item spawns.</summary>
    public int AreaLevel { get; init; }
    /// <summary>Quality percentage for items that can be qualityed.</summary>
    public int Quality { get; init; }
    /// <summary>Number of sockets the item has (flag HasSockets / condition Sockets N).</summary>
    public int Sockets { get; init; }
    /// <summary>True when the item is corrupted — matched by the Corrupted flag.</summary>
    public bool Corrupted { get; init; }
    /// <summary>True when the item is identified — matched by the Identified flag.</summary>
    public bool Identified { get; init; }
    /// <summary>True when the item is enchanted — matched by the Enchanted flag.</summary>
    public bool Enchanted { get; init; }

    /// <summary>Custom modifiers a simulated item may carry. Populated from the data library when the player
    /// wants a more realistic preview.</summary>
    public List<string> Modifiers { get; init; } = new();

    /// <summary>RGBA colour 0-255 for the item icon texture (fallback when no icon asset is available).</summary>
    public int[]? IconAccent { get; init; }
}

/// <summary>
/// Produces a realistic pool of simulated drops. Each pool returns items in the item-class vocabulary the
/// game and the editor understand, so a filter written for a real item will match the simulated one too.
/// </summary>
public static class LootItemGenerator
{
    public static IEnumerable<(string Class, string BaseType, int[]? Icon)> Currencies()
    {
        yield return ("Currency", "Orb of Chance", null);
        yield return ("Currency", "Chaos Orb", null);
        yield return ("Currency", "Orb of Alchemy", null);
        yield return ("Currency", "Orb of Fusing", null);
        yield return ("Currency", "Orb of Regret", null);
        yield return ("Currency", "Divine Orb", null);
        yield return ("Currency", "Exalted Orb", null);
        yield return ("Currency", "Mirror of Kalandra", null);
        yield return ("Currency", "Ancestral Orb", null);
        yield return ("Currency", "Blessed Orb", null);
        yield return ("Currency", "Orb of Alteration", null);
        yield return ("Currency", "Orb of Augmentation", null);
        yield return ("Currency", "Orb of Transmutation", null);
        yield return ("Currency", "Orb of Scouring", null);
        yield return ("Currency", "Orb of Annulment", null);
        yield return ("Currency", "Portal Scroll", null);
        yield return ("Currency", "Stamina Potion", null);
    }

    public static IEnumerable<(string Class, string BaseType, string[] Modifiers, int ItemLevel)> Uniques()
    {
        yield return ("Unique", "Apep's Mask", ["Adds 1-10 fire damage to attacks", "+1 to level of all Fire Spell gems"], 80);
        yield return ("Unique", "The Ascendancy", ["Adds 1-10 fire damage to attacks", "+1 to level of all Fire Spell gems"], 80);
        yield return ("Unique", "Echoing Sword", ["Adds 1-10 fire damage to attacks", "+1 to level of all Fire Spell gems"], 80);
        yield return ("Unique", "Shavronne's Gloves", ["Adds 1-10 fire damage to attacks", "+1 to level of all Fire Spell gems"], 80);
        yield return ("Unique", "Soul Taker", ["Adds 1-10 fire damage to attacks", "+1 to level of all Fire Spell gems"], 80);
    }

    public static IEnumerable<(string Class, string BaseType, string[] Modifiers)> Socketables()
    {
        yield return ("Flask", "Divine Vine", ["+1 to level of all Precision gems", "Grants 20% increased effect of flask effects"]);
        yield return ("Flask", "Diamond Flask", ["Grants 20% increased effect of flask effects", "+10% to maximum Mana per 50 energy shield"]);
        yield return ("Flask", "Gold Flask", ["Grants 20% increased effect of flask effects", "+20% to maximum Life"]);
        yield return ("Flask", "Silver Flask", ["Grants 20% increased effect of flask effects", "Grants 20% increased physical damage reduction"]);
        yield return ("Flask", "Quicksilver Flask", ["Grants 20% increased effect of flask effects", "Grants 20% increased movement speed per stack"]);
    }

    public static IEnumerable<(string Class, string BaseType, int ItemLevel)> Gems()
    {
        yield return ("Gem", "Vaal Harmastri", 20);
        yield return ("Gem", "Molten Strike", 20);
        yield return ("Gem", "Spell Echo", 20);
        yield return ("Gem", "Increased Critical Strikes", 20);
        yield return ("Gem", "Faster Attacks", 20);
        yield return ("Gem", "Faster Casting", 20);
        yield return ("Gem", "Blasphemy", 20);
        yield return ("Gem", "Herald of Agony", 20);
        yield return ("Gem", "Cursed Blood", 20);
        yield return ("Gem", "Pain Offering", 20);
    }

    public static IEnumerable<(string Class, string BaseType, string[] Modifiers, int ItemLevel)> Rares()
    {
        yield return ("Base", "Blighted Regalia", ["+10% to Fire Resistance", "+10% to Cold Resistance", "+50 to maximum Life"], 85);
        yield return ("Base", "Voidwraps", ["+10% to Lightning Resistance", "+10% to Cold Resistance", "Adds 1-5 fire damage to attacks"], 85);
        yield return ("Base", "Monarch", ["+10% to Fire Resistance", "+10% to Cold Resistance", "+50 to maximum Life"], 85);
        yield return ("Base", "Serpent Staff", ["+10% to Fire Resistance", "+10% to Cold Resistance", "Adds 1-5 fire damage to spells"], 85);
        yield return ("Base", "Shaper Bow", ["+10% to Fire Resistance", "+10% to Cold Resistance", "+50 to maximum Life"], 85);
    }

    private static readonly Random _rng = new();

    /// <summary>Returns a random item from the given pools. The item is built so its properties match the
    /// keyword vocabulary the editor uses: Class, BaseType, ItemLevel, AreaLevel, Quality, Sockets and flags.</summary>
    public static LootItem Pick(params Func<IEnumerable<(string, string, string[], int)>>[] pools)
    {
        var pool = pools
            .SelectMany(p => p())
            .OrderBy(_ => _rng.Next())
            .FirstOrDefault();
        var (cls, baseType, modifiers, level) = pool;
        var rarity = cls switch
        {
            "Currency" or "Gem" or "Quest" or "Relic" => cls,
            "Base" or "Flask" => _rng.NextDouble() < 0.3 ? "Rare" : "Normal",
            _ => "Normal"
        };
        return new LootItem
        {
            Class = cls,
            BaseType = baseType,
            Rarity = rarity,
            ItemLevel = level,
            AreaLevel = 70 + _rng.Next(0, 40),
            Quality = _rng.Next(0, 21),
            Sockets = _rng.Next(0, 6),
            Corrupted = rarity == "Rare" && _rng.NextDouble() < 0.1,
            Identified = _rng.NextDouble() > 0.2,
            Enchanted = _rng.NextDouble() > 0.9,
            Modifiers = modifiers.Length > 0 ? modifiers.ToList() : (cls == "Base" ? new List<string> { "Adds 1-5 fire damage", "+10 to maximum Life" } : new List<string>())
        };
    }
}