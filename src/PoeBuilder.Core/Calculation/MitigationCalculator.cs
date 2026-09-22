namespace PoeBuilder.Core.Calculation;

/// <summary>Damage after mitigation, retaining the contribution of each damage type.</summary>
public sealed record MitigatedDamageResult(
    DamagePacket Incoming,
    DamagePacket AfterMitigation,
    decimal DamageMultiplier,
    decimal? EffectiveHitPool);

public static class MitigationCalculator
{
    /// <summary>Applies type-specific mitigation after damage routing. Armour uses the routed
    /// physical hit size; resistance results already contain reduction and penetration stages.</summary>
    public static MitigatedDamageResult Evaluate(
        DamagePacket incoming,
        decimal armour,
        IReadOnlyDictionary<string, ResistanceHitResult> resistances,
        decimal pool,
        decimal armourRatio = 12m,
        decimal armourCapPercent = 90m)
    {
        decimal physicalMultiplier = EhpCalculator.ArmourDamageMultiplier(
            armour, incoming.Physical, armourRatio, armourCapPercent);
        var mitigated = new DamagePacket(
            incoming.Physical * physicalMultiplier,
            Mitigated(incoming.Fire, "fire"),
            Mitigated(incoming.Cold, "cold"),
            Mitigated(incoming.Lightning, "lightning"),
            Mitigated(incoming.Chaos, "chaos"));
        decimal multiplier = incoming.Total <= 0 ? 0 : mitigated.Total / incoming.Total;
        return new(incoming, mitigated, multiplier, EhpCalculator.EffectiveHitPool(pool, multiplier));

        decimal Mitigated(decimal amount, string type)
            => amount * (resistances.TryGetValue(type, out var result) ? result.DamageMultiplier : 1m);
    }
}
