namespace PoeBuilder.Core.Calculation;

/// <summary>
/// Hit-chance formulas used by the current PoB2 defence reference. These are rating-vs-rating
/// calculations only; entropy, avoidance, block, suppression and damage mitigation are separate stages.
/// The exact rounding and data values remain target-patch verification items.
/// </summary>
public static class DefenceCalculator
{
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
}
