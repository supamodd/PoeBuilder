namespace PoeBuilder.Core.Calculation;

/// <summary>Allocation of one mitigated damage amount between Energy Shield and Life.</summary>
public sealed record ResourceDamageResult(
    decimal IncomingDamage,
    decimal EnergyShieldDamage,
    decimal LifeDamage,
    decimal RemainingDamage,
    bool ChaosBypassesEnergyShield);

public static class ResourceDamageCalculator
{
    /// <summary>Routes mitigated damage through Energy Shield and then Life. Chaos can bypass
    /// Energy Shield unless a mechanic such as Chaos Inoculation explicitly prevents bypass.</summary>
    public static ResourceDamageResult Route(
        decimal damage,
        decimal energyShield,
        decimal life,
        bool chaosDamage = false,
        bool chaosInoculation = false,
        decimal chaosEnergyShieldDamageMultiplier = 2m)
    {
        decimal incoming = Math.Max(0, damage);
        decimal es = Math.Max(0, energyShield);
        decimal maxLife = Math.Max(0, life);
        bool bypass = chaosDamage && !chaosInoculation;
        decimal esDamage = bypass ? 0 : Math.Min(es,
            incoming * (chaosDamage ? Math.Max(0, chaosEnergyShieldDamageMultiplier) : 1m));
        decimal remaining = incoming - (chaosDamage && chaosEnergyShieldDamageMultiplier > 0
            ? esDamage / chaosEnergyShieldDamageMultiplier
            : esDamage);
        decimal lifeDamage = bypass ? Math.Min(maxLife, incoming) : Math.Min(maxLife, Math.Max(0, remaining));
        decimal consumed = bypass ? lifeDamage : (chaosDamage && chaosEnergyShieldDamageMultiplier > 0
            ? esDamage / chaosEnergyShieldDamageMultiplier + lifeDamage
            : esDamage + lifeDamage);
        return new(incoming, esDamage, lifeDamage, Math.Max(0, incoming - consumed), bypass);
    }
}
