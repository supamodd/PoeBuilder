using System.Text.RegularExpressions;

namespace PoeBuilder.Core.Calculation;

/// <summary>Deterministic mapper from the English modifier lines of an imported unique item's
/// user-provided text (GearItem.Notes) to stat ids the calculator already understands. Only exact
/// PoE2/PoB wording shapes are matched; anything else stays untouched in the item text.</summary>
public static class UniqueTextParser
{
    private static readonly Regex HeaderLine = new(
        @"^(Rarity:|Item Class:|Item Level:|Level:|Requirements:|Unique ID:|Corrupted|Sockets:|--------|Implicits?:|unclaimed|Unmodifiable|Size:|Type:|Base:)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AllAttributes = new(@"^[+-]?\s*(\d+)\s+to\s+all\s+Attributes$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SingleAttribute = new(@"^[+-]?\s*(\d+)\s+to\s+(Strength|Dexterity|Intelligence)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FlatPool = new(@"^[+-]?\s*(\d+)\s+to\s+maximum\s+(Life|Mana|Energy Shield|Ward)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PercentPool = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+maximum\s+(Life|Mana|Energy Shield|Ward)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PercentMaxPool = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+(Energy Shield|Ward)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Resistance = new(@"^[+-]?\s*(\d+)%\s+to\s+(Fire|Cold|Lightning|Chaos)\s+Resistance$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AllResistance = new(@"^[+-]?\s*(\d+)%\s+to\s+(?:all\s+)?Elemental Resistances$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaxResistance = new(@"^[+-]?\s*(\d+)%\s+to\s+maximum\s+(Fire|Cold|Lightning|Chaos)\s+Resistance$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DamageInc = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+((?:Physical|Fire|Cold|Lightning|Chaos|Elemental|Attack|Spell)\s)?Damage$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpeedInc = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+(Attack|Cast|Skill)\s+Speed$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CritChanceInc = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Critical Strike Chance$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CritBonusTo = new(@"^[+-]?\s*(\d+)%\s+to\s+Critical Damage Bonus$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CritBonusInc = new(@"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Critical Damage Bonus$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AddedDamage = new(@"^Adds\s+(\d+)\s+to\s+(\d+)\s+(Physical|Fire|Cold|Lightning|Chaos)\s+Damage$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LifeRegenPercent = new(@"^Regenerate\s+([+-]?\d+(?:\.\d+)?)%\s+of\s+(?:maximum\s+)?Life\s+per\s+second$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LifeRegenFlat = new(@"^[+-]?\s*(\d+)\s+Life Regenerated\s+per\s+second$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GainAsExtra = new(@"^Gain\s+(\d+(?:\.\d+)?)%\s+of\s+(Physical|Fire|Cold|Lightning|Chaos)\s+Damage\s+as\s+Extra\s+(Fire|Cold|Lightning|Chaos)\s+Damage$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses every English modifier line of an imported unique's text into stat ids.</summary>
    public static IReadOnlyList<(string Id, decimal Value)> ParseMods(string? text)
    {
        var result = new List<(string, decimal)>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || HeaderLine.IsMatch(line)) continue;
            MatchLine(line, result);
        }
        return result;
    }

    private static decimal D(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private static void MatchLine(string line, List<(string, decimal)> result)
    {
        // ---- curated ordering: most specific shapes first ----
        if (AllAttributes.Match(line) is { Success: true } attrs)
        {
            result.Add(("additional_all_attributes", D(attrs.Groups[1].Value)));
            return;
        }
        if (SingleAttribute.Match(line) is { Success: true } one)
        {
            result.Add((one.Groups[2].Value.ToLowerInvariant() switch
            {
                "strength" => "base_strength",
                "dexterity" => "base_dexterity",
                _ => "base_intelligence"
            }, D(one.Groups[1].Value)));
            return;
        }
        if (FlatPool.Match(line) is { Success: true } flat)
        {
            result.Add((flat.Groups[2].Value.ToLowerInvariant() switch
            {
                "life" => "base_maximum_life",
                "mana" => "base_maximum_mana",
                "energy shield" => "base_maximum_energy_shield",
                _ => "base_maximum_ward"
            }, D(flat.Groups[1].Value)));
            return;
        }
        if (PercentPool.Match(line) is { Success: true } pct)
        {
            result.Add((pct.Groups[2].Value.ToLowerInvariant() switch
            {
                "life" => "maximum_life_+%",
                "mana" => "maximum_mana_+%",
                "energy shield" => "maximum_energy_shield_+%",
                _ => "maximum_ward_+%"
            }, D(pct.Groups[1].Value)));
            return;
        }
        // PoE2 tooltips often omit "maximum" before the defence name in the increased line.
        if (PercentMaxPool.Match(line) is { Success: true } maxPct)
        {
            result.Add((maxPct.Groups[2].Value.ToLowerInvariant() switch
            {
                "energy shield" => "maximum_energy_shield_+%",
                _ => "maximum_ward_+%"
            }, D(maxPct.Groups[1].Value)));
            return;
        }
        if (Resistance.Match(line) is { Success: true } res)
        {
            result.Add(("base_" + res.Groups[2].Value.ToLowerInvariant() + "_damage_resistance_%", D(res.Groups[1].Value)));
            return;
        }
        if (AllResistance.Match(line) is { Success: true } allRes)
        {
            result.Add(("base_resist_all_elements_%", D(allRes.Groups[1].Value)));
            return;
        }
        if (MaxResistance.Match(line) is { Success: true } maxRes)
        {
            result.Add(("maximum_" + maxRes.Groups[2].Value.ToLowerInvariant() + "_damage_resistance_%", D(maxRes.Groups[1].Value)));
            return;
        }
        if (DamageInc.Match(line) is { Success: true } dmg)
        {
            string scope = (dmg.Groups[2].Value ?? "").Trim();
            string id = scope.ToLowerInvariant() switch
            {
                "physical" => "physical_damage_+%",
                "fire" => "fire_damage_+%",
                "cold" => "cold_damage_+%",
                "lightning" => "lightning_damage_+%",
                "chaos" => "chaos_damage_+%",
                "elemental" => "elemental_damage_+%",
                "attack" => "attack_damage_+%",
                "spell" => "spell_damage_+%",
                _ => "damage_+%"
            };
            result.Add((id, D(dmg.Groups[1].Value)));
            return;
        }
        if (SpeedInc.Match(line) is { Success: true } speed)
        {
            string id = speed.Groups[2].Value.ToLowerInvariant() switch
            {
                "attack" => "attack_speed_+%",
                "cast" => "base_cast_speed_+%",
                _ => "skill_speed_+%"
            };
            result.Add((id, D(speed.Groups[1].Value)));
            return;
        }
        if (CritChanceInc.Match(line) is { Success: true } critChance)
        {
            result.Add(("critical_strike_chance_+%", D(critChance.Groups[1].Value)));
            return;
        }
        if (CritBonusTo.Match(line) is { Success: true } critBonus)
        {
            result.Add(("base_critical_strike_multiplier_+", D(critBonus.Groups[1].Value)));
            return;
        }
        if (CritBonusInc.Match(line) is { Success: true } critBonus2)
        {
            result.Add(("base_critical_strike_multiplier_+", D(critBonus2.Groups[1].Value)));
            return;
        }
        if (AddedDamage.Match(line) is { Success: true } added)
        {
            string type = added.Groups[3].Value.ToLowerInvariant();
            result.Add(("attack_minimum_added_" + type + "_damage", D(added.Groups[1].Value)));
            result.Add(("attack_maximum_added_" + type + "_damage", D(added.Groups[2].Value)));
            return;
        }
        if (LifeRegenPercent.Match(line) is { Success: true } regenPct)
        {
            result.Add(("life_regeneration_percent_per_second", D(regenPct.Groups[1].Value)));
            return;
        }
        if (LifeRegenFlat.Match(line) is { Success: true } regenFlat)
        {
            result.Add(("base_life_regeneration_rate_per_minute", D(regenFlat.Groups[1].Value) * 60m));
            return;
        }
        if (GainAsExtra.Match(line) is { Success: true } gain)
        {
            string source = gain.Groups[2].Value.ToLowerInvariant();
            string dest = gain.Groups[3].Value.ToLowerInvariant();
            result.Add(("non_skill_base_" + source + "_damage_%_to_gain_as_" + dest, D(gain.Groups[1].Value)));
            return;
        }
        // No deterministic pattern matched: consult the pinned reverse stat-translation table.
        if (ReverseStatTextMatcher.TryMatch(line) is { } reverse)
        {
            result.AddRange(reverse);
            return;
        }
    }
}