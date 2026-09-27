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
    Dictionary<string, decimal>? Static,
    int? Cooldown)
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
    /// <summary>PoB2's own per-skill data (skilldata.json next to the catalog): attack damage and
    /// attack-speed multipliers, per-level crit/cooldown, gem quality stats and effect-level values
    /// the pinned RePoE export does not carry. Empty when the file is absent.</summary>
    public SkillDataIndex SkillData { get; }
    /// <summary>PoB2's quest-reward table (questrewards.json next to the catalog): the one source of
    /// truth for the Quest Rewards tab and for the rewards an imported build carries. Empty when the
    /// file is absent.</summary>
    public QuestRewardIndex QuestRewards { get; }
    /// <summary>PoB2's own unique-item data (uniques.json next to the catalog): base type, variant list
    /// and modifier lines. The pinned RePoE export carries unique identities only, so this is what
    /// makes unique modifiers available. Empty when the file is absent.</summary>
    public UniqueDataIndex UniqueData { get; }
    /// <summary>Every pinned mod by id: the item affix pool and the jewel pool together. The
    /// importer's text matcher searches both (they share the same English wording), so a stored roll
    /// can come from either and the calculator must be able to resolve both — looking in only one of
    /// them silently dropped the other's modifiers.</summary>
    public IReadOnlyDictionary<string, ItemMod> AllMods { get; }
    public Vitality Vitals => Data.Vitals;
    public IReadOnlyDictionary<string, MonsterLevel> Monsters => Data.Monsters;

    private GameCatalog(GameData data, SkillDataIndex skillData, UniqueDataIndex uniqueData, QuestRewardIndex questRewards)
    {
        Data = data; Bases = data.Bases.ToDictionary(x => x.Id); Mods = data.Mods.ToDictionary(x => x.Id);
        SkillData = skillData;
        QuestRewards = questRewards;
        UniqueData = uniqueData;
        Augments = data.Augments.ToDictionary(x => x.Id); Gems = data.Gems.ToDictionary(x => x.Id);
        var uniqueItems = (data.Uniques ?? []).ToList();
        AddMissingUnique("Hands of Wisdom and Action", "Gloves", "Art/2DItems/Armours/Gloves/Uniques/HandsOfWisdomAndAction.dds");
        AddMissingUnique("Morior Invictus", "Body Armour", "Art/2DItems/Armours/BodyArmours/Uniques/MoriorInvictus.dds");
        AddMissingUnique("Headhunter", "Belt", "Art/2DItems/Belts/Uniques/Headhunter.dds");
        Uniques = uniqueItems.GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        void AddMissingUnique(string name, string itemClass, string icon)
        {
            if (!uniqueItems.Any(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                uniqueItems.Add(new("supplement:" + name, name, itemClass, icon));
        }
        // The jewel-affix export carries no group/kind/level metadata, only id/name/text/stats.
        // Normalize missing groups to an empty set and missing kind to the empty string so the
        // jewel editor can always treat a jewel mod like an ordinary item mod with no group conflict.
        JewelMods = (data.JewelMods ?? []).Select(m => m with { Groups = m.Groups ?? [], Kind = m.Kind ?? "" }).ToArray();
        // Item affixes win over jewel affixes when both carry the same id, but both must be resolvable.
        var allMods = new Dictionary<string, ItemMod>(Mods);
        foreach (var mod in JewelMods) allMods.TryAdd(mod.Id, mod);
        AllMods = allMods;
    }
    public static GameCatalog Load(string path)
    {
        if (new FileInfo(path).Length > 48 * 1024 * 1024) throw new InvalidDataException("Catalog exceeds size limit.");
        var bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Game catalog checksum mismatch.");
        var data = JsonSerializer.Deserialize<GameData>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty catalog.");
        if (data.DatasetId != Dataset) throw new InvalidDataException("Wrong catalog identity.");
        // PoB2's per-skill and unique-item data sit next to the catalog and are verified by their own
        // pinned checksums.
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var skillData = SkillDataIndex.Load(Path.Combine(folder, "skilldata.json"));
        var uniqueData = UniqueDataIndex.Load(Path.Combine(folder, "uniques.json"));
        var questRewards = QuestRewardIndex.Load(Path.Combine(folder, "questrewards.json"));
        return new(data, skillData, uniqueData, questRewards);
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
