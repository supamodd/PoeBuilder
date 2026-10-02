using System.Security.Cryptography;
using System.Text.Json;

namespace PoeBuilder.Core.Equipment;

/// <summary>
/// The pinned affix table next to the catalog (<c>Data/Game/affixes.json</c>, built by
/// <c>build/extract-poe2-affixes.ps1</c> from the same pinned RePoE snapshot).
/// <para>
/// Two things the shipped catalog cannot answer live here. First, an affix's <b>spawn tags</b>: the
/// catalog pools a class's affixes by its item-class tag, so a Strength body armour never saw
/// "+(278-310) to Armour" (<c>LocalIncreasedPhysicalDamageReductionRating11</c>) even though the snapshot
/// rolls it on <c>str_armour</c> bases — the mod was in the catalog, it was simply in no pool. Second, the
/// <b>jewels</b>: PoE2 jewel bases are item class "Jewel", which the catalog left out, so the jewel editor
/// had to treat every jewel as a baseless item whose affixes carried no kind and no level.
/// </para>
/// <para>
/// A missing file is not an error: <see cref="Empty"/> keeps an older dataset (or a trimmed build) working
/// exactly as before, with the catalog's own pools in charge.
/// </para>
/// </summary>
/// <summary>One base-implicit mod as the pinned snapshot states it: the mod id it rolls, its display
/// text (with the game's range notation) and the stat ranges, from <c>base_items.json</c> +
/// <c>mods.json</c> via <c>extract-poe2-affixes.ps1</c>. This is what makes a base's own modifier a
/// reproducible part of the affix table instead of whatever the catalog generator happened to include.</summary>
public sealed record BaseImplicit(string Mod, string Text, ImplicitStat[] Stats);

public sealed record AffixData(
    IReadOnlyDictionary<string, ItemMod> Mods,
    IReadOnlyDictionary<string, string[]> TagPools,
    IReadOnlyList<ItemBase> JewelBases,
    IReadOnlyList<string> JewelTags,
    IReadOnlyDictionary<string, BaseImplicit[]> BaseImplicits)
{
    public static AffixData Empty { get; } = new(
        new Dictionary<string, ItemMod>(StringComparer.Ordinal),
        new Dictionary<string, string[]>(StringComparer.Ordinal),
        [],
        [],
        new Dictionary<string, BaseImplicit[]>(StringComparer.Ordinal));

    /// <summary>One jewel base as the snapshot states it; its properties are empty there (a jewel's
    /// "Radius"/"Limited to" lines are rules, and no pinned table carries them, so none are invented).</summary>
    private sealed record JewelBaseRow(string Id, string Name, string ItemClass, string ClassName, int DropLevel,
        string PropertiesText, string[]? Tags, string[]? Implicits, string Art);

    private sealed record TagPoolsRow(Dictionary<string, string[]?> TagPools);


    public static AffixData Load(string path, string sha256)
    {
        if (!File.Exists(path)) return Empty;
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("Affix table exceeds size limit.");
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Affix table checksum mismatch.");
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;

        var mods = new Dictionary<string, ItemMod>(StringComparer.Ordinal);
        if (root.TryGetProperty("mods", out var modsElement) && modsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in modsElement.EnumerateArray())
            {
                var id = element.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? "" : "";
                if (id.Length == 0) continue;
                var stats = new List<ModStat>();
                if (element.TryGetProperty("stats", out var statsElement) && statsElement.ValueKind == JsonValueKind.Array)
                    foreach (var stat in statsElement.EnumerateArray())
                        stats.Add(new(stat.TryGetProperty("id", out var sid) ? sid.GetString() ?? "" : "",
                            stat.TryGetProperty("min", out var min) ? min.GetDecimal() : 0m,
                            stat.TryGetProperty("max", out var max) ? max.GetDecimal() : 0m));
                mods[id] = new ItemMod(id,
                    element.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    element.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "" : "",
                    element.TryGetProperty("level", out var level) ? level.GetInt32() : 0,
                    ReadStrings(element, "groups"),
                    element.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
                    [.. stats]);
            }
        }

        var tagPools = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (root.TryGetProperty("tagPools", out var poolsElement) && poolsElement.ValueKind == JsonValueKind.Object)
            foreach (var property in poolsElement.EnumerateObject())
                tagPools[property.Name] = [.. property.Value.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0)];

        var jewelBases = new List<ItemBase>();
        if (root.TryGetProperty("jewelBases", out var jewelsElement) && jewelsElement.ValueKind == JsonValueKind.Array)
        {
            var rows = jewelsElement.Deserialize<List<JewelBaseRow>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            foreach (var row in rows)
                jewelBases.Add(new ItemBase(row.Id, row.Name, row.ItemClass.Length == 0 ? "Jewel" : row.ItemClass,
                    row.ClassName.Length == 0 ? "Jewels" : row.ClassName, row.DropLevel, row.PropertiesText,
                    new BaseProps(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null),
                    row.Implicits ?? [], [], row.Tags ?? [], "", row.Art));
        }

        var baseImplicits = new Dictionary<string, BaseImplicit[]>(StringComparer.Ordinal);
        if (root.TryGetProperty("baseImplicits", out var implicitEl) && implicitEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in implicitEl.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array) continue;
                var lines = new List<BaseImplicit>();
                foreach (var line in property.Value.EnumerateArray())
                {
                    string mod = line.TryGetProperty("mod", out var m) ? m.GetString() ?? "" : "";
                    string text = line.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (mod.Length == 0 || text.Length == 0) continue;
                    var stats = new List<ImplicitStat>();
                    if (line.TryGetProperty("stats", out var statsEl) && statsEl.ValueKind == JsonValueKind.Array)
                        foreach (var stat in statsEl.EnumerateArray())
                            stats.Add(new(stat.TryGetProperty("id", out var sid) ? sid.GetString() ?? "" : "",
                                stat.TryGetProperty("min", out var min) ? min.GetDecimal() : 0m,
                                stat.TryGetProperty("max", out var max) ? max.GetDecimal() : 0m));
                    lines.Add(new(mod, text, [.. stats]));
                }
                if (lines.Count > 0) baseImplicits[property.Name] = [.. lines];
            }
        }

        return new AffixData(mods, tagPools, jewelBases, ReadStrings(root, "jewelTags"), baseImplicits);
    }

    private static string[] ReadStrings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0)]
            : [];
}
