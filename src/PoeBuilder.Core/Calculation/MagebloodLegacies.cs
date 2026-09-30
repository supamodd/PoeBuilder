using System.Text.RegularExpressions;

namespace PoeBuilder.Core.Calculation;

/// <summary>
/// PoB2's Mageblood legacies. A "Legacy of X" line on the belt is only a MARKER in PoB2's own data
/// (<c>Data/ModCache.lua:6076-6089</c>: <c>LegacyOfX BASE 1</c> plus the <c>MagebloodEquipped</c> flag) — the
/// effects themselves live in a table in the engine (<c>Modules/CalcPerform.lua:65-141</c>) and are applied
/// once the equipment is known (<c>:1502-1529</c>):
/// <code>
/// local stacks = modDB:Sum("BASE", nil, name)                       -- how many copies of that legacy
/// local effectPerDupe = modDB:Sum("INC", nil, "MagesLegacyEffect")  -- "per duplicate" scaling
/// local globalEffect = 1 + totalDuplicates * (effectPerDupe / 100)
/// modDB:NewMod(entry.stat, entry.type, m_floor(globalEffect * entry.value), "Mageblood")
/// </code>
/// The values below are transcribed from that table, so the sheet shows what PoB2 shows (the owner's Diamond
/// legacy is the "+75% to Critical Hit Chance" one, and Amethyst is the +45% Chaos Resistance we were missing).
/// </summary>
public static class MagebloodLegacies
{
    /// <summary>One effect of a legacy: PoB2's stat name, its mod type and the value.</summary>
    public sealed record Effect(string Stat, string Type, decimal Value);

    /// <summary>The legacy effects, exactly as PoB2's <c>legacies</c> table defines them.</summary>
    public static readonly IReadOnlyDictionary<string, Effect[]> Effects =
        new Dictionary<string, Effect[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Amethyst"] = [new("ChaosResist", "BASE", 45)],
            ["Basalt"] = [new("Armour", "INC", 150)],
            ["Bismuth"] = [new("ElementalResist", "BASE", 45)],
            ["Diamond"] = [new("CritChance", "INC", 75)],
            ["Gold"] = [new("LootRarity", "INC", 45)],
            ["Granite"] = [new("Armour", "BASE", 2000)],
            ["Jade"] = [new("Evasion", "BASE", 2000)],
            ["Quicksilver"] = [new("MovementSpeed", "INC", 30)],
            ["Ruby"] = [new("FireResist", "BASE", 60), new("FireResistMax", "BASE", 5)],
            ["Sapphire"] = [new("ColdResist", "BASE", 60), new("ColdResistMax", "BASE", 5)],
            ["Silver"] = [new("Speed", "INC", 30), new("WarcrySpeed", "INC", 30), new("TotemPlacementSpeed", "INC", 30)],
            ["Stibnite"] = [new("Evasion", "INC", 150)],
            ["Sulphur"] = [new("Damage", "INC", 60)],
            ["Topaz"] = [new("LightningResist", "BASE", 60), new("LightningResistMax", "BASE", 5)]
        };

    private static readonly Regex LegacyLine = new(@"^Legacy of ([A-Za-z]+)\s*$", RegexOptions.Compiled);
    private static readonly Regex DuplicateEffect = new(
        @"^All Mage's Legacies have (\d+)% increased effect per duplicate Mage's Legacy you have\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Applies every legacy the item text names, with PoB2's duplicate scaling. Returns how many
    /// legacy effects reached the bucket; a legacy whose effect this model has no place for is reported.</summary>
    public static int Apply(StatBucket bucket, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var stacks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        decimal perDuplicate = 0;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (LegacyLine.Match(line) is { Success: true } legacy && Effects.ContainsKey(legacy.Groups[1].Value))
            {
                stacks[legacy.Groups[1].Value] = stacks.GetValueOrDefault(legacy.Groups[1].Value) + 1;
                continue;
            }
            if (DuplicateEffect.Match(line) is { Success: true } duplicate)
                perDuplicate = decimal.Parse(duplicate.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        if (stacks.Count == 0) return 0;
        // PoB2: a duplicate of the same legacy scales every legacy's effect (CalcPerform.lua:1513-1520).
        int duplicates = stacks.Values.Sum(n => Math.Max(0, n - 1));
        decimal globalEffect = 1 + duplicates * perDuplicate / 100m;
        int applied = 0;
        foreach (var name in stacks.Keys)
            foreach (var effect in Effects[name])
            {
                decimal value = Math.Floor(globalEffect * effect.Value);
                if (Place(bucket, effect.Stat, effect.Type, value)) applied++;
                else bucket.Extras["Mageblood:" + name + ":" + effect.Stat] = value;
            }
        bucket.Extras["MagebloodLegacies"] = stacks.Count;
        return applied;
    }

    /// <summary>Puts one legacy effect into the bucket the rest of the calculator reads. Returns false for the
    /// stats this model has no place for (rarity, warcry/totem placement speed), which the caller reports
    /// instead of dropping silently.</summary>
    private static bool Place(StatBucket bucket, string stat, string type, decimal value)
    {
        switch (stat)
        {
            case "ChaosResist": bucket.ChaosRes += value; return true;
            case "ElementalResist": bucket.FireRes += value; bucket.ColdRes += value; bucket.LightRes += value; return true;
            case "FireResist": bucket.FireRes += value; return true;
            case "ColdResist": bucket.ColdRes += value; return true;
            case "LightningResist": bucket.LightRes += value; return true;
            case "FireResistMax": bucket.FireMax += value; return true;
            case "ColdResistMax": bucket.ColdMax += value; return true;
            case "LightningResistMax": bucket.LightMax += value; return true;
            case "CritChance" when type == "INC": bucket.CritChanceInc += value; return true;
            case "Armour" when type == "INC": bucket.ArmourInc += value; return true;
            case "Armour" when type == "BASE": bucket.ArmourFlat += value; return true;
            case "Evasion" when type == "INC": bucket.EvInc += value; return true;
            case "Evasion" when type == "BASE": bucket.EvFlat += value; return true;
            case "MovementSpeed" when type == "INC": bucket.MoveInc += value; return true;
            case "Speed" when type == "INC": bucket.AddSkillSpeed("Mageblood:Speed", value); return true;
            case "Damage" when type == "INC": bucket.DamageInc += value; return true;
            default: return false;
        }
    }
}
