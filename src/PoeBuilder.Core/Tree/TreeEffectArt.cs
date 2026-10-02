namespace PoeBuilder.Core.Tree;

/// <summary>
/// PoB2's node "effect" art — the pattern a node is drawn as, or drawn on top of: a mastery is *only*
/// that pattern, and a notable that carries one draws it under its frame.
/// <para>
/// The numbers come from PoB2's own node-target table (<c>Classes/PassiveTree.lua:777-835</c>), whose
/// sizes are <b>half</b> sizes: <c>DrawAsset</c> centres the art and gives it twice the listed width
/// (<c>PassiveTreeView.lua:1326-1343</c>), so a value of 380 is a pattern 760 world units across.
/// </para>
/// </summary>
public static class TreeEffectArt
{
    /// <summary>Half-size of the pattern PoB2 draws for a node it has no icon for
    /// (<c>OnlyImage</c> — the mastery nodes of the pinned tree — <c>PassiveTree.lua:809-810</c>).</summary>
    public const double MasteryRadius = 380;

    /// <summary>Half-size of the effect a notable or a keystone draws under its frame
    /// (<c>PassiveTree.lua:799</c> and <c>:813</c>).</summary>
    public const double NotableRadius = 380;

    /// <summary>The opacity PoB2 gives an effect that is not being allocated
    /// (<c>PassiveTreeView.lua:1035</c>): the faint star burst a cluster shows around its centre.</summary>
    public const double IdleOpacity = 0.15;

    /// <summary>
    /// The half-size to draw a node's effect at, or 0 when PoB2 draws none.
    /// <para>
    /// A mastery is the whole node, so the pattern carries the full <see cref="MasteryRadius"/>; anything
    /// else that has effect art (<c>hasEffectArt</c>) draws it only as a backdrop under its frame. A plain
    /// node with an <c>activeEffectImage</c> is a real case in the pinned tree (a handful of ordinary nodes
    /// carry a mastery-style pattern), so the backdrop is gated on the image itself, not on the node's
    /// notable/keystone class.
    /// </para>
    /// </summary>
    public static double Radius(bool isMastery, bool hasEffectArt) =>
        isMastery ? MasteryRadius : hasEffectArt ? NotableRadius : 0;

    /// <summary>The sprite name of an exported effect path
    /// (<c>Art/…/MasteryFirePattern.png</c> → <c>MasteryFirePattern</c>), or <c>null</c> when the node
    /// carries no effect art. PoB2 addresses the same sprite by that name in its own dds map.</summary>
    public static string? Sprite(string exportPath)
    {
        if (string.IsNullOrWhiteSpace(exportPath)) return null;
        string name = exportPath.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}