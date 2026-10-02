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
    /// <summary>Pinned checksum of <c>affixes.json</c> (build/extract-poe2-affixes.ps1): the spawn tags of
    /// every affix plus the PoE2 jewel bases the catalog was built without.</summary>
    public const string AffixSha256 = "8910e352b125027e54a9fbcdee5c295f3aba652136d1fc00ba1b80d20d64bc46";
    public GameData Data { get; }
    public IReadOnlyDictionary<string, ItemBase> Bases { get; }
    /// <summary>Pinned bases by their display name (first entry wins where a name repeats, exactly like
    /// the identity tables). A unique's data names its base type as text ("Grand Regalia"), and so does an
    /// imported item's second line, so this is how either reaches the base's own art, requirements and
    /// implicit text without a linear scan of 1845 entries.</summary>
    public IReadOnlyDictionary<string, ItemBase> BasesByName { get; }
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
    /// <summary>The pinned affix table (affixes.json next to the catalog): the spawn tags every affix may
    /// roll on and the PoE2 jewel bases. Empty for an older dataset, which then behaves exactly as
    /// before — the catalog's own per-class pools stay in charge.</summary>
    public AffixData Affix { get; }
    /// <summary>The PoE2 jewel bases (Ruby, Emerald, Sapphire, Diamond, Timeless, Time-Lost …), already
    /// part of <see cref="Bases"/>. They fit no equipment slot; they are socketed into tree jewel sockets.</summary>
    public IReadOnlyList<ItemBase> JewelBases => Affix.JewelBases;
    /// <summary>A jewel base, told apart by its item class rather than by a missing base id.</summary>
    public static bool IsJewel(ItemBase item) => item.ItemClass.Equals("Jewel", StringComparison.OrdinalIgnoreCase);
    public Vitality Vitals => Data.Vitals;
    public IReadOnlyDictionary<string, MonsterLevel> Monsters => Data.Monsters;

    private GameCatalog(GameData data, SkillDataIndex skillData, UniqueDataIndex uniqueData, QuestRewardIndex questRewards, AffixData affix)
    {
        Data = data;
        Affix = affix;
        var mods = data.Mods.ToDictionary(x => x.Id);
        // An affix the catalog did not carry (every jewel affix, and the equipment affixes only a spawn tag
        // reaches) joins the table with its own kind and level. A catalog entry always wins.
        foreach (var mod in affix.Mods.Values) mods.TryAdd(mod.Id, mod);
        Mods = mods;
        var bases = data.Bases.ToDictionary(x => x.Id);
        var basesByName = new Dictionary<string, ItemBase>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in data.Bases) basesByName.TryAdd(b.Name, b);
        foreach (var jewel in affix.JewelBases)
        {
            if (!bases.TryAdd(jewel.Id, jewel)) continue;
            basesByName.TryAdd(jewel.Name, jewel);
        }
        // A base's own implicit is made reproducible by the affix extraction (base_items.json +
        // mods.json): when the pinned table knows a base's implicit, it is authoritative and replaces
        // whatever the catalog generator happened to include, so the editor's tooltip and the base's
        // implicit stats always come from the same verified source. A base without an extracted set
        // keeps the catalog's own data untouched.
        foreach (var (baseId, implicits) in Affix.BaseImplicits)
        {
            if (implicits.Length == 0 || !bases.TryGetValue(baseId, out var b)) continue;
            bases[baseId] = b with
            {
                Implicits = implicits.Select(i => i.Text).ToArray(),
                ImplicitStats = implicits.SelectMany(i => i.Stats).ToArray()
            };
        }
        Bases = bases;
        BasesByName = basesByName;
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
        // The jewel-affix export carries no group/kind/level metadata, only id/name/text/stats, so the
        // pinned affix table supplies kind and level wherever it knows the affix; an id only the catalog
        // has keeps an empty kind (the jewel editor then treats it as "no group conflict").
        var jewelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in Affix.JewelTags)
            if (Affix.TagPools.TryGetValue(tag, out var pooled))
                foreach (var id in pooled) jewelIds.Add(id);
        var jewelMods = new List<ItemMod>(jewelIds.Count + 64);
        foreach (var id in jewelIds)
            if (Mods.TryGetValue(id, out var mod)) jewelMods.Add(mod);
        foreach (var mod in data.JewelMods ?? [])
        {
            if (!jewelIds.Add(mod.Id)) continue;
            jewelMods.Add(mod with { Groups = mod.Groups ?? [], Kind = mod.Kind ?? "" });
        }
        JewelMods = jewelMods;
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
        var affix = AffixData.Load(Path.Combine(folder, "affixes.json"), AffixSha256);
        return new(data, skillData, uniqueData, questRewards, affix);
    }
    /// <summary>Every affix the base can roll at that item level: the catalog's own class pool first, then
    /// the affixes of the pinned affix table whose spawn tags the base carries. A base with no catalog pool
    /// (a jewel) is answered entirely from the affix table, which is why jewels finally have a real pool.</summary>
    public IEnumerable<ItemMod> ModsFor(ItemBase item, int level)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (Data.ModPools.TryGetValue(item.ModPool, out var ids))
            foreach (var id in ids)
                if (Mods.TryGetValue(id, out var mod) && mod.Level <= level && seen.Add(id)) yield return mod;
        foreach (var tag in item.Tags)
            if (Affix.TagPools.TryGetValue(tag, out var pooled))
                foreach (var id in pooled)
                    if (Mods.TryGetValue(id, out var mod) && mod.Level <= level && seen.Add(id)) yield return mod;
    }
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
