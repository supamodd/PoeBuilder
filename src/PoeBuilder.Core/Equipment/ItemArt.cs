namespace PoeBuilder.Core.Equipment;

/// <summary>
/// Where an item's artwork lives — pure path logic, no imaging API, so it is testable and the WPF layer
/// only has to load the file it is handed.
/// <para>
/// Three addresses exist for one picture: the <b>bundled</b> layout this project ships (the DDS paths of
/// the pinned export, converted once to PNG under <c>Data/Icons/Items/…</c>), the game's <b>public CDN</b>
/// endpoint for art that is not bundled, and — when neither applies — the picture of the unique's
/// <b>base type</b>, which PoB2's data names. Nothing here invents a picture: an unknown name returns
/// null and the UI shows an empty slot.
/// </para>
/// </summary>
public static class ItemArt
{
    /// <summary>"Art/2DItems/.../X.dds" → "Items/2DItems/.../X.png" (the layout the icon pack uses).</summary>
    public static string? RelativePath(string? art)
    {
        if (art is null || art.Length <= 4 || !art.StartsWith("Art/", StringComparison.Ordinal) || !art.EndsWith(".dds", StringComparison.Ordinal)) return null;
        return "Items/" + art[4..^4] + ".png";
    }

    /// <summary>The pinned Art path as the game's public image endpoint, or null for a path that is not one.</summary>
    public static Uri? CdnUri(string? art)
    {
        if (art is null || art.Length <= 4 || !art.StartsWith("Art/", StringComparison.Ordinal) || !art.EndsWith(".dds", StringComparison.Ordinal)) return null;
        return Uri.TryCreate("https://web.poecdn.com/image/" + art[..^4] + ".png", UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>
    /// The bundled icon of a unique found by name: its own art when the pack carries it, otherwise the art
    /// of its base type ("Grand Regalia" for Morior Invictus) — a real picture of what the character wears,
    /// which is what a planner should show when the unique's own art is not shipped. Null when neither is
    /// bundled, or when the pinned data does not know the name at all.
    /// </summary>
    public static string? UniqueRelativePath(GameCatalog catalog, string name, Func<string, bool> isBundled)
    {
        if (catalog.Uniques.TryGetValue(name, out var unique))
        {
            var own = RelativePath(unique.Icon);
            if (own is not null && isBundled(own)) return own;
        }
        var data = catalog.UniqueData.For(name);
        if (data is null || !catalog.BasesByName.TryGetValue(data.BaseType, out var itemBase)) return null;
        var fromBase = RelativePath(itemBase.Art);
        return fromBase is not null && isBundled(fromBase) ? fromBase : null;
    }
}
