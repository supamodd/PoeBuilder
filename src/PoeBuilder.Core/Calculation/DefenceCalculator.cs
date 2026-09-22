namespace PoeBuilder.Core.Calculation;

/// <summary>
/// Hit-chance formulas used by the current PoB2 defence reference. These are rating-vs-rating
/// calculations only; entropy, avoidance, block, suppression and damage mitigation are separate stages.
/// The exact rounding and data values remain target-patch verification items.
/// </summary>
public static class DefenceCalculator
{
    // Current PoB2 reference constants. They are kept here rather than hidden in the
    // character calculator so each boundary remains directly unit-testable.
    public const decimal DeflectionChanceCap = 95m;
    public const decimal DeflectionDamagePreventedPercent = 40m;
    public const decimal BaseBlockChanceMaximum = 50m;
    public const decimal BlockChanceCap = 90m;
    public const decimal SpellSuppressionChanceCap = 100m;
    public const decimal BaseSpellSuppressionEffectPercent = 50m;
    public const decimal BaseEnergyShieldRechargePercentPerSecond = 12.5m;
    public const decimal BaseEnergyShieldRechargeDelaySeconds = 4m;

    /// <summary>Player attack hit chance against a target's evasion.</summary>
    /// <remarks>Non-positive accuracy returns the 5% floor. With positive accuracy, a target with
    /// non-positive evasion is treated as having no avoidance; this keeps the zero/zero case deterministic.</remarks>
    public static decimal PlayerHitChance(decimal targetEvasion, decimal accuracy, bool uncapped = false)
    {
        if (accuracy <= 0) return 5m;
        decimal denominator = accuracy + targetEvasion * 0.3m;
        if (denominator <= 0) return 5m;
        decimal raw = accuracy * 1.25m / denominator * 100m;
        decimal rounded = Math.Round(raw, 0, MidpointRounding.AwayFromZero);
        return uncapped ? Math.Max(rounded, 5m) : Math.Clamp(rounded, 5m, 100m);
    }

    /// <summary>Default monster hit chance against the player's evasion.</summary>
    public static decimal MonsterHitChance(decimal playerEvasion, decimal monsterAccuracy)
    {
        if (playerEvasion <= 0) return 100m;
        if (monsterAccuracy <= 0) return 5m;
        decimal denominator = playerEvasion + 4m * monsterAccuracy;
        decimal raw = (1m - 0.95m * playerEvasion / denominator) * 100m;
        decimal rounded = Math.Round(raw, 0, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, 5m, 100m);
    }

    /// <summary>Deflection chance against one attack accuracy value. This is a chance to avoid
    /// the hit, not the deflection rating itself. The current PoB2 reference caps it at 95%.</summary>
    public static decimal DeflectionChance(decimal deflection, decimal accuracy,
        decimal chanceCap = DeflectionChanceCap)
    {
        if (deflection < 1 || accuracy < 0) return 0;
        decimal denominator = accuracy + deflection * 0.12m;
        if (denominator <= 0) return 0;
        decimal chanceToNotDeflect = accuracy / denominator * 150m - 50m;
        decimal chance = 100m - Math.Round(chanceToNotDeflect, 0, MidpointRounding.AwayFromZero);
        return Math.Clamp(chance, 0, chanceCap);
    }

    /// <summary>Spell suppression chance after the current PoB2 cap.</summary>
    public static decimal SpellSuppressionChance(decimal totalChance,
        decimal chanceCap = SpellSuppressionChanceCap)
        => Math.Clamp(totalChance, 0, chanceCap);

    /// <summary>Average spell-hit multiplier after a chance to suppress and the amount prevented.
    /// This is not applied to the current successful-hit EHP vector without a spell scenario.</summary>
    public static decimal SpellSuppressionDamageMultiplier(decimal suppressionChance,
        decimal suppressionEffect = BaseSpellSuppressionEffectPercent)
    {
        decimal chance = SpellSuppressionChance(suppressionChance) / 100m;
        decimal effect = Math.Max(0, suppressionEffect) / 100m;
        return Math.Max(0, 1 - chance * effect);
    }

    /// <summary>Maximum attack block chance. PoE2's current reference starts at 50%, allows
    /// explicit maximum-block additions/overrides, and has a 90% global cap.</summary>
    public static decimal BlockChanceMaximum(decimal maximumBlockIncrease = 0,
        decimal? maximumBlockOverride = null,
        decimal baseMaximum = BaseBlockChanceMaximum,
        decimal globalCap = BlockChanceCap)
    {
        decimal maximum = maximumBlockOverride ?? baseMaximum + maximumBlockIncrease;
        return Math.Clamp(maximum, 0, globalCap);
    }

    /// <summary>Final attack block chance after global increases, flat additions and maximum cap.</summary>
    public static decimal BlockChance(decimal baseBlock, decimal increasedPercent = 0,
        decimal additionalBlock = 0, decimal maximumBlockIncrease = 0,
        decimal? maximumBlockOverride = null)
    {
        decimal total = Math.Round((baseBlock + additionalBlock) * (1 + increasedPercent / 100m),
            0, MidpointRounding.AwayFromZero);
        return Math.Clamp(total, 0, BlockChanceMaximum(maximumBlockIncrease, maximumBlockOverride));
    }

    /// <summary>Spell block has a separate base chance but the same current maximum-cap contract.</summary>
    public static decimal SpellBlockChance(decimal baseSpellBlock, decimal increasedPercent = 0,
        decimal additionalSpellBlock = 0, decimal maximumBlockIncrease = 0,
        decimal? maximumBlockOverride = null)
        => BlockChance(baseSpellBlock, increasedPercent, additionalSpellBlock,
            maximumBlockIncrease, maximumBlockOverride);

    /// <summary>Panel ES recharge rate before recovery modifiers and combat interruption state.</summary>
    public static decimal EnergyShieldRechargePerSecond(decimal energyShield, decimal increasedPercent = 0,
        decimal basePercentPerSecond = BaseEnergyShieldRechargePercentPerSecond)
    {
        if (energyShield <= 0 || basePercentPerSecond <= 0) return 0;
        return Math.Max(0, energyShield * basePercentPerSecond / 100m * (1 + increasedPercent / 100m));
    }

    /// <summary>Delay before ES recharge starts. A non-positive speed multiplier cannot produce a
    /// finite delay, so the result is null rather than an invented value.</summary>
    public static decimal? EnergyShieldRechargeDelaySeconds(decimal fasterStartPercent,
        decimal baseDelaySeconds = BaseEnergyShieldRechargeDelaySeconds)
    {
        decimal speedMultiplier = 1 + fasterStartPercent / 100m;
        return baseDelaySeconds < 0 || speedMultiplier <= 0 ? null : baseDelaySeconds / speedMultiplier;
    }
}
