using System.Text.RegularExpressions;
using PoeBuilder.Core.Tree;

namespace PoeBuilder.Core.Calculation;

/// <summary>One of PoB2's jewel radius bands: an annulus in raw tree units.
/// <c>Inner</c>/<c>Outer</c> are PoB2's own numbers (Modules/Data.lua data.jewelRadii["0_1"]).</summary>
public sealed record JewelRadiusBand(string Label, double Inner, double Outer);

/// <summary>The kind of passive a radius grant can name. PoB2's ModParser matches
/// <c>"^(%w+) Passive Skills in Radius also grant (.*)$"</c> against the node's type, and treats an
/// un-flagged "Normal" node as "Small" (Modules/ModParser.lua:7041).</summary>
public enum RadiusPassiveType { Small, Notable, Keystone }

/// <summary>One "&lt;Type&gt; Passive Skills in Radius also grant &lt;effect&gt;" line of a jewel.</summary>
public sealed record RadiusGrant(RadiusPassiveType Type, string Effect);

/// <summary>PoB2's radius model for jewels socketed in the tree. PoB2 precomputes, for every jewel
/// socket, the set of nodes inside each radius band (Classes/PassiveTree.lua:331-354) and then attaches
/// the jewel's "… in Radius also grant …" mod to every ALLOCATED node of the named type inside it
/// (Classes/PassiveSpec.lua:1452-1490, Modules/CalcSetup.lua:279-283) — so the effect applies once per
/// qualifying node, which is exactly what the panel's source list shows (one row per notable).</summary>
public static class JewelRadius
{
    /// <summary>PoB2's <c>gameConstants.PassiveTreeJewelDistanceMultiplier</c>
    /// (PathOfBuilding-PoE2-master/src/Data/Misc.lua:36 = 1.2). PoB2 multiplies every band radius by it
    /// before comparing squared distances, so a "Very Large" jewel really reaches
    /// <c>1500 × 1.2 = 1800</c> tree units.</summary>
    public const double DistanceMultiplier = 1.2;

    // Verbatim from PathOfBuilding-PoE2-master/src/Modules/Data.lua (data.jewelRadii["0_1"]).
    // The first four are the fixed bands ("Radius: Small|Medium|Large|Very Large"); the rest are the
    // "Variable" bands a Time-Lost jewel picks from. PoB2 compares squared distances scaled by
    // gameConstants.PassiveTreeJewelDistanceMultiplier, which applies to both sides of the comparison
    // and therefore cancels — raw tree units are exact.
    public static readonly JewelRadiusBand[] Bands =
    [
        new("Small", 0, 1000),
        new("Medium", 0, 1150),
        new("Large", 0, 1300),
        new("Very Large", 0, 1500),
        new("Variable 1", 650, 950),
        new("Variable 2", 800, 1100),
        new("Variable 3", 950, 1250),
        new("Variable 4", 1100, 1400),
        new("Variable 5", 1250, 1550),
        new("Variable 6", 1400, 1700),
        new("Variable 7", 1650, 1950),
        new("Variable 8", 1800, 2100)
    ];

    private static readonly Regex UpgradeLine = new(@"^Upgrades Radius to (.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RadiusLine = new(@"^Radius:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GrantLine = new(@"^(\w+) Passive Skills in Radius also grant (.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // PoB2's own two allocation lines (Modules/ModParser.lua:5507-5511). The From Nothing wording carries
    // a literal newline inside the mod ("…can be Allocated\nwithout being connected to your tree"), so the
    // pattern is matched against the whitespace-flattened item text.
    private static readonly Regex AllocateFromKeystone = new(
        @"Passives in radius of (?<name>.+?) can be Allocated\s+without being connected to your tree",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AllocateFromSocket = new(
        @"Passives in radius can be allocated\s+without being connected to your tree",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Band index (1-based, as PoB2's <c>jewelRadiusIndex</c>) for a printed radius label;
    /// 0 means "not one of the fixed bands" (a "Variable" jewel whose band has to come from its mods).</summary>
    public static int IndexOfLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return 0;
        string wanted = label.Trim();
        for (int i = 0; i < Bands.Length; i++)
            if (string.Equals(Bands[i].Label, wanted, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }

    /// <summary>The radius band a jewel's own text states. PoB2 reads the same two lines: an
    /// "Upgrades Radius to X" modifier overrides the base band (Classes/Item.lua:1362,
    /// <c>timeLostJewelRadiusOverride</c>), and a plain "Radius: X" line sets it otherwise.</summary>
    public static int IndexForItemText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int index = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (UpgradeLine.Match(line) is { Success: true } upgrade)
                index = Math.Max(index, IndexOfLabel(upgrade.Groups[1].Value));
            else if (RadiusLine.Match(line) is { Success: true } radius)
                index = Math.Max(index, IndexOfLabel(radius.Groups[1].Value));
        }
        return index;
    }

    /// <summary>
    /// The allocation rule a jewel's own text states, or null when it states none. PoB2 reads the same two
    /// lines and stores them on the item (Modules/ModParser.lua:5507-5511); the band comes from the item's
    /// own "Radius: X" / "Upgrades Radius to X" text (Classes/Item.lua:1362). A jewel whose text names no
    /// band is reported as "no rule" instead of guessing one, because the radius is what makes the line legal.
    /// </summary>
    public static RadiusAllocationRule? AllocationRule(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int index = IndexForItemText(text);
        if (index == 0) return null;
        // "Passives in radius of Resonance can be Allocated\nwithout being connected to your tree" is ONE
        // mod whose text contains a newline, so the two physical lines are joined before matching.
        string flat = string.Join(" ", text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length != 0));
        if (AllocateFromKeystone.Match(flat) is { Success: true } keystone && keystone.Groups["name"].Value.Trim() is { Length: > 0 } name)
            return new(index, name);
        return AllocateFromSocket.IsMatch(flat) ? new(index, "") : null;
    }

    /// <summary>The "… in Radius also grant …" lines of a jewel, in file order.</summary>
    public static IReadOnlyList<RadiusGrant> ParseGrants(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var grants = new List<RadiusGrant>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (GrantLine.Match(raw.Trim()) is not { Success: true } grant) continue;
            if (TypeOf(grant.Groups[1].Value) is not { } type) continue;
            // PoB2 feeds the effect text through its full modifier parser, so anything it understands
            // becomes a real mod; the text is kept verbatim and resolved the same way here.
            string effect = grant.Groups[2].Value.Trim();
            if (effect.Length > 0) grants.Add(new(type, effect));
        }
        return grants;
    }

    /// <summary>PoB2's node-type words: "Notable", "Keystone" and "Small".</summary>
    public static RadiusPassiveType? TypeOf(string? word) => word?.ToLowerInvariant() switch
    {
        "notable" => RadiusPassiveType.Notable,
        "keystone" => RadiusPassiveType.Keystone,
        "small" => RadiusPassiveType.Small,
        _ => null
    };

    /// <summary>The type a passive node answers to, or null when a radius grant can never name it
    /// (jewel sockets, masteries, class starts, ascendancy nodes, attribute nodes).</summary>
    public static RadiusPassiveType? TypeOf(PassiveNode node)
    {
        if (node.IsMastery || node.IsJewel || node.IsStart || node.IsAscendancy) return null;
        if (node.IsNotable) return RadiusPassiveType.Notable;
        if (node.IsKeystone) return RadiusPassiveType.Keystone;
        return node.IsAttribute ? null : RadiusPassiveType.Small;
    }

    /// <summary>The outer edge of a band in tree units, scaled by PoB2's distance multiplier — what the tree
    /// view draws as the jewel's radius circle. 0 when the index names no band.</summary>
    public static double OuterRadius(int radiusIndex) =>
        radiusIndex >= 1 && radiusIndex <= Bands.Length ? Bands[radiusIndex - 1].Outer * DistanceMultiplier : 0;

    /// <summary>The allocated nodes inside a socket's radius band. PoB2's comparison is
    /// <c>inner² ≤ distance² ≤ outer²</c> between the socket and the node
    /// (Classes/PassiveTree.lua:341-352). <paramref name="pool"/> defaults to every node of the tree, which
    /// is what an allocation rule needs ("which nodes COULD this radius reach"); the stat side passes the
    /// allocated set, because PoB2 attaches a radius mod only to allocated nodes.</summary>
    public static IReadOnlyList<int> NodesInRadius(TreeCatalog tree, int socketId, int radiusIndex,
        IReadOnlyCollection<int>? pool = null)
    {
        if (radiusIndex < 1 || radiusIndex > Bands.Length) return [];
        if (!tree.Nodes.TryGetValue(socketId, out var socket)) return [];
        var band = Bands[radiusIndex - 1];
        double outerSquared = Math.Pow(band.Outer * DistanceMultiplier, 2);
        double innerSquared = Math.Pow(band.Inner * DistanceMultiplier, 2);
        var inside = new List<int>();
        foreach (int id in pool ?? (IReadOnlyCollection<int>)tree.Nodes.Keys)
        {
            if (id == socketId || !tree.Nodes.TryGetValue(id, out var node)) continue;
            double dx = node.X - socket.X, dy = node.Y - socket.Y;
            double distanceSquared = dx * dx + dy * dy;
            if (distanceSquared <= outerSquared && innerSquared <= distanceSquared) inside.Add(id);
        }
        return inside;
    }
}

/// <summary>Resolves the effect text of a radius grant ("5% increased Critical Hit Chance") into the
/// stat ids the calculator speaks. Wordings PoB2 shares with quest rewards and ordinary item lines come
/// from the same parser (<see cref="QuestRewardParser"/>), so a wording is only ever mapped once in this
/// codebase; the rest are listed here with PoB2's own id. An effect that resolves to nothing is reported
/// by the caller instead of vanishing.</summary>
public static class RadiusEffects
{
    private static readonly Regex CritChanceInc = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Critical Hit Chance$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CritBonusInc = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Critical Damage Bonus$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttackSpeedInc = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Attack Speed$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CastSpeedInc = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Cast Speed$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DamageInc = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Damage$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Stat ids for one radius effect. An empty list means "not modelled" — the caller reports it.</summary>
    public static IReadOnlyList<(string Id, decimal Value)> Resolve(string effect)
    {
        var quest = QuestRewardParser.ParseLine(effect);
        if (quest.Count > 0) return quest;
        if (CritChanceInc.Match(effect) is { Success: true } crit) return [("critical_strike_chance_+%", D(crit.Groups[1].Value))];
        if (CritBonusInc.Match(effect) is { Success: true } bonus) return [("critical_strike_multiplier_+%", D(bonus.Groups[1].Value))];
        if (AttackSpeedInc.Match(effect) is { Success: true } attack) return [("attack_speed_+%", D(attack.Groups[1].Value))];
        if (CastSpeedInc.Match(effect) is { Success: true } cast) return [("cast_speed_+%", D(cast.Groups[1].Value))];
        if (DamageInc.Match(effect) is { Success: true } damage) return [("damage_+%", D(damage.Groups[1].Value))];
        return [];
    }

    private static decimal D(string text) =>
        decimal.Parse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
}
