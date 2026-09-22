namespace PoeBuilder.Core.Calculation;

/// <summary>
/// Single-successful-hit effective hit-pool helpers. These deliberately do not model
/// hit chance, block, deflection, suppression, recovery, penetration or enemy configuration.
/// Armour reduction is hit-size dependent, so callers must provide the scenario's raw hit.
/// </summary>
public static class EhpCalculator
{
    public static decimal ResistanceDamageMultiplier(decimal resistancePercent)
        => Math.Max(0, 1m - resistancePercent / 100m);

    public static decimal ArmourDamageMultiplier(decimal armour, decimal rawHit,
        decimal armourRatio = 12m, decimal reductionCapPercent = 90m)
    {
        if (rawHit <= 0 || armour <= 0) return 1m;
        decimal reduction = armour / (armour + armourRatio * rawHit) * 100m;
        reduction = Math.Clamp(reduction, 0, reductionCapPercent);
        return 1m - reduction / 100m;
    }

    /// <summary>Returns EHP in raw incoming-damage units. Null means zero damage taken under
    /// this simplified scenario, which is an unbounded result rather than a fake finite number.</summary>
    public static decimal? EffectiveHitPool(decimal pool, decimal damageMultiplier)
    {
        if (pool <= 0) return 0;
        if (damageMultiplier <= 0) return null;
        return pool / damageMultiplier;
    }
}

/// <summary>Typed v1 EHP estimate for one successful hit scenario. The pool is the current
/// Life + Energy Shield panel pool; chaos bypass, recovery and other pool rules are not applied.</summary>
public sealed record DefenceEhpEstimate(
    string DamageType,
    decimal RawHit,
    decimal Pool,
    decimal DamageMultiplier,
    decimal? EffectiveHitPool);
