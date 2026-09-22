namespace PoeBuilder.Core.Calculation;

public sealed record CurseResult(
    decimal BaseResistanceReduction,
    decimal EffectPercent,
    decimal AppliedReduction,
    decimal FinalResistance);

public static class CurseCalculator
{
    /// <summary>Applies curse effect to a resistance reduction supplied by the caller. Curse
    /// immunity and ailment-specific conditions remain explicit scenario inputs.</summary>
    public static CurseResult ApplyResistanceReduction(
        decimal resistance,
        decimal baseReduction,
        decimal curseEffectPercent = 0,
        bool immune = false)
    {
        decimal effect = Math.Max(0, curseEffectPercent);
        decimal reduction = immune ? 0 : Math.Max(0, baseReduction) * (1 + effect / 100m);
        return new(baseReduction, effect, reduction, resistance - reduction);
    }

    public static decimal ApplyConditionalDamageTaken(
        decimal damage,
        decimal increasedPercent = 0,
        decimal reducedPercent = 0,
        bool conditionActive = true)
    {
        if (!conditionActive) return Math.Max(0, damage);
        return Math.Max(0, damage) * (1 + increasedPercent / 100m) * Math.Max(0, 1 - reducedPercent / 100m);
    }
}
