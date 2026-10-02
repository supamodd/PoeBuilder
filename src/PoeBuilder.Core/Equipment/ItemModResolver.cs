namespace PoeBuilder.Core.Equipment;

/// <summary>
/// Resolves a stored <see cref="ModRoll"/> back to the <see cref="ItemMod"/> it was rolled from, so an
/// item can be opened for viewing/editing without crashing.
/// <para>
/// A roll's id can legally come from any of the three pools the importer searches — the catalog's own
/// base affixes, the pinned affix table, and the jewel pool — which is why the lookup goes through
/// <see cref="GameCatalog.AllMods"/> (all three merged) rather than the base-only
/// <c>catalog.Mods</c>. A roll can also carry a synthetic id: when no pinned template matched a line,
/// the matcher falls back to the reverse stat-text table and stores the <b>stat id</b> the line names,
/// which no mod dictionary contains. <see cref="For"/> handles that too, building a readable stand-in so
/// the row stays visible (and its rolls stay editable) instead of dropping the modifier or throwing.
/// </para>
/// </summary>
public static class ItemModResolver
{
    /// <summary>A pinned mod when the id is known, otherwise a synthetic stand-in for the same id — never
    /// null, so every stored roll of an imported item opens and displays.</summary>
    public static ItemMod For(GameCatalog catalog, ModRoll roll)
    {
        if (catalog.AllMods.TryGetValue(roll.Id, out var real)) return real;
        return Synthetic(roll);
    }

    /// <summary>Resolves, telling the caller whether the id was a real pinned mod or a synthetic stand-in
    /// (a caller that renders only real templates may want to know). Never null.</summary>
    public static ItemMod For(GameCatalog catalog, ModRoll roll, out bool resolved)
    {
        resolved = catalog.AllMods.TryGetValue(roll.Id, out var real);
        return resolved ? real! : Synthetic(roll);
    }

    /// <summary>A readable stand-in for an id no mod dictionary knows (a synthetic stat id the reverse
    /// stat-text matcher produced). Its text is the stat id rendered as words, its roll keeps the stored
    /// values, and its single stat mirrors the first roll so value text round-trips through the editor.</summary>
    private static ItemMod Synthetic(ModRoll roll)
    {
        decimal value = roll.Values.Length > 0 ? roll.Values[0] : 0m;
        var stat = new ModStat(roll.Id, value, value);
        return new ItemMod(roll.Id, roll.Id, "", 0, [], Readable(roll.Id), [stat]);
    }

    /// <summary>"local_added_fire_damage_to_attacks" → "local added fire damage to attacks".</summary>
    public static string Readable(string statId)
    {
        if (string.IsNullOrWhiteSpace(statId)) return statId;
        return string.Join(' ', statId.Split('_').Where(w => w.Length > 0));
    }
}
