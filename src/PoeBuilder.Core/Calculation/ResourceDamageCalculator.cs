namespace PoeBuilder.Core.Calculation;

/// <summary>Allocation of one mitigated damage amount between Energy Shield and Life.</summary>
public sealed record ResourceDamageResult(
    decimal IncomingDamage,
    decimal ManaDamage,
    decimal EnergyShieldDamage,
    decimal LifeDamage,
    decimal WardDamage,
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
        decimal mana = 0,
        decimal ward = 0,
        decimal damageTakenFromManaPercent = 0,
        bool chaosDamage = false,
        bool chaosInoculation = false,
        decimal chaosEnergyShieldDamageMultiplier = 2m)
    {
        decimal incoming = Math.Max(0, damage);
        decimal es = Math.Max(0, energyShield);
        decimal maxLife = Math.Max(0, life);
        decimal maxMana = Math.Max(0, mana);
        decimal maxWard = Math.Max(0, ward);
        bool bypass = chaosDamage && !chaosInoculation;
        decimal manaDamage = Math.Min(maxMana, incoming * Math.Clamp(damageTakenFromManaPercent, 0, 100) / 100m);
        decimal afterMana = incoming - manaDamage;
        decimal esDamage = bypass ? 0 : Math.Min(es,
            afterMana * (chaosDamage ? Math.Max(0, chaosEnergyShieldDamageMultiplier) : 1m));
        decimal remaining = afterMana - (chaosDamage && chaosEnergyShieldDamageMultiplier > 0
            ? esDamage / chaosEnergyShieldDamageMultiplier
            : esDamage);
        decimal wardDamage = Math.Min(maxWard, Math.Max(0, remaining));
        decimal lifeDamage = bypass ? Math.Min(maxLife, Math.Max(0, afterMana)) : Math.Min(maxLife, Math.Max(0, remaining - wardDamage));
        decimal consumed = manaDamage + wardDamage + (bypass ? lifeDamage : (chaosDamage && chaosEnergyShieldDamageMultiplier > 0
            ? esDamage / chaosEnergyShieldDamageMultiplier + lifeDamage
            : esDamage + lifeDamage));
        return new(incoming, manaDamage, esDamage, lifeDamage, wardDamage, Math.Max(0, incoming - consumed), bypass);
    }
}
