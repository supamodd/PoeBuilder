using System.Security.Cryptography;
using System.Text.Json;

namespace PoeBuilder.Core.Equipment;

/// <summary>PoB2's own data for one unique item: base type, variant list and the modifier lines with
/// their <c>{variant:N}</c> / <c>{tags:…}</c> filters (PathOfBuilding-PoE2-master/src/Data/Uniques/*.lua).
/// The pinned RePoE export carries identities only, so this is what makes a unique's modifiers
/// available to the calculator and the item editors.</summary>
public sealed record UniqueModLine(string Line, int[] Variants, string[] Tags);

public sealed record UniqueData(string Name, string BaseType, string ItemClass, string File,
    string[] Variants, int SelectedVariant, int Implicits, int RequiresLevel, string Source, UniqueModLine[] Mods);

/// <summary>Loaded <c>uniques.json</c>. A missing file is an empty index; a checksum mismatch fails closed.
/// Honest limit: PoB2 marks a handful of uniques with alt variants ("Has Alt Variant", Morior Invictus,
/// Grand Spectrum…). Their modifier lines are numbered in more than one dimension, which this index does
/// not model yet — <see cref="ModsFor"/> therefore returns only the lines matching the single
/// "Selected Variant" PoB2 records. Imported items are unaffected: their own text is authoritative and
/// carries the exact lines and rolls.</summary>
public sealed class UniqueDataIndex
{
    public const string Sha256 = "f14f0cc825eca0de7cf55a2a96d57c73ce83f8b810394f51ab45daaf05a5cb27";
    public static readonly UniqueDataIndex Empty = new(new Dictionary<string, UniqueData>(StringComparer.OrdinalIgnoreCase));

    private readonly IReadOnlyDictionary<string, UniqueData> _byName;
    private UniqueDataIndex(IReadOnlyDictionary<string, UniqueData> byName) => _byName = byName;

    public int Count => _byName.Count;
    public IReadOnlyDictionary<string, UniqueData> ByName => _byName;
    public UniqueData? For(string? name) =>
        name is not null && _byName.TryGetValue(name.Trim(), out var data) ? data : null;

    /// <summary>Modifier lines that apply to the item's variant. PoB2 stores the variant the game
    /// currently uses in "Selected Variant: N" (Morior Invictus: 29 of 29); without one the last listed
    /// variant ("Current") is the live one. A line without a variant filter always applies. Lines keep
    /// the game's range notation (e.g. "+(10-20) to Strength"); a consumer resolves the roll.</summary>
    public IReadOnlyList<UniqueModLine> ModsFor(string? name)
    {
        var data = For(name);
        if (data is null) return [];
        int variant = data.SelectedVariant > 0 ? data.SelectedVariant : data.Variants.Length;
        return [.. data.Mods.Where(m => m.Variants.Length == 0 || m.Variants.Contains(variant))];
    }

    public static UniqueDataIndex Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        var bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unique item data checksum mismatch.");
        using var json = JsonDocument.Parse(bytes);
        var byName = new Dictionary<string, UniqueData>(StringComparer.OrdinalIgnoreCase);
        if (json.RootElement.TryGetProperty("uniques", out var uniques))
        {
            foreach (var entry in uniques.EnumerateArray())
            {
                string name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (name.Length == 0) continue;
                var mods = new List<UniqueModLine>();
                if (entry.TryGetProperty("mods", out var modEl) && modEl.ValueKind == JsonValueKind.Array)
                    foreach (var mod in modEl.EnumerateArray())
                    {
                        string line = mod.TryGetProperty("line", out var l) ? l.GetString() ?? "" : "";
                        if (line.Length == 0) continue;
                        mods.Add(new(line, ReadInts(mod, "variants"), ReadStrings(mod, "tags")));
                    }
                var data = new UniqueData(name,
                    entry.TryGetProperty("baseType", out var bt) ? bt.GetString() ?? "" : "",
                    entry.TryGetProperty("itemClass", out var ic) ? ic.GetString() ?? "" : "",
                    entry.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "",
                    ReadStrings(entry, "variants"),
                    ReadInt(entry, "selectedVariant"),
                    ReadInt(entry, "implicits"),
                    ReadInt(entry, "requiresLevel"),
                    entry.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "",
                    [.. mods]);
                // A name can appear several times (Grand Spectrum, Grip of Kulemak…): keep the first
                // entry so every lookup is deterministic, exactly like the catalog's identity table.
                byName.TryAdd(name, data);
            }
        }
        return new(byName);
    }

    private static int ReadInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static int[] ReadInts(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetInt32())];
    }

    private static string[] ReadStrings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0)];
    }
}
