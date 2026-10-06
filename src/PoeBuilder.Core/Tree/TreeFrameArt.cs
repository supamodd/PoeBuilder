namespace PoeBuilder.Core.Tree;

/// <summary>The state a node is drawn in, one frame sprite each, exactly as PoB2's own tree data
/// declares them (TreeData/0_5/tree.json → <c>nodeOverlay</c>).</summary>
public enum NodeFrameState
{
    /// <summary>Not allocated and not on the previewed path (<c>nodeOverlay.*.unalloc</c>).</summary>
    Unallocated,
    /// <summary>On the path that a click would allocate — PoB2's "can allocate" highlight
    /// (<c>nodeOverlay.*.path</c>).</summary>
    CanAllocate,
    /// <summary>Allocated (<c>nodeOverlay.*.alloc</c>).</summary>
    Allocated,
}

/// <summary>
/// PoB2's node frame art. PoB2 draws a passive as its frame sprite plus the skill icon on top
/// (<c>Classes/PassiveTree.lua:506-540</c>): the sprite is chosen by the node's type and state from
/// <c>nodeOverlay</c>, and each sprite is one slice of a BC7 DDS array atlas listed in
/// <c>ddsCoords</c> — e.g. <c>group-background_152_156_BC7.dds.zst</c> holds
/// <c>NotableFrameAllocated = 13</c>, <c>JewelFrameCanAllocate = 17</c>.
/// <para>
/// The slices are converted to PNGs once, with a throwaway tool (the app takes no BC7 dependency);
/// this class holds the sprite names and their sizes so the viewport only has to pick a bitmap.
/// </para>
/// </summary>
public static class TreeFrameArt
{
    // group-background_104_104_BC7.dds.zst (104x104)
    public const string Normal = "PSSkillFrame";
    public const string NormalCanAllocate = "PSSkillFrameHighlighted";
    public const string NormalAllocated = "PSSkillFrameActive";
    // group-background_152_156_BC7.dds.zst (152x156) — notables and jewel sockets share the atlas.
    public const string NotableUnallocated = "NotableFrameUnallocated";
    public const string NotableCanAllocate = "NotableFrameCanAllocate";
    public const string NotableAllocated = "NotableFrameAllocated";
    public const string JewelUnallocated = "JewelFrameUnallocated";
    public const string JewelCanAllocate = "JewelFrameCanAllocate";
    public const string JewelAllocated = "JewelFrameAllocated";
    // group-background_220_224_BC7.dds.zst (220x224)
    public const string KeystoneUnallocated = "KeystoneFrameUnallocated";
    public const string KeystoneCanAllocate = "KeystoneFrameCanAllocate";
    public const string KeystoneAllocated = "KeystoneFrameAllocated";
    // group-background_208_208_BC7.dds.zst (208x208) — one set of slices is shared by every
    // ascendancy class ("<Class>FrameLarge{Allocated,CanAllocate,Normal}" all point at 4/5/6).
    public const string AscendancyUnallocated = "AscendancyFrameNormal";
    public const string AscendancyCanAllocate = "AscendancyFrameCanAllocate";
    public const string AscendancyAllocated = "AscendancyFrameAllocated";

    /// <summary>The frame sprite PoB2 uses for this node in this state, or <c>null</c> for a node it
    /// draws without a frame at all (the class start node, which PoB2 renders as the class crest).</summary>
    public static string? Sprite(bool isAscendancy, bool isKeystone, bool isNotable, bool isJewel, bool isStart,
        NodeFrameState state)
    {
        if (isStart) return null;
        string[] states = isAscendancy
            ? [AscendancyUnallocated, AscendancyCanAllocate, AscendancyAllocated]
            : isKeystone ? [KeystoneUnallocated, KeystoneCanAllocate, KeystoneAllocated]
            : isJewel ? [JewelUnallocated, JewelCanAllocate, JewelAllocated]
            : isNotable ? [NotableUnallocated, NotableCanAllocate, NotableAllocated]
            : [Normal, NormalCanAllocate, NormalAllocated];
        return states[(int)state];
    }

    /// <summary>The atlas slice size PoB2 ships for a sprite, so the viewport can keep the frame's
    /// own aspect ratio instead of forcing a square.</summary>
    public static (int Width, int Height) Size(string sprite) => sprite switch
    {
        AscendancyUnallocated or AscendancyCanAllocate or AscendancyAllocated => (208, 208),
        KeystoneUnallocated or KeystoneCanAllocate or KeystoneAllocated => (220, 224),
        JewelUnallocated or JewelCanAllocate or JewelAllocated => (152, 156),
        NotableUnallocated or NotableCanAllocate or NotableAllocated => (152, 156),
        _ => (104, 104),
    };

    // ---- The hover tooltip's frame ---------------------------------------------------------------
    // PoB2 does not draw a passive's tooltip with plain rectangles: it reuses its item header art.
    // Classes/Tooltip.lua:18-32 maps PASSIVE/NOTABLE/JEWEL/KEYSTONE/ASCENDANCY onto
    // normal/notable/jewel/keystone/ascendancy "passiveheader{left,middle,right}.png", and
    // Classes/Tooltip.lua:518-555 draws them as a left cap, the middle tiled across the panel and a
    // right cap — the ornate bronze frame the game puts round a node's name. PassiveTreeView.lua:1540-1546
    // picks the set from the node's type.
    public const string TooltipHeaderNormal = "HeaderNormal";
    public const string TooltipHeaderNotable = "HeaderNotable";
    public const string TooltipHeaderJewel = "HeaderJewel";
    public const string TooltipHeaderKeystone = "HeaderKeystone";
    public const string TooltipHeaderAscendancy = "HeaderAscendancy";

    /// <summary>
    /// The header art PoB2 draws round this node's name in its hover tooltip
    /// (<c>Classes/PassiveTreeView.lua:1540-1551</c>), or <c>null</c> when nothing matches and the
    /// caller keeps its plain frame.
    /// </summary>
    public static string? TooltipHeader(bool isAscendancy, bool isKeystone, bool isNotable, bool isJewel) =>
        isAscendancy ? TooltipHeaderAscendancy
        : isKeystone ? TooltipHeaderKeystone
        : isJewel ? TooltipHeaderJewel
        : isNotable ? TooltipHeaderNotable
        : TooltipHeaderNormal;

    /// <summary>
    /// PoB2's own header metrics in pixels (<c>Classes/Tooltip.lua:18-32</c>, the <c>PASSIVE</c>/
    /// <c>NOTABLE</c>/<c>JEWEL</c>/<c>KEYSTONE</c>/<c>ASCENDANCY</c> rows): a 38 px tall strip whose
    /// left and right caps are 32 px wide (38 for a notable) with a 32 px middle tile repeated between
    /// them, and the title sitting <see cref="TextYOffset"/> px below the strip's top. The art itself is
    /// a 71x88 slice, so the caps are drawn slightly narrower than they are stored.
    /// </summary>
    public static (int Height, int SideWidth, int MiddleWidth, int TextYOffset, int TitleSize) TooltipHeaderShape(bool notable) =>
        (38, notable ? 38 : 32, 32, 6, 24);

    /// <summary>
    /// The 1 px border PoB2 strokes round a tooltip in its own tooltip colour
    /// (<c>Classes/Tooltip.lua:88</c>, <c>self.color = {0.5, 0.3, 0}</c>, drawn at
    /// <c>BORDER_WIDTH</c> = 1 by <c>:656-671</c>).
    /// </summary>
    public static string TooltipBorderColor => "#804D00";

    /// <summary>
    /// How much of the frame's width the node's own icon takes — PoB2's own numbers
    /// (<c>Classes/PassiveTree.lua:777-835</c>: a normal node draws a 37 unit icon inside a 54 unit frame,
    /// a notable 54 inside 80, a keystone 82 inside 120, a socket 76 inside 76).
    /// <para>
    /// The ratio is what makes the frame the outer layer: <c>PassiveTreeView.lua</c> draws the icon first
    /// (<c>:1064</c>) and the frame over it (<c>:1126</c>), so the ring masks the icon patch's square
    /// corners. Drawing the icon at the frame's full width, and last, is what produced a square sitting on
    /// top of the node's round frame.
    /// </para>
    /// </summary>
    public static double IconShare(string? sprite) => sprite switch
    {
        KeystoneUnallocated or KeystoneCanAllocate or KeystoneAllocated => 82.0 / 120.0,
        JewelUnallocated or JewelCanAllocate or JewelAllocated => 76.0 / 76.0,
        NotableUnallocated or NotableCanAllocate or NotableAllocated => 54.0 / 80.0,
        AscendancyUnallocated or AscendancyCanAllocate or AscendancyAllocated => 54.0 / 100.0,
        _ => 37.0 / 54.0,
    };
}
