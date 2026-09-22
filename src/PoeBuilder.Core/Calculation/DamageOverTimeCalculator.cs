namespace PoeBuilder.Core.Calculation;

public sealed record DamageOverTimeResult(
    string DamageType,
    decimal DamagePerSecond,
    decimal DurationSeconds,
    decimal TotalDamage,
    decimal DamageMultiplier);

public static class DamageOverTimeCalculator
{
    /// <summary>Evaluates a damage-over-time effect. Resistance applies to the final damage
    /// type; armour, block, evasion and spell suppression are hit-only mechanics here.</summary>
    public static DamageOverTimeResult Evaluate(
        string damageType,
        decimal damagePerSecond,
        decimal durationSeconds,
        IReadOnlyDictionary<string, ResistanceHitResult> resistances)
    {
        decimal baseDps = Math.Max(0, damagePerSecond);
        decimal duration = Math.Max(0, durationSeconds);
        string type = damageType.ToLowerInvariant();
        decimal multiplier = resistances.TryGetValue(type, out var resistance)
            ? resistance.DamageMultiplier : 1m;
        decimal dps = baseDps * multiplier;
        return new(damageType, dps, duration, dps * duration, multiplier);
    }
}
