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
        decimal armourCapPercent = 90m,
        bool armourAppliesToElemental = false)
    {
        decimal physicalMultiplier = EhpCalculator.ArmourDamageMultiplier(
            armour, incoming.Physical, armourRatio, armourCapPercent);
        decimal fireMultiplier = MitigationFor("fire", incoming.Fire);
        decimal coldMultiplier = MitigationFor("cold", incoming.Cold);
        decimal lightningMultiplier = MitigationFor("lightning", incoming.Lightning);
        var mitigated = new DamagePacket(
            incoming.Physical * physicalMultiplier,
            incoming.Fire * fireMultiplier,
            incoming.Cold * coldMultiplier,
            incoming.Lightning * lightningMultiplier,
            Mitigated(incoming.Chaos, "chaos"));
        decimal multiplier = incoming.Total <= 0 ? 0 : mitigated.Total / incoming.Total;
        return new(incoming, mitigated, multiplier, EhpCalculator.EffectiveHitPool(pool, multiplier));

        decimal Mitigated(decimal amount, string type)
            => amount * (resistances.TryGetValue(type, out var result) ? result.DamageMultiplier : 1m);

        decimal MitigationFor(string type, decimal amount)
        {
            decimal resistanceMultiplier = resistances.TryGetValue(type, out var result)
                ? result.DamageMultiplier : 1m;
            if (!armourAppliesToElemental || amount <= 0) return resistanceMultiplier;
            return resistanceMultiplier * EhpCalculator.ArmourDamageMultiplier(
                armour, amount, armourRatio, armourCapPercent);
        }
    }
}
