using System.Security.Cryptography;
using System.Text.Json;

namespace PoeBuilder.Core.Equipment;

/// <summary>Structured base properties from the pinned RePoE export. Values in display units
/// (percentages as whole points, movement speed and block as percent, damage in raw points).</summary>
public sealed record BaseProps(
    int? AttackTime, decimal? CritChance, decimal? PhysMin, decimal? PhysMax, int? Range,
    decimal? Armour, decimal? Evasion, decimal? EnergyShield, decimal? Ward, decimal? Block, decimal? MovementSpeed,
    int? ChargesMax, int? ChargesPerUse, int? Duration, decimal? LifePerUse, decimal? ManaPerUse, int? StackSize,
    int? ReqLevel, decimal? ReqStr, decimal? ReqDex, decimal? ReqInt)
{
    public bool IsWeapon => AttackTime is not null && PhysMin is not null;
}

public sealed record ImplicitStat(string Id, decimal Min, decimal Max);
public sealed record ItemBase(string Id, string Name, string ItemClass, string ClassName, int DropLevel,
    string PropertiesText, BaseProps Props, string[] Implicits, ImplicitStat[] ImplicitStats, string[] Tags, string ModPool, string Art)
{
    public override string ToString() => Name;
}
public sealed record ModStat(string Id, decimal Min, decimal Max);
public sealed record ItemMod(string Id, string Name, string Kind, int Level, string[] Groups, string Text, ModStat[] Stats)
{
    public string DisplayName => $"{Name} · {Kind} · ilvl {Level} · {Text}";
    public override string ToString() => DisplayName;
}
public sealed record Augment(string Id, string Name, string Kind, int Level, string Limit, Dictionary<string, string> Effects)
{
    public string DisplayName => $"{Name} · {Kind}";
    public override string ToString() => DisplayName;
}

/// <summary>Per-level values of a gem's primary skill stat set (pinned RePoE skills.json).</summary>
public sealed record GemSkill(
    int? CastTime, decimal? Crit, string[] Order,
    Dictionary<string, Dictionary<string, decimal>> Levels,
    Dictionary<string, Dictionary<string, decimal>> Costs,
    Dictionary<string, Dictionary<string, string>> StatText,
    Dictionary<string, decimal>? Static)
{
    /// <summary>Static (level-independent) stats of the skill: conversions, support multipliers, flags.</summary>
    public IReadOnlyDictionary<string, decimal> Statics => Static ?? new Dictionary<string, decimal>();

    public Dictionary<string, decimal>? LevelValues(int level)
    {
        for (int candidate = Math.Min(level, 40); candidate >= 1; candidate--)
            if (Levels.TryGetValue(candidate.ToString(), out var values)) return values;
        return Levels.Count == 0 ? null : Levels.Values.First();
    }

    public Dictionary<string, decimal>? LevelCosts(int level)
    {
        for (int candidate = Math.Min(level, 40); candidate >= 1; candidate--)
            if (Costs.TryGetValue(candidate.ToString(), out var values)) return values;
        return null;
    }
}

public sealed record Gem(string Id, string Name, string Kind, string Description, string[] Tags, int[] Levels,
    string[] RecommendedSupports, string Color, string Icon, GemSkill? Skill)
{
    public override string ToString() => Name;
}
/// <summary>Identity of a unique item from the pinned export. The pinned RePoE source exports NO
/// unique modifiers, so only the name/class/icon are verified data; imported uniques keep their
/// user-provided text and are displayed, never interpreted.</summary>
public sealed record UniqueItem(string Id, string Name, string ItemClass, string Icon)
{
    public override string ToString() => Name;
}
public sealed record Vitality(int BaseLife, int BaseMana);
public sealed record MonsterLevel(decimal? Accuracy, decimal? Armour, decimal? Evasion, decimal? Life, decimal? PhysicalDamage);
public sealed record GameData(string DatasetId, string SourceVersion, Vitality Vitals, Dictionary<string, MonsterLevel> Monsters,
    ItemBase[] Bases, ItemMod[] Mods, Dictionary<string, string[]> ModPools, Dictionary<string, string[]> CorruptedPools,
    Augment[] Augments, Gem[] Gems, UniqueItem[]? Uniques, ItemMod[]? JewelMods);

public sealed class GameCatalog
{
    public const string Dataset = "repoe-poe2-4.5.5.2-b818b843-r2";
    public const string Sha256 = "9a6dfd49b1579f6c37a7a97893d6cdf815e63476f0fba8b8e30c4d34991908ac";
    public GameData Data { get; }
    public IReadOnlyDictionary<string, ItemBase> Bases { get; }
    public IReadOnlyDictionary<string, ItemMod> Mods { get; }
    public IReadOnlyDictionary<string, Augment> Augments { get; }
    public IReadOnlyDictionary<string, Gem> Gems { get; }
    public IReadOnlyDictionary<string, UniqueItem> Uniques { get; }
    public IReadOnlyList<ItemMod> JewelMods { get; }
    public Vitality Vitals => Data.Vitals;
    public IReadOnlyDictionary<string, MonsterLevel> Monsters => Data.Monsters;

    private GameCatalog(GameData data)
    {
        Data = data; Bases = data.Bases.ToDictionary(x => x.Id); Mods = data.Mods.ToDictionary(x => x.Id);
        Augments = data.Augments.ToDictionary(x => x.Id); Gems = data.Gems.ToDictionary(x => x.Id);
        Uniques = (data.Uniques ?? []).GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        JewelMods = data.JewelMods ?? [];
    }
    public static GameCatalog Load(string path)
    {
        if (new FileInfo(path).Length > 48 * 1024 * 1024) throw new InvalidDataException("Catalog exceeds size limit.");
        var bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Game catalog checksum mismatch.");
        var data = JsonSerializer.Deserialize<GameData>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty catalog.");
        if (data.DatasetId != Dataset) throw new InvalidDataException("Wrong catalog identity.");
        return new(data);
    }
    public IEnumerable<ItemMod> ModsFor(ItemBase item, int level) => Data.ModPools.TryGetValue(item.ModPool, out var ids)
        ? ids.Select(id => Mods[id]).Where(m => m.Level <= level) : [];
    /// <summary>Per-class corruption-implicit pool (mods_by_base 'corrupted' kind). One corrupted implicit per item in game.</summary>
    public IEnumerable<ItemMod> CorruptedFor(ItemBase item) => Data.CorruptedPools.TryGetValue(item.ModPool, out var ids)
        ? ids.Select(id => Mods[id]) : [];
    public string AugmentEffect(ItemBase item, Augment augment)
    {
        // Exact class categories first, then explicitly mapped category families. Unknown targets fail closed.
        var keys = new List<string> { item.ItemClass, item.ClassName };
        if (item.ItemClass == "Body Armour") keys.Insert(0, "Body Armour");
        if (item.Tags.Contains("armour")) keys.Add("Armour");
        if (item.ItemClass is "Wand" or "Staff") keys.Add("Wand or Staff");
        if (item.Tags.Contains("weapon") && item.ItemClass is not ("Wand" or "Staff" or "Sceptre")) keys.Add("Martial Weapon");
        foreach (var key in keys) if (augment.Effects.TryGetValue(key, out var text)) return text;
        return "";
    }
}
public sealed class PlanningException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
