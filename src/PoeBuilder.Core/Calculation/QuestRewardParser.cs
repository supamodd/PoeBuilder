using System.Globalization;
using System.Text.RegularExpressions;

namespace PoeBuilder.Core.Calculation;

/// <summary>
/// Translates the quest-reward lines of an imported Path of Exile 2 build into stat ids. The lines
/// come from PoB2's config ("Quest Rewards" section, generated from
/// PathOfBuilding-PoE2-master src/Data/QuestRewards.lua), e.g. "+10% to Cold Resistance",
/// "+30 to Spirit", "5% increased maximum Life". Only the shapes that exist in that file are
/// matched; anything else is reported as unaccounted by the calculator, never guessed.
/// </summary>
public static class QuestRewardParser
{
    private static readonly Regex Resistance = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+to\s+(Fire|Cold|Lightning|Chaos)\s+Resistance$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AllElementalResistance = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+to\s+all\s+Elemental\s+Resistances$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FlatPool = new(
        @"^([+-]?\d+(?:\.\d+)?)\s+to\s+(?:maximum\s+)?(Life|Mana|Energy Shield|Spirit)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FlatAttribute = new(
        @"^([+-]?\d+(?:\.\d+)?)\s+to\s+(Strength|Dexterity|Intelligence|all Attributes)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PercentPool = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+(?:maximum\s+)?(Life|Mana|Energy Shield|Ward)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GlobalDefences = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Global\s+Armour,\s*Evasion\s+and\s+Energy Shield$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Movement = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Movement\s+Speed$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ManaRegeneration = new(
        @"^([+-]?\d+(?:\.\d+)?)%\s+increased\s+Mana\s+Regeneration\s+Rate$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArmourToElemental = new(
        @"^[+-]?\s*(\d+(?:\.\d+)?)%\s+of\s+Armour\s+also\s+applies\s+to\s+Elemental\s+Damage$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeflectionFromEvasion = new(
        @"^Gain\s+Deflection\s+Rating\s+equal\s+to\s+(\d+(?:\.\d+)?)%\s+of\s+Evasion\s+Rating$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EsRechargeStart = new(
        @"^(\d+(?:\.\d+)?)%\s+faster\s+start\s+of\s+Energy\s+Shield\s+Recharge$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Parses one quest-reward line into stat ids. Empty when the shape is not modelled.</summary>
    public static IReadOnlyList<(string Id, decimal Value)> ParseLine(string? line)
    {
        var result = new List<(string, decimal)>();
        if (string.IsNullOrWhiteSpace(line)) return result;
        string text = line.Trim();
        if (Resistance.Match(text) is { Success: true } res)
        {
            result.Add(("base_" + res.Groups[2].Value.ToLowerInvariant() + "_damage_resistance_%", D(res.Groups[1].Value)));
            return result;
        }
        if (AllElementalResistance.Match(text) is { Success: true } allRes)
        {
            result.Add(("base_resist_all_elements_%", D(allRes.Groups[1].Value)));
            return result;
        }
        if (FlatPool.Match(text) is { Success: true } pool)
        {
            result.Add((pool.Groups[2].Value.ToLowerInvariant() switch
            {
                "life" => "base_maximum_life",
                "mana" => "base_maximum_mana",
                "energy shield" => "base_maximum_energy_shield",
                _ => "base_maximum_spirit"
            }, D(pool.Groups[1].Value)));
            return result;
        }
        if (FlatAttribute.Match(text) is { Success: true } attribute)
        {
            result.Add((attribute.Groups[2].Value.ToLowerInvariant() switch
            {
                "strength" => "base_strength",
                "dexterity" => "base_dexterity",
                "intelligence" => "base_intelligence",
                _ => "additional_all_attributes"
            }, D(attribute.Groups[1].Value)));
            return result;
        }
        if (PercentPool.Match(text) is { Success: true } pct)
        {
            result.Add((pct.Groups[2].Value.ToLowerInvariant() switch
            {
                "life" => "maximum_life_+%",
                "mana" => "maximum_mana_+%",
                "energy shield" => "maximum_energy_shield_+%",
                _ => "maximum_ward_+%"
            }, D(pct.Groups[1].Value)));
            return result;
        }
        if (GlobalDefences.Match(text) is { Success: true } defences)
        {
            result.Add(("defences_+%", D(defences.Groups[1].Value)));
            return result;
        }
        if (Movement.Match(text) is { Success: true } movement)
        {
            result.Add(("movement_speed_+%", D(movement.Groups[1].Value)));
            return result;
        }
        if (ManaRegeneration.Match(text) is { Success: true } regen)
        {
            result.Add(("mana_regeneration_rate_+%", D(regen.Groups[1].Value)));
            return result;
        }
        if (ArmourToElemental.Match(text) is { Success: true })
        {
            result.Add(("armour_%_applies_to_fire_cold_lightning_damage", 1m));
            return result;
        }
        if (DeflectionFromEvasion.Match(text) is { Success: true } deflection)
        {
            result.Add(("base_deflection_rating_%_of_evasion_rating", D(deflection.Groups[1].Value)));
            return result;
        }
        if (EsRechargeStart.Match(text) is { Success: true } recharge)
        {
            result.Add(("energy_shield_recharge_rate_+%", D(recharge.Groups[1].Value)));
            return result;
        }
        return result;
    }

    /// <summary>Parses a multi-line reward ("a\nb"): one stat list per line, concatenated.</summary>
    public static IReadOnlyList<(string Id, decimal Value)> Parse(string? text)
    {
        var result = new List<(string, decimal)>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
            result.AddRange(ParseLine(raw));
        return result;
    }

    private static decimal D(string s) => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
}
