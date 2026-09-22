namespace PoeBuilder.Core.Calculation;

/// <summary>
/// Single-successful-hit effective hit-pool helpers. The successful-hit vector deliberately does
/// not invent hit chance, block, deflection, suppression, recovery, penetration or enemy
/// configuration. Explicit scenario records may compose only the stages supplied by their caller.
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

    /// <summary>Converts the current resource pool into raw incoming-damage units for the
    /// supported v1 damage types. Current PoB2 reference applies double damage to Energy Shield
    /// from chaos by default; this is not chaos bypass and can be changed only by an explicit
    /// future modifier/context.</summary>
    public static decimal ResourcePoolForDamageType(string damageType, decimal life, decimal energyShield,
        decimal chaosEnergyShieldDamageMultiplier = 2m)
    {
        decimal safeLife = Math.Max(0, life);
        decimal safeEnergyShield = Math.Max(0, energyShield);
        if (string.Equals(damageType, "Chaos", StringComparison.OrdinalIgnoreCase))
        {
            if (chaosEnergyShieldDamageMultiplier <= 0) return safeLife;
            return safeLife + safeEnergyShield / chaosEnergyShieldDamageMultiplier;
        }
        return safeLife + safeEnergyShield;
    }

    /// <summary>Expected incoming multiplier for one attempted monster attack. The successful-hit
    /// mitigation is multiplied by monster hit chance, optional attack dodge, block and deflection.
    /// Blocked-hit damage is explicit because special block effects are not silently assumed.</summary>
    public static decimal ExpectedAttackDamageMultiplier(decimal successfulHitMultiplier,
        decimal hitChancePercent, decimal blockChancePercent = 0, decimal deflectionChancePercent = 0,
        decimal deflectionDamagePreventedPercent = DefenceCalculator.DeflectionDamagePreventedPercent,
        decimal blockedHitDamagePercent = 0, decimal attackDodgeChancePercent = 0)
    {
        decimal hit = Math.Clamp(hitChancePercent, 0, 100) / 100m;
        decimal block = Math.Clamp(blockChancePercent, 0, 100) / 100m;
        decimal blockedDamage = Math.Clamp(blockedHitDamagePercent, 0, 100) / 100m;
        decimal deflection = Math.Clamp(deflectionChancePercent, 0, 100) / 100m;
        decimal prevented = Math.Clamp(deflectionDamagePreventedPercent, 0, 100) / 100m;
        decimal dodge = 1 - DefenceCalculator.DodgeChance(attackDodgeChancePercent) / 100m;
        return Math.Max(0, successfulHitMultiplier) * hit * dodge *
            (1 - block * (1 - blockedDamage)) * (1 - deflection * prevented);
    }

    /// <summary>Expected multiplier for a spell hit after average suppression and optional spell
    /// dodge. This is a scenario helper only: it does not invent a spell hit size or source data.</summary>
    public static decimal ExpectedSpellDamageMultiplier(decimal successfulHitMultiplier,
        decimal suppressionChancePercent = 0,
        decimal suppressionEffectPercent = DefenceCalculator.BaseSpellSuppressionEffectPercent,
        decimal spellDodgeChancePercent = 0)
    {
        decimal suppression = DefenceCalculator.SpellSuppressionDamageMultiplier(
            suppressionChancePercent, suppressionEffectPercent);
        decimal dodge = 1 - DefenceCalculator.DodgeChance(spellDodgeChancePercent) / 100m;
        return Math.Max(0, successfulHitMultiplier) * suppression * dodge;
    }

    /// <summary>Builds a typed spell EHP estimate from an explicit spell scenario. The caller
    /// supplies raw hit, pool and successful-hit mitigation; this method only composes the
    /// verified suppression/dodge stages and never invents enemy spell data.</summary>
    public static ExpectedSpellEhpEstimate? SpellEhpEstimate(string damageType, decimal rawHit,
        decimal pool, decimal successfulHitMultiplier, decimal suppressionChancePercent = 0,
        decimal suppressionEffectPercent = DefenceCalculator.BaseSpellSuppressionEffectPercent,
        decimal spellDodgeChancePercent = 0, decimal hitChancePercent = 100,
        decimal spellBlockChancePercent = 0, decimal blockedHitDamagePercent = 0)
    {
        if (string.IsNullOrWhiteSpace(damageType) || rawHit <= 0 || pool < 0)
            return null;
        decimal suppressionChance = DefenceCalculator.SpellSuppressionChance(suppressionChancePercent);
        decimal suppressionEffect = Math.Max(0, suppressionEffectPercent);
        decimal spellDodge = DefenceCalculator.DodgeChance(spellDodgeChancePercent);
        decimal hitChance = Math.Clamp(hitChancePercent, 0, 100);
        decimal spellBlock = Math.Clamp(spellBlockChancePercent, 0, 100);
        decimal blockedDamage = Math.Clamp(blockedHitDamagePercent, 0, 100);
        decimal expectedMultiplier = ExpectedSpellDamageMultiplier(successfulHitMultiplier,
            suppressionChance, suppressionEffect, spellDodge) * hitChance / 100m *
            (1 - spellBlock / 100m * (1 - blockedDamage / 100m));
        return new(damageType, rawHit, pool, suppressionChance, suppressionEffect, spellDodge,
            Math.Max(0, successfulHitMultiplier), expectedMultiplier,
            EffectiveHitPool(pool, expectedMultiplier), hitChance, spellBlock, blockedDamage);
    }

    /// <summary>Builds a typed attack EHP estimate from explicit scenario inputs. No enemy or
    /// player source is inferred from the scenario record.</summary>
    public static ExpectedAttackEhpEstimate? AttackEhpEstimate(AttackEhpScenario scenario)
    {
        if (scenario is null || string.IsNullOrWhiteSpace(scenario.DamageType) || scenario.RawHit <= 0 ||
            scenario.Pool < 0 || scenario.SuccessfulHitMultiplier < 0)
            return null;
        decimal dodge = DefenceCalculator.DodgeChance(scenario.AttackDodgeChancePercent);
        decimal expectedMultiplier = ExpectedAttackDamageMultiplier(scenario.SuccessfulHitMultiplier,
            scenario.HitChancePercent, scenario.BlockChancePercent, scenario.DeflectionChancePercent,
            scenario.DeflectionDamagePreventedPercent, scenario.BlockedHitDamagePercent, dodge);
        return new(scenario.DamageType, scenario.RawHit, scenario.Pool,
            Math.Clamp(scenario.HitChancePercent, 0, 100),
            Math.Clamp(scenario.BlockChancePercent, 0, 100),
            Math.Clamp(scenario.DeflectionChancePercent, 0, 100),
            scenario.SuccessfulHitMultiplier, expectedMultiplier,
            EffectiveHitPool(scenario.Pool, expectedMultiplier), dodge);
    }

    /// <summary>Builds a typed spell EHP estimate from explicit scenario inputs.</summary>
    public static ExpectedSpellEhpEstimate? SpellEhpEstimate(SpellEhpScenario scenario)
    {
        if (scenario is null) return null;
        return SpellEhpEstimate(scenario.DamageType, scenario.RawHit, scenario.Pool,
            scenario.SuccessfulHitMultiplier, scenario.SuppressionChancePercent,
            scenario.SuppressionEffectPercent, scenario.SpellDodgeChancePercent,
            scenario.HitChancePercent, scenario.SpellBlockChancePercent,
            scenario.BlockedHitDamagePercent);
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

/// <summary>Explicit attack scenario inputs. The caller supplies all source-derived values;
/// no default monster or player state is inferred here.</summary>
public sealed record AttackEhpScenario(
    string DamageType,
    decimal RawHit,
    decimal Pool,
    decimal SuccessfulHitMultiplier,
    decimal HitChancePercent,
    decimal BlockChancePercent = 0,
    decimal DeflectionChancePercent = 0,
    decimal DeflectionDamagePreventedPercent = DefenceCalculator.DeflectionDamagePreventedPercent,
    decimal BlockedHitDamagePercent = 0,
    decimal AttackDodgeChancePercent = 0);

/// <summary>Explicit spell scenario inputs.</summary>
public sealed record SpellEhpScenario(
    string DamageType,
    decimal RawHit,
    decimal Pool,
    decimal SuccessfulHitMultiplier,
    decimal SuppressionChancePercent = 0,
    decimal SuppressionEffectPercent = DefenceCalculator.BaseSpellSuppressionEffectPercent,
    decimal SpellDodgeChancePercent = 0,
    decimal HitChancePercent = 100,
    decimal SpellBlockChancePercent = 0,
    decimal BlockedHitDamagePercent = 0);

/// <summary>Expected EHP for the same-level default monster's physical attack attempt. This is
/// separate from the successful-hit vector because it includes hit chance, optional dodge, block
/// and deflection.</summary>
public sealed record ExpectedAttackEhpEstimate(
    string DamageType,
    decimal RawHit,
    decimal Pool,
    decimal HitChancePercent,
    decimal BlockChancePercent,
    decimal DeflectionChancePercent,
    decimal SuccessfulHitMultiplier,
    decimal ExpectedDamageMultiplier,
    decimal? EffectiveHitPool,
    decimal AttackDodgeChancePercent = 0);

/// <summary>Typed spell scenario estimate. It is populated only when an explicit defence plan
/// supplies a spell-hit scenario; the pinned default-monster catalog has no spell-hit scenario.</summary>
public sealed record ExpectedSpellEhpEstimate(
    string DamageType,
    decimal RawHit,
    decimal Pool,
    decimal SuppressionChancePercent,
    decimal SuppressionEffectPercent,
    decimal SpellDodgeChancePercent,
    decimal SuccessfulHitMultiplier,
    decimal ExpectedDamageMultiplier,
    decimal? EffectiveHitPool,
    decimal HitChancePercent = 100,
    decimal SpellBlockChancePercent = 0,
    decimal BlockedHitDamagePercent = 0);

/// <summary>Typed v1 EHP estimate for one successful hit scenario. Pool is the effective
/// raw incoming-damage pool; physical/elemental damage use Life + Energy Shield, while default
/// chaos damage uses Life + half Energy Shield under the current PoB2 reference. Explicit chaos
/// bypass, recovery and other pool rules are not applied.</summary>
public sealed record DefenceEhpEstimate(
    string DamageType,
    decimal RawHit,
    decimal Pool,
    decimal DamageMultiplier,
    decimal? EffectiveHitPool);
