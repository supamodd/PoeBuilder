namespace PoeBuilder.Core.Tree;

/// <summary>Where a class or ascendancy backdrop sits on the tree, in world units (PoB2's tree
/// coordinates are the same units as its art pixels).</summary>
public sealed record TreeClassArt(string Name, string Sprite, double X, double Y, double Size = TreeClassArtTable.TableSize);

/// <summary>
/// PoB2's class and ascendancy backdrops. Every class has its own 1500×1500 art centred on the tree
/// centre, and each ascendancy has one placed around the ring (<c>Classes/PassiveTreeView.lua:588-640</c>);
/// on top of them PoB2 draws <c>BGTree</c> and, rotated towards the class start node, <c>BGTreeActive</c>
/// — that rotated glow is what makes the class circle look lit in the game.
/// <para>
/// The positions come from <c>TreeData/0_5/tree.json</c> (<c>classes[].background</c> and
/// <c>ascendancies[].background</c>) and the sprite names from its <c>ddsCoords</c>; the art itself was
/// converted once out of the BC7 <c>ascendancy-background_*</c> atlases into PNGs (see
/// docs/VALIDATION.md), so the app needs no BC7 decoder.
/// </para>
/// </summary>
public static class TreeClassArtTable
{
    /// <summary>The table size PoB2 lists for a class background (art and world size alike).</summary>
    public const double TableSize = 1500;

    /// <summary>PoB2 draws the centre ring and its glow at 2000 world units even though the art is 4000
    /// (it halves the asset when drawing, <c>PassiveTreeView.lua:609-615</c>).</summary>
    public const double CentreSize = 2000;

    public const string RingSprite = "BGTree";
    public const string GlowSprite = "BGTreeActive";

    /// <summary>The rotation PoB2 applies to the glow: the quad is turned so it points at the class's
    /// start node (<c>PassiveTreeView.lua:608</c>).</summary>
    public static double GlowRotationDegrees(double startX, double startY, double centreX, double centreY) =>
        (Math.PI / 2 + Math.Atan2(startY - centreY, startX - centreX)) * 180 / Math.PI;

    /// <summary>The backdrop of a base class, or <c>null</c> when the class has no art (the pinned tree
    /// lists twelve classes, but only the eight the reference game ships are drawn).</summary>
    public static TreeClassArt? Class(string name) => Classes.TryGetValue(name, out var art) ? art : null;

    /// <summary>The backdrop of an ascendancy, or <c>null</c> when that ascendancy has no art.</summary>
    public static TreeClassArt? Ascendancy(string name) => Ascendancies.TryGetValue(name, out var art) ? art : null;

    private static readonly Dictionary<string, TreeClassArt> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ranger"] = new("Ranger", "ClassesRanger", 0, 0),
        ["Huntress"] = new("Huntress", "ClassesHuntress", 0, 0),
        ["Warrior"] = new("Warrior", "ClassesWarrior", 0, 0),
        ["Mercenary"] = new("Mercenary", "ClassesMercenary", 0, 0),
        ["Druid"] = new("Druid", "ClassesDruid", 0, 0),
        ["Witch"] = new("Witch", "ClassesWitch", 0, 0),
        ["Sorceress"] = new("Sorceress", "ClassesSorceress", 0, 0),
        ["Monk"] = new("Monk", "ClassesMonk", 0, 0),
    };

    /// <summary>Every ascendancy backdrop, in the order PoB2 draws them (it walks its own name map, and
    /// two entries share a position — Lich and Abyssal Lich — so the later one simply covers the other).
    /// </summary>
    public static readonly IReadOnlyList<TreeClassArt> Ring = new TreeClassArt[]
    {
        new("Deadeye", "ClassesDeadeye", 15451.74, 1623.24),
        new("Pathfinder", "ClassesPathfinder", 14776.59, 4800.37),
        new("Amazon", "ClassesAmazon", 13455.63, 7767.70),
        new("Spirit Walker", "ClassesSpirit Walker", 11546.60, 10395.54),
        new("Ritualist", "ClassesRitualist", 9132.92, 12569.04),
        new("Titan", "ClassesTitan", -11551.11, 10390.52),
        new("Warbringer", "ClassesWarbringer", -13459.00, 7761.86),
        new("Smith of Kitava", "ClassesSmith of Kitava", -14778.67, 4793.96),
        new("Tactician", "ClassesTactician", 3250.43, 15192.95),
        new("Witchhunter", "ClassesWitchhunter", 20.61, 15536.75),
        new("Gemling Legionnaire", "ClassesGemling Legionnaire", -3210.11, 15201.52),
        new("Oracle", "ClassesOracle", -14155.37, -6404.41),
        new("Shaman", "ClassesShaman", -12514.49, -9207.52),
        new("Infernalist", "ClassesInfernalist", -9132.28, -12569.51),
        new("Blood Mage", "ClassesBlood Mage", -6319.37, -14193.54),
        new("Lich", "ClassesLich", -3230.28, -15197.25),
        new("Abyssal Lich", "ClassesAbyssal Lich", -3230.28, -15197.25),
        new("Stormweaver", "ClassesStormweaver", 0, -15536.77),
        new("Chronomancer", "ClassesChronomancer", 3230.28, -15197.25),
        new("Disciple of Varashta", "ClassesDisciple of Varashta", 6319.37, -14193.54),
        new("Martial Artist", "ClassesMartial Artist", 11574.56, -10364.39),
        new("Invoker", "ClassesInvoker", 13476.51, -7731.41),
        new("Acolyte of Chayula", "ClassesAcolyte of Chayula", 14789.47, -4760.54),
    };

    private static readonly Dictionary<string, TreeClassArt> Ascendancies = Ring
        .ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
}
