namespace PoeBuilder.Core.Calculation;

public sealed record CombatScenario(
    DamagePacket Incoming,
    decimal Armour,
    IReadOnlyDictionary<string, ResistanceHitResult> Resistances,
    decimal Life,
    decimal EnergyShield,
    bool ChaosInoculation = false,
    bool IsSpell = false,
    decimal HitChancePercent = 100,
    decimal Entropy = 0,
    bool Lucky = false,
    bool Unlucky = false,
    decimal BlockChancePercent = 0,
    decimal SecondsSinceBlock = decimal.MaxValue,
    decimal BlockRecoverySeconds = 0,
    decimal SuppressionChancePercent = 0,
    decimal SuppressionEffectPercent = DefenceCalculator.BaseSpellSuppressionEffectPercent,
    decimal RecoveryPerSecond = 0,
    decimal RecoupPercent = 0,
    decimal LeechPercent = 0,
    bool ArmourAppliesToElemental = false,
    decimal ConditionalDamageTakenIncreasePercent = 0,
    decimal ConditionalDamageTakenReductionPercent = 0,
    bool ConditionalDamageActive = true);

public sealed record CombatScenarioResult(
    bool Hit,
    decimal FinalEntropy,
    decimal ExpectedDamageMultiplier,
    DamagePacket AfterMitigation,
    decimal EnergyShieldDamage,
    decimal LifeDamage,
    decimal RecoupPerSecond,
    decimal LeechPerSecond,
    decimal RecoveryPerSecond,
    decimal EffectiveHitPool);

public static class CombatScenarioCalculator
{
    /// <summary>Evaluates a deterministic combat scenario in order: hit/entropy, mitigation,
    /// spell suppression or attack block, resource routing, then recovery sources. Recoup and
    /// leech are explicit scenario inputs because their game conditions are not inferable from
    /// a bare damage packet.</summary>
    public static CombatScenarioResult Evaluate(CombatScenario scenario)
    {
        decimal hitChance = EvasionCalculator.ApplyLuck(scenario.HitChancePercent,
            scenario.Lucky, scenario.Unlucky);
        var hitRoll = EvasionCalculator.ResolveAttack(scenario.Entropy, hitChance);
        if (!hitRoll.Hit)
            return Empty(hitRoll, scenario);

        var routed = DamageRoutingCalculator.ApplyTakenAs(scenario.Incoming, new Dictionary<(string, string), decimal>());
        var mitigation = MitigationCalculator.Evaluate(routed, scenario.Armour, scenario.Resistances,
            scenario.Life + scenario.EnergyShield, armourAppliesToElemental: scenario.ArmourAppliesToElemental);
        decimal multiplier = mitigation.DamageMultiplier;
        if (scenario.IsSpell)
            multiplier *= DefenceCalculator.SpellSuppressionDamageMultiplier(
                scenario.SuppressionChancePercent, scenario.SuppressionEffectPercent);
        else if (BlockCalculator.RecoveryReady(scenario.SecondsSinceBlock, scenario.BlockRecoverySeconds))
            multiplier *= 1 - BlockCalculator.Chance(scenario.BlockChancePercent) / 100m;

        decimal incomingAfterAvoidance = mitigation.AfterMitigation.Total * Math.Max(0, multiplier / Math.Max(mitigation.DamageMultiplier, 0.0000001m));
        incomingAfterAvoidance = CurseCalculator.ApplyConditionalDamageTaken(incomingAfterAvoidance,
            scenario.ConditionalDamageTakenIncreasePercent,
            scenario.ConditionalDamageTakenReductionPercent,
            scenario.ConditionalDamageActive);
        var resource = ResourceDamageCalculator.Route(incomingAfterAvoidance, scenario.EnergyShield, scenario.Life,
            chaosDamage: scenario.Incoming.Chaos > 0 && scenario.Incoming.Total == scenario.Incoming.Chaos,
            chaosInoculation: scenario.ChaosInoculation);
        decimal pool = scenario.Life + scenario.EnergyShield;
        decimal expected = pool <= 0 ? 0 : incomingAfterAvoidance / pool;
        decimal recoup = incomingAfterAvoidance * Math.Max(0, scenario.RecoupPercent) / 100m;
        decimal leech = mitigation.AfterMitigation.Total * Math.Max(0, scenario.LeechPercent) / 100m;
        return new(true, hitRoll.EntropyAfter, expected, mitigation.AfterMitigation,
            resource.EnergyShieldDamage, resource.LifeDamage, recoup, leech,
            Math.Max(0, scenario.RecoveryPerSecond), pool / Math.Max(expected, 0.0000001m));
    }

    private static CombatScenarioResult Empty(EvasionRollResult roll, CombatScenario scenario)
        => new(false, roll.EntropyAfter, 0, new DamagePacket(0, 0, 0, 0, 0), 0, 0, 0, 0,
            Math.Max(0, scenario.RecoveryPerSecond), decimal.MaxValue);
}
