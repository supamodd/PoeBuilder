namespace PoeBuilder.Core.Calculation;

public sealed record AilmentResult(
    string Ailment,
    decimal ChancePercent,
    decimal EffectPercent,
    decimal DurationSeconds,
    bool Applied);

public static class AilmentCalculator
{
    /// <summary>Evaluates a threshold ailment from an incoming hit. The caller supplies the
    /// patch-specific threshold and base duration; this keeps game data outside the formula.</summary>
    public static AilmentResult Evaluate(
        string ailment,
        decimal hitDamage,
        decimal ailmentThreshold,
        decimal baseDurationSeconds,
        decimal chancePercent = 100,
        decimal effectCapPercent = 100)
    {
        decimal hit = Math.Max(0, hitDamage);
        decimal threshold = Math.Max(0, ailmentThreshold);
        decimal chance = Math.Clamp(chancePercent, 0, 100);
        decimal ratio = threshold > 0 ? hit / threshold : hit > 0 ? 1 : 0;
        decimal effect = Math.Clamp(ratio * 100, 0, Math.Max(0, effectCapPercent));
        bool applied = hit > 0 && chance > 0 && ratio > 0;
        decimal duration = applied ? Math.Max(0, baseDurationSeconds) * effect / 100m : 0;
        return new(ailment, chance, effect, duration, applied);
    }

    public static decimal DamagePerStack(decimal hitDamage, decimal damagePercent)
        => Math.Max(0, hitDamage) * Math.Max(0, damagePercent) / 100m;
}
