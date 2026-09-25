using System.Text.Json;
using PoeBuilder.Core.Calculation;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Skills;
using PoeBuilder.Core.Tree;

internal static class CalculationTests
{
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }

    private static readonly Lazy<TreeCatalog> Tree = new(() => TreeCatalog.LoadPinned(Path.Combine(AppContext.BaseDirectory, "Data", "Tree", "data.json")));
    private static readonly Lazy<GameCatalog> Catalog = new(() => GameCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "catalog.json")));
    private static readonly Lazy<GameStatMap> StatMap = new(() => GameStatMap.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "statmap.json")));

    private static SkillGroup GemGroup(string gemId, string name, bool enabled = true, int weaponSet = 0, int level = 1, GemSelection[]? supports = null, int quality = 0)
    {
        var gem = Catalog.Value.Gems[gemId];
        return new() { Name = name, Enabled = enabled, WeaponSet = weaponSet, Active = new() { GemId = gemId, Level = gem.Levels.Contains(level) ? level : gem.Levels[0], Quality = Math.Clamp(quality, 0, 20) }, Supports = supports ?? [] };
    }

    private static decimal Round1(decimal v) => decimal.Round(v, 1, MidpointRounding.AwayFromZero);
    private static decimal Round2(decimal v) => decimal.Round(v, 2, MidpointRounding.AwayFromZero);

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Calc: reference mechanics baseline fixture is reproducible", () => Task.Run(() =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "calculation-baselines.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert(document.RootElement.GetProperty("version").GetInt32() == 1, "baseline version");
            Assert(document.RootElement.GetProperty("source").GetString() == "PoB2 reference mechanics", "baseline source");

            foreach (var scenario in document.RootElement.GetProperty("scenarios").EnumerateArray())
            {
                string name = scenario.GetProperty("name").GetString() ?? "unnamed";
                string kind = scenario.GetProperty("kind").GetString() ?? "";
                decimal expected;
                decimal actual;
                switch (kind)
                {
                    case "armour":
                        actual = EhpCalculator.ArmourDamageMultiplier(
                            scenario.GetProperty("armour").GetDecimal(),
                            scenario.GetProperty("rawHit").GetDecimal(),
                            scenario.GetProperty("armourRatio").GetDecimal(),
                            scenario.GetProperty("reductionCapPercent").GetDecimal());
                        expected = scenario.GetProperty("expectedDamageMultiplier").GetDecimal();
                        break;
                    case "resistance":
                        actual = EhpCalculator.ResistanceDamageMultiplier(scenario.GetProperty("resistancePercent").GetDecimal());
                        expected = scenario.GetProperty("expectedDamageMultiplier").GetDecimal();
                        break;
                    case "resource_pool":
                        actual = EhpCalculator.ResourcePoolForDamageType(
                            scenario.GetProperty("damageType").GetString() ?? "",
                            scenario.GetProperty("life").GetDecimal(),
                            scenario.GetProperty("energyShield").GetDecimal(),
                            scenario.GetProperty("chaosEnergyShieldDamageMultiplier").GetDecimal());
                        expected = scenario.GetProperty("expectedPool").GetDecimal();
                        break;
                    case "spell_suppression":
                        actual = DefenceCalculator.SpellSuppressionDamageMultiplier(
                            scenario.GetProperty("suppressionChancePercent").GetDecimal(),
                            scenario.GetProperty("suppressionEffectPercent").GetDecimal());
                        expected = scenario.GetProperty("expectedDamageMultiplier").GetDecimal();
                        break;
                    default:
                        throw new Exception("Unknown baseline kind: " + kind);
                }

                Assert(Math.Abs(actual - expected) <= 0.0000001m,
                    $"baseline {name}: actual {actual}, expected {expected}");
            }
        }));

        await test("Defence: resistance hit stages keep panel value separate from reduction and penetration", () => Task.Run(() =>
        {
            var panel = ResistanceCalculator.Calculate(-40, 115, 10);
            var hit = ResistanceCalculator.ForHit(panel, reduction: 10, penetration: 20);
            Assert(panel.Effective == 75 && hit.DisplayedResistance == 75, "panel resistance");
            Assert(hit.AfterReduction == 65, "resistance after reduction");
            Assert(hit.AfterPenetration == 45, "resistance after penetration");
            Assert(hit.DamageMultiplier == 0.55m, "resistance hit multiplier");

            var negative = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, -20, 0), penetration: 10);
            Assert(negative.AfterPenetration == -30 && negative.DamageMultiplier == 1.3m,
                "negative effective resistance");
        }));

        await test("Defence: damage taken as routes preserve total damage and cap source transfers", () => Task.Run(() =>
        {
            var routes = new Dictionary<(string Source, string Destination), decimal>
            {
                [("physical", "fire")] = 50,
                [("fire", "cold")] = 100
            };
            var routed = DamageRoutingCalculator.ApplyTakenAs(
                new DamagePacket(100, 40, 0, 0, 0), routes);
            Assert(routed.Physical == 50 && routed.Fire == 0 && routed.Cold == 90 && routed.Total == 140,
                "damage routing " + routed);

            var capped = DamageRoutingCalculator.ApplyTakenAs(
                new DamagePacket(100, 0, 0, 0, 0),
                new Dictionary<(string Source, string Destination), decimal>
                {
                    [("physical", "fire")] = 80,
                    [("physical", "cold")] = 80
                });
            Assert(capped.Total == 100 && capped.Physical == 0 && capped.Fire == 20 && capped.Cold == 80,
                "source transfer cap " + capped);
        }));

        await test("Defence: damage taken as stat IDs enter the routing bucket", () => Task.Run(() =>
        {
            var bucket = new StatBucket();
            StatInterpreter.Apply(bucket, "physical_damage_taken_%_as_lightning", 30, null);
            StatInterpreter.Apply(bucket, "elemental_damage_taken_%_as_chaos", 20, null);
            Assert(bucket.DamageTakenAs[("physical", "lightning")] == 30, "physical taken as lightning");
            Assert(bucket.DamageTakenAs[("fire", "chaos")] == 20 &&
                   bucket.DamageTakenAs[("cold", "chaos")] == 20 &&
                   bucket.DamageTakenAs[("lightning", "chaos")] == 20,
                "elemental taken as chaos");
        }));

        await test("Defence: mixed routed damage uses armour and resistance per final type", () => Task.Run(() =>
        {
            var resistances = new Dictionary<string, ResistanceHitResult>
            {
                ["fire"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 75, 0)),
                ["cold"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 0, 0)),
                ["lightning"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 0, 0)),
                ["chaos"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 0, 0))
            };
            var result = MitigationCalculator.Evaluate(
                new DamagePacket(100, 100, 0, 0, 0), 1000, resistances, 1000);
            decimal expectedPhysical = 100 * EhpCalculator.ArmourDamageMultiplier(1000, 100);
            Assert(Math.Abs(result.AfterMitigation.Physical - expectedPhysical) <= 0.0000001m,
                "physical mitigation " + result.AfterMitigation.Physical);
            Assert(result.AfterMitigation.Fire == 25, "fire mitigation " + result.AfterMitigation.Fire);
            Assert(Math.Abs(result.AfterMitigation.Total - (expectedPhysical + 25)) <= 0.0000001m,
                "mixed mitigation " + result.AfterMitigation.Total);
            Assert(Math.Abs(result.DamageMultiplier - result.AfterMitigation.Total / 200) <= 0.0000001m,
                "mixed multiplier " + result.DamageMultiplier);
            Assert(result.EffectiveHitPool is decimal ehp &&
                   Math.Abs(ehp - 1000 / result.DamageMultiplier) <= 0.0000001m,
                "mixed EHP " + result.EffectiveHitPool);
        }));

        await test("Defence: resource routing handles ES, chaos bypass and Chaos Inoculation", () => Task.Run(() =>
        {
            var ordinary = ResourceDamageCalculator.Route(150, 100, 200);
            Assert(ordinary.EnergyShieldDamage == 100 && ordinary.LifeDamage == 50 && ordinary.RemainingDamage == 0,
                "ordinary resource routing " + ordinary);

            var chaos = ResourceDamageCalculator.Route(150, 100, 200, chaosDamage: true);
            Assert(chaos.ChaosBypassesEnergyShield && chaos.EnergyShieldDamage == 0 && chaos.LifeDamage == 150,
                "chaos bypass " + chaos);

            var ci = ResourceDamageCalculator.Route(150, 100, 1, chaosDamage: true, chaosInoculation: true);
            Assert(!ci.ChaosBypassesEnergyShield && ci.EnergyShieldDamage == 100 && ci.LifeDamage == 1,
                "CI chaos routing " + ci);

            var manaFirst = ResourceDamageCalculator.Route(100, 0, 100, mana: 100,
                damageTakenFromManaPercent: 40);
            Assert(manaFirst.ManaDamage == 40 && manaFirst.LifeDamage == 60, "mana before life " + manaFirst);

            var bucket = new StatBucket();
            StatInterpreter.Apply(bucket, "keystone_chaos_inoculation", 1, null);
            Assert(bucket.ChaosInoculation, "CI stat mapping");
            StatInterpreter.Apply(bucket, "energy_shield_to_mana", 100, null);
            StatInterpreter.Apply(bucket, "damage_removed_from_mana_before_life_%", 40, null);
            Assert(bucket.EnergyShieldToManaPercent == 100 && bucket.DamageTakenFromManaPercent == 40,
                "mana conversion stat mapping");
        }));

        await test("Defence: Ward and ES-to-Mana remain separate resources", () => Task.Run(() =>
        {
            var bucket = new StatBucket();
            StatInterpreter.Apply(bucket, "base_maximum_energy_shield", 378, null);
            StatInterpreter.Apply(bucket, "base_maximum_ward", 209, null);
            StatInterpreter.Apply(bucket, "energy_shield_to_mana", 100, null);
            decimal manaFromEs = bucket.EsFlat * bucket.EnergyShieldToManaPercent / 100m;
            Assert(bucket.EsFlat == 378 && bucket.WardFlat == 209 && manaFromEs == 378,
                $"resource separation es={bucket.EsFlat}, ward={bucket.WardFlat}, manaFromEs={manaFromEs}");
        }));

        await test("Defence: armour can apply to elemental damage when the stat is present", () => Task.Run(() =>
        {
            var resistances = new Dictionary<string, ResistanceHitResult>
            {
                ["fire"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 0, 0)),
                ["cold"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 0, 0)),
                ["lightning"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 0, 0)),
                ["chaos"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 0, 0))
            };
            var withoutArmour = MitigationCalculator.Evaluate(
                new DamagePacket(0, 100, 0, 0, 0), 1000, resistances, 1000);
            var withArmour = MitigationCalculator.Evaluate(
                new DamagePacket(0, 100, 0, 0, 0), 1000, resistances, 1000,
                armourAppliesToElemental: true);
            Assert(withoutArmour.AfterMitigation.Fire == 100, "elemental without armour");
            Assert(withArmour.AfterMitigation.Fire == 100 * EhpCalculator.ArmourDamageMultiplier(1000, 100),
                "elemental with armour " + withArmour.AfterMitigation.Fire);

            var bucket = new StatBucket();
            StatInterpreter.Apply(bucket, "armour_%_applies_to_fire_cold_lightning_damage", 1, null);
            Assert(bucket.ArmourAppliesToElemental, "armour elemental stat mapping");
        }));

        await test("Defence: damage over time uses resistance but ignores hit-only mitigation", () => Task.Run(() =>
        {
            var resistances = new Dictionary<string, ResistanceHitResult>
            {
                ["fire"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 75, 0))
            };
            var ignite = DamageOverTimeCalculator.Evaluate("Fire", 100, 4, resistances);
            Assert(ignite.DamagePerSecond == 25 && ignite.TotalDamage == 100,
                "fire DoT resistance " + ignite);

            var unknown = DamageOverTimeCalculator.Evaluate("Chaos", 100, 2, resistances);
            Assert(unknown.DamagePerSecond == 100 && unknown.TotalDamage == 200,
                "unmapped DoT resistance " + unknown);
        }));

        await test("Defence: evasion entropy resolves deterministic attack sequences", () => Task.Run(() =>
        {
            var first = EvasionCalculator.ResolveAttack(0, 40);
            var second = EvasionCalculator.ResolveAttack(first.EntropyAfter, 40);
            var third = EvasionCalculator.ResolveAttack(second.EntropyAfter, 40);
            Assert(!first.Hit && !second.Hit && third.Hit, "entropy hit sequence");
            Assert(third.EntropyBefore == 80 && third.EntropyAfter == 20, "entropy rollover");
            Assert(EvasionCalculator.ResolveAttack(99, 1).Hit, "boundary hit");
            var sequence = EvasionCalculator.ResolveSequence(0, 40, 10);
            Assert(sequence.Attacks == 10 && sequence.Hits == 4 && sequence.FinalEntropy == 0,
                "entropy sequence summary");
        }));

        await test("Defence: lucky and unlucky avoidance use two-roll probabilities", () => Task.Run(() =>
        {
            Assert(EvasionCalculator.ApplyLuck(50, lucky: true) == 75, "lucky 50");
            Assert(EvasionCalculator.ApplyLuck(50, unlucky: true) == 25, "unlucky 50");
            Assert(EvasionCalculator.ApplyLuck(40) == 40, "ordinary chance");
            Assert(EvasionCalculator.ApplyLuck(40, lucky: true, unlucky: true) == 40, "conflicting luck");
            Assert(DefenceCalculator.PlayerHitChance(100, 100, lucky: true) == 100, "lucky hit chance");
            Assert(DefenceCalculator.PlayerHitChance(100, 100, unlucky: true) == 92, "unlucky hit chance");
        }));

        await test("Defence: block luck, blocked damage and recovery are explicit", () => Task.Run(() =>
        {
            Assert(BlockCalculator.Chance(50, lucky: true) == 75, "lucky block");
            Assert(BlockCalculator.Chance(50, unlucky: true) == 25, "unlucky block");
            Assert(BlockCalculator.Chance(100, 90) == 90, "block cap");
            Assert(BlockCalculator.BlockedDamage(100) == 0, "full block");
            Assert(BlockCalculator.BlockedDamage(100, 40) == 40, "partial block");
            Assert(!BlockCalculator.RecoveryReady(0.9m, 1), "block recovery active");
            Assert(BlockCalculator.RecoveryReady(1, 1), "block recovery complete");
        }));

        await test("Defence: ailment thresholds produce bounded effects and durations", () => Task.Run(() =>
        {
            var freeze = AilmentCalculator.Evaluate("Freeze", 50, 100, 4, effectCapPercent: 100);
            Assert(freeze.Applied && freeze.EffectPercent == 50 && freeze.DurationSeconds == 2,
                "freeze threshold " + freeze);
            var capped = AilmentCalculator.Evaluate("Shock", 300, 100, 4, effectCapPercent: 50);
            Assert(capped.EffectPercent == 50 && capped.DurationSeconds == 2, "shock cap " + capped);
            var immune = AilmentCalculator.Evaluate("Ignite", 100, 100, 4, chancePercent: 0);
            Assert(!immune.Applied && immune.DurationSeconds == 0, "ailment avoidance");
            Assert(AilmentCalculator.DamagePerStack(200, 10) == 20, "DoT stack damage");
        }));

        await test("Defence: combat scenario applies entropy, mitigation, block and resource routing in order", () => Task.Run(() =>
        {
            var resistances = new Dictionary<string, ResistanceHitResult>
            {
                ["fire"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 75, 0)),
                ["cold"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 0, 0)),
                ["lightning"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 0, 0)),
                ["chaos"] = ResistanceCalculator.ForHit(ResistanceCalculator.Calculate(0, 0, 0))
            };
            var result = CombatScenarioCalculator.Evaluate(new CombatScenario(
                new DamagePacket(100, 100, 0, 0, 0), 1000, resistances, 1000, 500,
                HitChancePercent: 100, BlockChancePercent: 50, RecoupPercent: 10, LeechPercent: 20));
            decimal physical = 100 * EhpCalculator.ArmourDamageMultiplier(1000, 100);
            decimal mitigated = physical + 25;
            Assert(result.Hit && result.EnergyShieldDamage == mitigated * 0.5m,
                "combat ES routing " + result);
                 decimal afterBlock = mitigated * 0.5m;
                 Assert(result.LifeDamage == 0 && result.RecoupPerSecond == afterBlock * 0.1m &&
                   result.LeechPerSecond == mitigated * 0.2m, "combat recovery sources " + result);

            var missed = CombatScenarioCalculator.Evaluate(new CombatScenario(
                new DamagePacket(100, 0, 0, 0, 0), 0, resistances, 1000, 0, HitChancePercent: 40));
            Assert(!missed.Hit && missed.EnergyShieldDamage == 0, "entropy miss");
        }));

        await test("Defence: curses and conditional damage taken are explicit combat stages", () => Task.Run(() =>
        {
            var curse = CurseCalculator.ApplyResistanceReduction(75, 20, 50);
            Assert(curse.AppliedReduction == 30 && curse.FinalResistance == 45, "curse reduction");
            Assert(CurseCalculator.ApplyResistanceReduction(75, 20, 50, immune: true).FinalResistance == 75,
                "curse immunity");
            Assert(CurseCalculator.ApplyConditionalDamageTaken(100, 20, 10) == 108, "conditional damage");
            Assert(CurseCalculator.ApplyConditionalDamageTaken(100, 20, 10, false) == 100, "inactive condition");
        }));

        await test("Defence: recovery window delays ES recharge and caps all sources", () => Task.Run(() =>
        {
            var delayed = RecoveryCalculator.AfterHit(1000, 500, 1000, 200, 3,
                recoupPerSecond: 50, leechPerSecond: 50, regenerationPerSecond: 50,
                esRechargePerSecond: 100, esRechargeDelaySeconds: 4);
            Assert(delayed.LifeAfter == 950 && delayed.EnergyShieldAfter == 200,
                "recovery delay " + delayed);
            var active = RecoveryCalculator.AfterHit(1000, 950, 1000, 950, 6,
                recoupPerSecond: 100, leechPerSecond: 100, regenerationPerSecond: 100,
                esRechargePerSecond: 100, esRechargeDelaySeconds: 4);
            Assert(active.LifeAfter == 1000 && active.EnergyShieldAfter == 1000,
                "recovery caps " + active);
        }));

        await test("Calc: v1 pools follow the pinned per-level and attribute formulas", () => Task.Run(() =>
        {
            var cls = Tree.Value.Classes[0]; // first class that ships a start node + ascendancies
            var build = BuildDocument.Create("Formulas") with { Level = 70, CharacterClass = cls.Name, Tree = new() { ClassIndex = cls.Index } };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            Assert(s.ClassName == cls.Name, "class name " + s.ClassName);
            Assert(s.Life == (16 + 12m * 69 + 2m * cls.BaseStrength), "life " + s.Life);
            Assert(s.Mana == (30 + 4m * 69 + 2m * cls.BaseIntelligence), "mana " + s.Mana);
            Assert(s.Accuracy == (6m * 69 + 5m * cls.BaseDexterity), "accuracy " + s.Accuracy);
            Assert(s.Evasion == 3m * 69, "evasion " + s.Evasion);
            Assert(s.MoveSpeedPercent == 100, "move " + s.MoveSpeedPercent);
            Assert(s.FireRes == 0 && s.ChaosRes == 0, "res " + s.FireRes);
            // No data sources at all: constants still apply, and data flags stay honest.
            var bare = CharacterCalculator.Calculate(BuildDocument.Create("Bare") with { Level = 70 }, null, null, null);
            Assert(bare.Life == 16 + 12m * 69, "bare life " + bare.Life);
            Assert(!bare.HasTreeData && !bare.HasGameData && !bare.HasStatMap);
        }));

        await test("Calc: ascendancy passive stats enter the character summary", () => Task.Run(() =>
        {
            var definition = Tree.Value.Ascendancies.First(a => a.Graph.Nodes.Values.Any(n =>
                n.IsSupported && n.Stats.Any(line => StatMap.Value.Lines.TryGetValue(line, out var stats) &&
                    stats.ContainsKey("base_fire_damage_resistance_%"))));
            var baseTree = new PassiveTreePlan { DatasetId = Tree.Value.DatasetId, ClassIndex = definition.ClassIndex };
            var selected = AscendancyRules.Select(Tree.Value, baseTree, definition.Id);
            var graphPlan = definition.ToGraphPlan(selected.Ascendancy!);
            var graphEngine = new PassiveTreeEngine(definition.Graph);
            PassiveTreePlan? allocatedGraph = null;
            foreach (var target in definition.Graph.Nodes.Values.Where(n => n.IsSupported && n.Stats.Any(line =>
                         StatMap.Value.Lines.TryGetValue(line, out var stats) && stats.ContainsKey("base_fire_damage_resistance_%"))))
            {
                try { allocatedGraph = graphEngine.Allocate(graphPlan, target.Id, 26297); break; }
                catch (TreeRuleException) { }
            }
            Assert(allocatedGraph is not null, definition.Id + " has no reachable fire-resistance node");
            var withAscendancy = AscendancyRules.Update(Tree.Value, selected, allocatedGraph!);
            var plain = CharacterCalculator.Calculate(BuildDocument.Create("Plain") with { Tree = baseTree }, Tree.Value, StatMap.Value, Catalog.Value);
            var applied = CharacterCalculator.Calculate(BuildDocument.Create("Ascendancy") with { Tree = withAscendancy }, Tree.Value, StatMap.Value, Catalog.Value);

            decimal expectedSources = 0;
            foreach (int id in allocatedGraph!.AllocatedNodes.Append(graphEngine.Start(graphPlan)).Distinct())
                foreach (var line in definition.Graph.Describe(id, graphPlan).Stats)
                    if (StatMap.Value.Lines.TryGetValue(line, out var stats) && stats.TryGetValue("base_fire_damage_resistance_%", out var value))
                        expectedSources += value;

            Assert(expectedSources > 0, "fixture must contain a positive ascendancy resistance source");
            Assert(applied.FireResSources - plain.FireResSources == expectedSources,
                $"ascendancy sources {applied.FireResSources} expected delta {expectedSources}");
            Assert(applied.FireRes == Math.Min(plain.FireRes + expectedSources, CharacterCalculator.ResistanceCap),
                $"ascendancy effective {applied.FireRes} expected {plain.FireRes + expectedSources}");
        }));

        await test("Calc: one level adds exactly +12 life, +4 mana, +6 accuracy, +3 evasion", () => Task.Run(() =>
        {
            CharacterSummary At(int level) => CharacterCalculator.Calculate(BuildDocument.Create("L") with { Level = level, Tree = new() { ClassIndex = 0 } }, Tree.Value, StatMap.Value, Catalog.Value);
            var a = At(70); var b = At(71);
            Assert(b.Life - a.Life == 12, "life step " + (b.Life - a.Life));
            Assert(b.Mana - a.Mana == 4, "mana step");
            Assert(b.Accuracy - a.Accuracy == 6, "accuracy step");
            Assert(b.Evasion - a.Evasion == 3, "evasion step");
        }));

        await test("Defence: EHP helpers preserve damage-type multipliers and hit-size dependence", () => Task.Run(() =>
        {
            Assert(EhpCalculator.ResistanceDamageMultiplier(75) == 0.25m, "75% resistance multiplier");
            Assert(EhpCalculator.ResistanceDamageMultiplier(-40) == 1.4m, "negative resistance multiplier");
            Assert(EhpCalculator.ArmourDamageMultiplier(0, 500) == 1, "zero armour");
            decimal armourMultiplier = EhpCalculator.ArmourDamageMultiplier(1000, 500, 12, 90);
            Assert(Round2(armourMultiplier) == 0.86m, "armour multiplier " + armourMultiplier);
            Assert(EhpCalculator.ArmourDamageMultiplier(1_000_000, 1, 12, 90) == 0.1m, "armour reduction cap");
            Assert(Round2(EhpCalculator.EffectiveHitPool(1000, 0.25m)!.Value) == 4000m, "resistance EHP");
            Assert(Round2(EhpCalculator.EffectiveHitPool(1000, armourMultiplier)!.Value) == 1166.67m, "armour EHP");
            Assert(Round2(EhpCalculator.EffectiveHitPool(1000, EhpCalculator.ResistanceDamageMultiplier(-40))!.Value) == 714.29m,
                "negative resistance lowers EHP");
            Assert(EhpCalculator.ResourcePoolForDamageType("Physical", 1000, 500) == 1500, "physical resource pool");
            Assert(EhpCalculator.ResourcePoolForDamageType("Chaos", 1000, 500) == 1250, "chaos double ES damage pool");
            Assert(EhpCalculator.ResourcePoolForDamageType("Chaos", 1, 500, chaosBypassesEnergyShield: false) == 501,
                "Chaos Inoculation chaos pool");
            Assert(EhpCalculator.ResourcePoolForDamageType("Fire", 1000, 0,
                mana: 4576, damageTakenFromManaPercent: 40) == 2830.4m,
                "mana-before-life EHP pool");
            Assert(EhpCalculator.EffectiveHitPool(1000, 0) is null, "zero damage multiplier is unbounded");
            Assert(DefenceCalculator.DeflectionChance(0, 100) == 0, "zero deflection chance");
            Assert(DefenceCalculator.DeflectionChance(1000, 100) == 82, "deflection chance formula");
            Assert(DefenceCalculator.DeflectionChance(1_000_000, 1) == DefenceCalculator.DeflectionChanceCap, "deflection chance cap");
            Assert(DefenceCalculator.DodgeChance(50) == 50, "dodge chance");
            Assert(DefenceCalculator.DodgeChance(100) == DefenceCalculator.DodgeChanceCap, "dodge chance cap");
            Assert(DefenceCalculator.DodgeChance(-1) == 0, "negative dodge chance");
            Assert(DefenceCalculator.BlockChanceMaximum() == 50, "base block maximum");
            Assert(DefenceCalculator.BlockChanceMaximum(25) == 75, "additional block maximum");
            Assert(DefenceCalculator.BlockChanceMaximum(50) == 90, "global block cap");
            Assert(DefenceCalculator.BlockChanceMaximum(0, 75) == 75, "block maximum override");
            Assert(DefenceCalculator.BlockChance(100) == 50, "block chance default cap");
            Assert(DefenceCalculator.BlockChance(40, 50, maximumBlockIncrease: 25) == 60, "block increased chance");
            Assert(DefenceCalculator.SpellBlockChance(100) == 50, "spell block default cap");
            Assert(DefenceCalculator.SpellBlockChance(40, 50, maximumBlockIncrease: 25) == 60, "spell block increased chance");
            Assert(DefenceCalculator.SpellSuppressionChance(50) == 50, "suppression chance");
            Assert(DefenceCalculator.SpellSuppressionChance(120) == 100, "suppression chance cap");
            Assert(DefenceCalculator.SpellSuppressionDamageMultiplier(50, 50) == 0.75m, "partial suppression multiplier");
            Assert(DefenceCalculator.SpellSuppressionDamageMultiplier(100, 50) == 0.5m, "full suppression multiplier");
            Assert(EhpCalculator.ExpectedSpellDamageMultiplier(0.5m, 50, 50) == 0.375m,
                "expected spell suppression multiplier");
            Assert(EhpCalculator.ExpectedSpellDamageMultiplier(0.5m, 0, 50, 75) == 0.125m,
                "expected spell dodge multiplier");
            var spellEstimate = EhpCalculator.SpellEhpEstimate("Fire", 100, 1000, 0.25m, 50, 50);
            Assert(spellEstimate is not null && spellEstimate.ExpectedDamageMultiplier == 0.1875m &&
                   Round2(spellEstimate.EffectiveHitPool!.Value) == 5333.33m,
                "typed spell EHP scenario");
            Assert(EhpCalculator.SpellEhpEstimate("", 100, 1000, 0.25m) is null,
                "invalid spell EHP scenario");
            var attackScenario = EhpCalculator.AttackEhpEstimate(new AttackEhpScenario(
                "Physical", 100, 1000, 0.5m, 100, AttackDodgeChancePercent: 75));
            Assert(attackScenario is not null && attackScenario.ExpectedDamageMultiplier == 0.125m &&
                   attackScenario.AttackDodgeChancePercent == 75,
                "typed attack EHP scenario");
            var spellScenario = EhpCalculator.SpellEhpEstimate(new SpellEhpScenario(
                "Fire", 100, 1000, 0.25m, 50, 50));
            Assert(spellScenario is not null && spellScenario.ExpectedDamageMultiplier == 0.1875m,
                "typed spell scenario record");
            Assert(EhpCalculator.ExpectedAttackDamageMultiplier(0.5m, 100, 50, 0) == 0.25m,
                "expected attack block multiplier");
            Assert(EhpCalculator.ExpectedAttackDamageMultiplier(0.5m, 100, 0, 50, 40) == 0.4m,
                "expected attack deflection multiplier");
            Assert(EhpCalculator.ExpectedAttackDamageMultiplier(0.5m, 100, 0, 0, 40, 0, 75) == 0.125m,
                "expected attack dodge multiplier");
            Assert(DefenceCalculator.EnergyShieldRechargePerSecond(1000, 20) == 150, "ES recharge rate modifier");
            Assert(DefenceCalculator.EnergyShieldRechargeDelaySeconds(0) == 4, "ES recharge delay");
            Assert(DefenceCalculator.EnergyShieldRechargeDelaySeconds(100) == 2, "faster ES recharge start");
            Assert(DefenceCalculator.EnergyShieldRechargeDelaySeconds(-100) is null, "invalid ES recharge speed");
            Assert(DefenceCalculator.EnergyShieldAfterRechargeWindow(1000, 100, 3, 100, 4) == 100,
                "ES stays unchanged during delay");
            Assert(DefenceCalculator.EnergyShieldAfterRechargeWindow(1000, 100, 6, 100, 4) == 300,
                "ES recovers after delay");
            Assert(DefenceCalculator.EnergyShieldAfterRechargeWindow(1000, 950, 10, 100, 4) == 1000,
                "ES recharge caps at maximum");
            Assert(DefenceCalculator.EnergyShieldAfterRechargeWindow(1000, 100, -1, 100, 4) is null,
                "invalid ES recovery window");
            Assert(ResourceRecovery.LifeRegenerationPerSecond(120, 50) == 3,
                "life regeneration rate modifier");
            Assert(ResourceRecovery.AfterRecoveryWindow(1000, 100, 2, 100) == 300,
                "continuous recovery window");
            Assert(ResourceRecovery.AfterRecoveryWindow(1000, 950, 2, 100) == 1000,
                "continuous recovery cap");
            Assert(ResourceRecovery.AfterRecoveryWindow(1000, 100, -1, 100) is null,
                "invalid continuous recovery window");
            var damageSequence = new[]
            {
                new EnergyShieldDamageEvent(0, 300),
                new EnergyShieldDamageEvent(5, 100)
            };
            Assert(ResourceRecovery.EnergyShieldAfterDamageSequence(1000, 1000, damageSequence, 9, 100, 4) == 700,
                "ES sequence interruption");
            Assert(ResourceRecovery.EnergyShieldAfterDamageSequence(1000, 1000, damageSequence, 10, 100, 4) == 800,
                "ES sequence final recovery");
            Assert(ResourceRecovery.EnergyShieldAfterDamageSequence(1000, 950,
                [new EnergyShieldDamageEvent(0, 0)], 10, 100, 4) == 1000,
                "ES sequence recovery cap");
            Assert(ResourceRecovery.EnergyShieldAfterDamageSequence(1000, 1000,
                [new EnergyShieldDamageEvent(1, 0)], 2, 100, 4) is null,
                "ES sequence must start at zero");
            var reservation = ResourceReservation.Calculate(100, 15, 20);
            Assert(reservation is not null && reservation.Reserved == 35 && reservation.Unreserved == 65 &&
                   reservation.ReservedPercent == 35, "resource reservation contract");
            var cappedReservation = ResourceReservation.Calculate(100, 95, 20);
            Assert(cappedReservation is not null && cappedReservation.Reserved == 100 && cappedReservation.Unreserved == 0,
                "reservation cap");
            Assert(ResourceReservation.Calculate(100, -1) is null, "invalid reservation input");
            var bucket = new StatBucket();
            var item = new ItemContext();
            StatInterpreter.Apply(bucket, "local_block_chance_+%", 10, null);
            StatInterpreter.Apply(bucket, "local_block_chance_+%", 20, item);
            StatInterpreter.Apply(bucket, "base_deflection_rating_%_of_armour", 20, null);
            StatInterpreter.Apply(bucket, "base_life_regeneration_rate_per_minute", 120, null);
            StatInterpreter.Apply(bucket, "life_regeneration_rate_+%", 50, null);
            StatInterpreter.Apply(bucket, "energy_shield_recharge_rate_+%", 15, null);
            StatInterpreter.Apply(bucket, "energy_shield_delay_-%", 25, null);
            StatInterpreter.Apply(bucket, "spell_suppression_chance_%", 50, null);
            StatInterpreter.Apply(bucket, "spell_suppression_effect", 10, null);
            StatInterpreter.Apply(bucket, "base_spell_block_%", 30, null);
            StatInterpreter.Apply(bucket, "additional_spell_block_%", 5, null);
            StatInterpreter.Apply(bucket, "base_chance_to_dodge_%", 40, null);
            StatInterpreter.Apply(bucket, "base_chance_to_dodge_spells_%", 25, null);
                Assert(bucket.BlockInc == 10 && item.BlockInc == 20 && bucket.DeflectPctOfArmour == 20 &&
                         bucket.LifeRegenPerMin == 120 && bucket.LifeRegenInc == 50 &&
                         bucket.EsRechargeInc == 15 && bucket.EsRechargeFasterInc == 25 &&
                         bucket.SpellSuppressionChance == 50 && bucket.SpellSuppressionEffectAdd == 10 &&
                         bucket.SpellBlockBase == 30 && bucket.SpellBlockAdditional == 5 &&
                         bucket.AttackDodgeChance == 40 && bucket.SpellDodgeChance == 25,
                    "defence stat scope mapping");
        }));

        await test("Defence: PoB2 hit-chance formulas round and clamp", () => Task.Run(() =>
        {
            Assert(DefenceCalculator.PlayerHitChance(100, 100) == 96, "player 100/100");
            Assert(DefenceCalculator.PlayerHitChance(100, 50) == 78, "player rounding");
            Assert(DefenceCalculator.PlayerHitChance(100, 0) == 5, "zero accuracy floor");
            Assert(DefenceCalculator.PlayerHitChance(0, 100) == 100, "zero target evasion");
            Assert(DefenceCalculator.PlayerHitChance(1, 1000) == 100, "capped high chance");
            Assert(DefenceCalculator.PlayerHitChance(1, 1000, uncapped: true) == 125, "uncapped high chance");
            Assert(DefenceCalculator.MonsterHitChance(100, 100) == 81, "monster 100/100");
            Assert(DefenceCalculator.MonsterHitChance(100, 0) == 5, "zero monster accuracy floor");
            Assert(DefenceCalculator.MonsterHitChance(0, 0) == 100, "zero player evasion");
            Assert(DefenceCalculator.MonsterHitChance(0, 100) == 100, "zero player evasion with accuracy");
        }));

        await test("Calc: character summary uses same-level default monster for both hit chances", () => Task.Run(() =>
        {
            var build = BuildDocument.Create("Hit chance") with { Level = 70, Tree = new() { ClassIndex = 0 } };
            var summary = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var monster = Catalog.Value.Monsters["70"];
            Assert(summary.EstimateMonsterLevel == 70, "estimate level " + summary.EstimateMonsterLevel);
            Assert(summary.HitChancePercent == DefenceCalculator.PlayerHitChance(monster.Evasion ?? 0, summary.Accuracy),
                "player hit chance " + summary.HitChancePercent);
            Assert(summary.MonsterHitChancePercent == DefenceCalculator.MonsterHitChance(summary.Evasion, monster.Accuracy ?? 0),
                "monster hit chance " + summary.MonsterHitChancePercent);
            Assert(summary.DeflectionChancePercent == DefenceCalculator.DeflectionChance(summary.DeflectionRating, monster.Accuracy ?? 0),
                "deflection chance " + summary.DeflectionChancePercent);
            Assert(summary.BlockChanceMax == DefenceCalculator.BlockChanceMaximum(),
                "block maximum " + summary.BlockChanceMax);
            Assert(summary.SpellSuppressionChancePercent is null, "no suppression source must not invent chance");
            Assert(summary.AttackDodgeChancePercent is null && summary.SpellDodgeChancePercent is null,
                "no dodge source must not invent chance");
            Assert(summary.EhpEstimates.Count == 5, "EHP vector count " + summary.EhpEstimates.Count);
            var physicalEhp = summary.EhpEstimates.Single(e => e.DamageType == "Physical");
            var chaosEhp = summary.EhpEstimates.Single(e => e.DamageType == "Chaos");
            Assert(physicalEhp.RawHit == Round2(monster.PhysicalDamage ?? 0), "EHP raw hit " + physicalEhp.RawHit);
            Assert(physicalEhp.Pool == summary.Life + summary.EnergyShield, "EHP pool " + physicalEhp.Pool);
            Assert(chaosEhp.Pool == EhpCalculator.ResourcePoolForDamageType("Chaos", summary.Life, summary.EnergyShield),
                "chaos EHP pool " + chaosEhp.Pool);
            Assert(summary.ExpectedAttackEhp is not null, "expected attack EHP exists with default monster");
            Assert(summary.ExpectedAttackEhp!.HitChancePercent == summary.MonsterHitChancePercent,
                "expected attack uses monster hit chance");
            var withoutCatalog = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, null);
            Assert(withoutCatalog.HitChancePercent is null && withoutCatalog.MonsterHitChancePercent is null &&
                   withoutCatalog.DeflectionChancePercent is null && withoutCatalog.ExpectedAttackEhp is null &&
                   withoutCatalog.EhpEstimates.Count == 0,
                "missing catalog must not invent defence scenarios");
        }));

        await test("Defence: reservation context is explicit and preserves unreserved pools", () => Task.Run(() =>
        {
            var build = BuildDocument.Create("Reservation") with { Level = 1, Tree = new() { ClassIndex = 0 } };
            var withoutContext = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            Assert(withoutContext.LifeReservation is null && withoutContext.ManaReservation is null &&
                   withoutContext.SpiritReservation is null, "no context must not invent reservation");
            var withContext = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value,
                new ResourceReservationContext(LifeReservedFlat: 10));
            Assert(withContext.LifeReservation is not null && withContext.LifeReservation.Reserved == 10 &&
                   withContext.LifeReservation.Unreserved == withContext.Life - 10,
                "explicit life reservation");
            var persistedBuild = build with
            {
                Reservation = new ResourceReservationPlan { LifeReservedFlat = 10 }
            };
            var fromBuild = CharacterCalculator.Calculate(persistedBuild, Tree.Value, StatMap.Value, Catalog.Value);
            Assert(fromBuild.LifeReservation is not null && fromBuild.LifeReservation.Reserved == 10,
                "persisted reservation plan");
            try
            {
                BuildValidation.Validate(persistedBuild with
                {
                    Reservation = new ResourceReservationPlan { ManaReservedPercent = -1 }
                });
                Assert(false, "negative persisted reservation must fail validation");
            }
            catch (BuildFormatException) { }
        }));

        await test("Calc: persisted defence plan produces an explicit spell EHP scenario", () => Task.Run(() =>
        {
            var build = BuildDocument.Create("Spell scenario") with
            {
                Level = 1,
                Tree = new() { ClassIndex = 0 },
                Defence = new DefenceScenarioPlan
                {
                    SpellRawHit = 100,
                    SpellDamageType = "Fire",
                    SpellHitChancePercent = 50
                }
            };
            var summary = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            Assert(summary.ExpectedSpellEhp is not null, "explicit spell EHP exists");
            Assert(summary.ExpectedSpellEhp!.RawHit == 100 &&
                   summary.ExpectedSpellEhp.HitChancePercent == 50 &&
                   summary.ExpectedSpellEhp.ExpectedDamageMultiplier == 0.5m,
                "explicit spell scenario values");
            try
            {
                BuildValidation.Validate(build with
                {
                    Defence = new DefenceScenarioPlan { SpellRawHit = 100, SpellDamageType = "Unknown" }
                });
                Assert(false, "invalid defence plan must fail validation");
            }
            catch (BuildFormatException) { }
        }));

        await test("Defence: spell resistance hit modifiers affect EHP but not panel resistance", () => Task.Run(() =>
        {
            var build = BuildDocument.Create("Spell resistance hit") with
            {
                ProgressStage = "endgame",
                Tree = new() { ClassIndex = 0 },
                Defence = new DefenceScenarioPlan
                {
                    SpellRawHit = 100,
                    SpellDamageType = "Fire",
                    SpellResistanceReductionPercent = 10,
                    SpellResistancePenetrationPercent = 20
                }
            };
            var summary = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            Assert(summary.FireRes == -40, "panel fire resistance " + summary.FireRes);
            Assert(summary.ExpectedSpellEhp is not null, "spell EHP exists");
            Assert(summary.ExpectedSpellEhp!.SuccessfulHitMultiplier == 1.7m,
                "hit multiplier " + summary.ExpectedSpellEhp.SuccessfulHitMultiplier);
        }));

        await test("Calc: mapped life regeneration modifiers reach the character summary", () => Task.Run(() =>
        {
            var helmet = Catalog.Value.Bases.Values.First(b => b.ItemClass == "Helmet");
            var item = new GearItem
            {
                BaseId = helmet.Id,
                Name = "Recovery helmet",
                Rarity = "rare",
                Mods = [new ModRoll { Id = "LifeRegeneration1", Values = [120] }],
                Corrupted = true,
                CorruptedMods = [new ModRoll { Id = "CorruptionLifeRegenerationRate1", Values = [50] }]
            };
            var build = BuildDocument.Create("Life recovery") with
            {
                Level = 1,
                Equipment = new EquipmentPlan
                {
                    Items = [item],
                    Slots = new Dictionary<string, Guid> { ["Helmet"] = item.Id }
                }
            };
            var summary = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            Assert(summary.LifeRegenPerSecond == 3, "life regen summary " + summary.LifeRegenPerSecond);
        }));

        await test("Calc: Fireball spell DPS comes from per-level damage, cast time and 2x crit", () => Task.Run(() =>
        {
            var gem = Catalog.Value.Gems.Values.First(g => g.Name == "Fireball");
            var skill = gem.Skill!;
            var l1 = skill.Levels["1"];
            decimal avg = (l1["spell_minimum_base_fire_damage"] + l1["spell_maximum_base_fire_damage"]) / 2;
            decimal rate = 1000m / (skill.CastTime ?? 1000);
            decimal crit = (skill.Crit ?? 0) / 100m;
            decimal expected = Round1(avg * rate * (1 + crit / 100 * 100 / 100));
            var build = BuildDocument.Create("Spell") with { Level = 1, Skills = new() { Groups = [GemGroup(gem.Id, "Fireball")] } };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var info = s.Skills.Single();
            Assert(info.HasData, "has data");
            Assert(info.Dps == expected, $"dps {info.Dps} expected {expected}");
            Assert(info.AvgHit == Round1(avg), "avg " + info.AvgHit);
            Assert(info.HitsPerSecond == Round2(rate), "rate " + info.HitsPerSecond);
            Assert(info.CritChancePercent == Round2(crit), "crit " + info.CritChancePercent);
            Assert(info.CritBonusPercent == 100, "bonus " + info.CritBonusPercent);
            Assert(info.ManaCost == skill.Costs["1"]["Mana"], "mana cost");
            Assert(info.Split.Fire == Round1(avg) && info.Split.Physical == 0, "split fire " + info.Split.Fire);
        }));

        await test("Calc: weapon quality scales the attack's base physical damage", () => Task.Run(() =>
        {
            var sword = Catalog.Value.Bases.Values.First(b => b.Id.EndsWith("OneHandSwordDemigods1"));
            var props = sword.Props;
            var gemId = Catalog.Value.Gems.Values.First(g => g.Kind == "active" && g.Tags.Contains("attack")).Id;
            var it0 = new GearItem { BaseId = sword.Id, Name = "Test blade", Rarity = "normal", Quality = 0 };
            var it20 = new GearItem { BaseId = sword.Id, Name = "Test blade", Rarity = "normal", Quality = 20 };
            BuildDocument With(GearItem it) => BuildDocument.Create("AttackQ") with
            {
                Level = 1,
                Equipment = new() { WeaponSet = 1, Items = [it], Slots = new() { ["Main1"] = it.Id } },
                Skills = new() { Groups = [GemGroup(gemId, "Strike", weaponSet: 1)] }
            };
            decimal avg = (props.PhysMin!.Value + props.PhysMax!.Value) / 2;
            decimal rate = 1000m / props.AttackTime!.Value;
            // Props.CritChance is stored scaled (e.g. 500 for 5%), matching the existing attack test:
            // the code divides by 100 once for the percentage and once for the crit-bonus fraction.
            decimal critFraction = props.CritChance!.Value / 100m / 100m;
            var q0 = CharacterCalculator.Calculate(With(it0), Tree.Value, StatMap.Value, Catalog.Value).Skills.Single();
            var q20 = CharacterCalculator.Calculate(With(it20), Tree.Value, StatMap.Value, Catalog.Value).Skills.Single();
            Assert(q0.AvgHit == Round1(avg), "quality-0 avg " + q0.AvgHit);
            Assert(q20.AvgHit == Round1(avg * 1.2m), "quality-20 avg " + q20.AvgHit + " expected " + Round1(avg * 1.2m));
            Assert(Round1(q20.Dps) == Round1(avg * 1.2m * rate * (1 + critFraction)), "quality-20 dps " + q20.Dps);
        }));

        await test("Calc: skill cooldown limits cast frequency", () => Task.Run(() =>
        {
            var skill = new GemSkill(100, 0, [], new Dictionary<string, Dictionary<string, decimal>>(),
                new Dictionary<string, Dictionary<string, decimal>>(),
                new Dictionary<string, Dictionary<string, string>>(), null, 8000);
            decimal rate = 1000m / Math.Max(1, skill.CastTime!.Value);
            rate = Math.Min(rate, 1000m / skill.Cooldown!.Value);
            Assert(rate == 0.125m, "cooldown rate " + rate);
        }));

        await test("Calc: attack DPS derives from weapon damage, attack time and weapon crit", () => Task.Run(() =>
        {
            var sword = Catalog.Value.Bases.Values.First(b => b.Id.EndsWith("OneHandSwordDemigods1"));
            var props = sword.Props;
            var item = new GearItem { BaseId = sword.Id, Name = "Test blade", Rarity = "normal" };
            var guid = item.Id;
            var build = BuildDocument.Create("Attack") with
            {
                Level = 1,
                Equipment = new() { WeaponSet = 1, Items = [item], Slots = new() { ["Main1"] = guid } },
                Skills = new() { Groups = [GemGroup(Catalog.Value.Gems.Values.First(g => g.Kind == "active" && g.Tags.Contains("attack")).Id, "Strike", weaponSet: 1)] }
            };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var info = s.Skills.Single();
            Assert(info.HasData && info.IsAttack, "attack info");
            decimal avg = (props.PhysMin!.Value + props.PhysMax!.Value) / 2;
            decimal rate = 1000m / props.AttackTime!.Value;
            decimal expected = Round1(avg * rate * (1 + props.CritChance!.Value / 100m / 100 * 1));
            Assert(info.Dps == expected, $"dps {info.Dps} expected {expected}");
            Assert(info.HitsPerSecond == Round2(rate), "rate");
            Assert(info.CritChancePercent == Round2(props.CritChance.Value / 100m), "crit");
            Assert(info.AvgHit == Round1(avg), "avg");
            Assert(info.Breakdown.Count >= 4, "attack breakdown has " + info.Breakdown.Count + " steps");
            Assert(string.Join("\n", info.Breakdown).Contains("Base (weapon):"), "breakdown lacks the base line");
        }));

        await test("Calc: spell damage effectiveness defaults to 100% on the pinned 0.5.5 export", () => Task.Run(() =>
        {
            foreach (var gem in Catalog.Value.Gems.Values.Where(g => g.Skill is not null))
            {
                var statics = gem.Skill!.Statics;
                decimal effectiveness = statics.TryGetValue("damage_effectiveness", out var eff)
                    || statics.TryGetValue("spell_damage_effectiveness", out eff) ? eff : 100m;
                // The pinned 0.5.5 export carries no effectiveness statics; the calculator must
                // fall back to 100%. A future export that adds effectiveness updates this contract.
                Assert(effectiveness == 100m, gem.Id + " has unexpected pinned effectiveness " + effectiveness);
            }
        }));

        await test("Calc: attack gain-as extra is added to the base hit and reported in the breakdown", () => Task.Run(() =>
        {
            var sword = Catalog.Value.Bases.Values.First(b => b.Id.EndsWith("OneHandSwordDemigods1"));
            var localMod = Catalog.Value.ModsFor(sword, 100).First(m => m.Stats.Length == 1 && m.Stats[0].Id == "local_physical_damage_+%" && m.Stats[0].Max >= 50);
            var gainMod = Catalog.Value.Mods["SpellDamageGainedAsCold3"]; // 19-21% of Damage as Extra Cold Damage
            var attackGem = Catalog.Value.Gems.Values.First(g => g.Kind == "active" && g.Tags.Contains("attack") && g.Levels.Contains(1) &&
                g.Skill is not null && (g.Skill.Statics.Count == 0 || !g.Skill.Statics.Keys.Any(k => k.Contains("convert", StringComparison.Ordinal) || k.Contains("_to_add_as_", StringComparison.Ordinal))));
            var item = new GearItem
            {
                BaseId = sword.Id,
                Name = "Gain blade",
                Rarity = "rare",
                Mods = [
                    new ModRoll { Id = localMod.Id, Values = [localMod.Stats[0].Max] },
                    new ModRoll { Id = gainMod.Id, Values = [gainMod.Stats[0].Max] }
                ]
            };
            var guid = item.Id;
            var build = BuildDocument.Create("Gain") with
            {
                Level = 1,
                Equipment = new() { WeaponSet = 1, Items = [item], Slots = new() { ["Main1"] = guid } },
                Skills = new() { Groups = [GemGroup(attackGem.Id, "Strike", weaponSet: 1)] }
            };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var info = s.Skills.Single();
            Assert(info.HasData && info.IsAttack, "gain attack info");
            decimal basePhys = (sword.Props.PhysMin!.Value + sword.Props.PhysMax!.Value) / 2m * (1m + localMod.Stats[0].Max / 100m);
            decimal gainPct = gainMod.Stats[0].Max;
            decimal expectedAvg = basePhys * (1m + gainPct / 100m);
            Assert(info.AvgHit == Round1(expectedAvg), $"avg {info.AvgHit} expected {expectedAvg}");
            string breakdownText = string.Join("\n", info.Breakdown);
            Assert(breakdownText.Contains("Base (weapon):"), "breakdown lacks base line");
            Assert(breakdownText.Contains("Gain as extra (cold)"), "breakdown lacks gain-as line");
            decimal rate = 1000m / sword.Props.AttackTime!.Value;
            decimal expectedDps = Round1(expectedAvg * rate * (1m + sword.Props.CritChance!.Value / 100m / 100m * 1m));
            Assert(info.Dps == expectedDps, $"dps {info.Dps} expected {expectedDps}");
        }));

        await test("Calc: gem quality adds +1% increased damage per 1% quality", () => Task.Run(() =>
        {
            var gem = Catalog.Value.Gems["Metadata/Items/Gem/SkillGemFireball"];
            var build = BuildDocument.Create("Quality") with { Level = 1, Skills = new() { Groups = [GemGroup(gem.Id, "Fireball", level: 1, quality: 20)] } };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var info = s.Skills.Single();
            // Same Fireball at 0% quality: DPS scaled by 1.2 at 20%.
            var plain = BuildDocument.Create("Plain") with { Level = 1, Skills = new() { Groups = [GemGroup(gem.Id, "Fireball", level: 1)] } };
            var s2 = CharacterCalculator.Calculate(plain, Tree.Value, StatMap.Value, Catalog.Value);
            var plainInfo = s2.Skills.Single();
            Assert(Math.Abs(info.Dps - plainInfo.Dps * 1.2m) < 0.05m, $"q20 dps {info.Dps} vs plain {plainInfo.Dps}");
            Assert(info.Breakdown.Any(b => b.Contains("Quality")), "breakdown lacks quality line");
        }));

        await test("Calc: deployed-skill hosts show the hosted gem's damage on the host slot", () => Task.Run(() =>
        {
            // The official export lists the skill inside Spell Totem as a support; its damage must
            // appear on the "Spell Totem" slot (and the directly cast Arc's for comparison).
            var group = new SkillGroup
            {
                Name = "Spell Totem",
                Active = new GemSelection { GemId = "Metadata/Items/Gems/SkillGemSpellTotem", Level = 18, Quality = 20 },
                Supports = [new GemSelection { GemId = "Metadata/Items/Gems/SkillGemArc", Level = 20, Quality = 20 }]
            };
            var build = BuildDocument.Create("Totem") with { Level = 96, Skills = new() { Groups = [group] } };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var info = s.Skills.Single();
            Assert(info.GemName == "Arc", "hosted gem must be Arc, got " + info.GemName);
            Assert(info.Dps > 0, "hosted damage must be non-zero, got " + info.Dps);
            Assert(info.Breakdown.Any(b => b.Contains("Hosted:")), "breakdown lacks hosted line");
        }));

        await test("Calc: damaging ailments follow PoE2 base percentages and durations", () => Task.Run(() =>
        {
            // PoB2 Data/Misc.lua: Bleed 900%/min = 15%/s over 5s; Ignite/Poison 1200%/min = 20%/s over 4s/2s.
            var bleed = AilmentDotCalculator.Evaluate("Bleed", 100, 100, 0, 100, 100);
            Assert(bleed is not null && bleed.DamagePerSecond == 15m && bleed.DurationSeconds == 5m && bleed.TotalDamagePerApplication == 75m, "bleed " + (bleed?.DamagePerSecond));
            var ignite = AilmentDotCalculator.Evaluate("Ignite", 100, 100, 0, 100, 100, increasedPercent: 50);
            Assert(ignite is not null && ignite.DamagePerSecond == 30m && ignite.DurationSeconds == 4m && ignite.TotalDamagePerApplication == 120m, "ignite " + (ignite?.DamagePerSecond));
            var poison = AilmentDotCalculator.Evaluate("Poison", 100, 100, 0, 100, 100, hitsPerSecond: 0.5m);
            Assert(poison is not null && poison.DamagePerSecond == 20m && poison.SustainedDamagePerSecond == 20m, "poison " + (poison?.DamagePerSecond));
            Assert(AilmentDotCalculator.Evaluate("Ignite", 100, 100, 0, 0, 0) is null, "zero chance must disable");
            Assert(AilmentDotCalculator.Evaluate("Bleed", 0, 0, 0, 100, 100) is null, "zero source must disable");
        }));

        await test("Calc: ailment crit weighting applies the crit portion of the source", () => Task.Run(() =>
        {
            // 10% crit, +100% crit bonus (crits double), 100% chance on hit and on crit:
            // weighted source = 100 * 0.9 + 200 * 0.1 = 110 => ignite DPS 110 * 20% = 22.
            var ignite = AilmentDotCalculator.Evaluate("Ignite", 100, 200, 10, 100, 100);
            Assert(ignite is not null && ignite.DamagePerSecond == 22m, "ignite crit " + (ignite?.DamagePerSecond));
        }));

        await test("Calc: Bone Blast's 40% bleeding reaches the skill summary as a DoT", () => Task.Run(() =>
        {
            var gem = Catalog.Value.Gems["Metadata/Items/Gem/SkillGemBoneBlast"];
            var build = BuildDocument.Create("BoneBlast") with
            {
                Level = 1,
                Skills = new() { Groups = [GemGroup(gem.Id, "BoneBlast")] }
            };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var info = s.Skills.Single();
            Assert(info.HasData, "bone blast info");
            var bleed = info.Ailments.SingleOrDefault(a => a.Ailment == "Bleed");
            Assert(bleed is not null, "bone blast must bleed");
            Assert(bleed!.SourceChancePercent == 40m, "bleed chance " + bleed.SourceChancePercent);
            decimal sourcePhys = (5m + 8m) / 2m; // level 1 base 5..8 physical
            // Bone Blast crits with 14% base, +100% bonus (crits double); the ailment instance is a
            // crit/non-crit mix weighted by crit chance (PoB calcAilmentDamage).
            decimal critRate = (gem.Skill!.Crit ?? 0) / 100m / 100m; // Crit is stored in hundredths (1400 = 14%)
            decimal weighted = sourcePhys * (1 - critRate) + sourcePhys * 2m * critRate;
            decimal expectedDps = weighted * 15m / 100m;
            Assert(Math.Abs(bleed.DamagePerSecond - expectedDps) < 0.001m, "bleed dps " + bleed.DamagePerSecond);
            Assert(bleed.DurationSeconds == 5m, "bleed duration " + bleed.DurationSeconds);
            Assert(info.TotalDotDps is decimal dotTotal && dotTotal > 0 && dotTotal <= bleed.DamagePerSecond, "total dot " + info.TotalDotDps);
            Assert(info.Breakdown.Any(b => b.Contains("Bleed source:")), "breakdown lacks bleed line");
        }));

        await test("Calc: armour reduction matches PoB2 (ratio 12, cap 90, break amplifies)", () => Task.Run(() =>
        {
            // PoB2 CalcDefence.lua armourReductionF: A / (A + ArmourRatio * rawHit), upper-capped at 90%.
            Assert(Math.Abs(EhpCalculator.ArmourReductionPercent(1000m, 100m) - (1000m / 2200m * 100m)) < 0.0001m, "reduction " + EhpCalculator.ArmourReductionPercent(1000m, 100m));
            Assert(Math.Abs(EhpCalculator.ArmourDamageMultiplier(1000m, 100m) - (1m - 1000m / 2200m)) < 0.0001m, "mult " + EhpCalculator.ArmourDamageMultiplier(1000m, 100m));
            Assert(EhpCalculator.ArmourReductionPercent(100000m, 100m) == 90m, "cap");
            Assert(EhpCalculator.ArmourReductionPercent(1000m, 0m) == 90m, "zero hit caps at 90% (PoB 100 -> cap)");
            Assert(EhpCalculator.ArmourReductionPercent(0m, 0m) == 0m, "zero-zero");
            Assert(EhpCalculator.ArmourReductionPercent(0m, 100m) == 0m, "no armour");
            // Armour break (negative armour) amplifies instead of reducing.
            Assert(Math.Abs(EhpCalculator.ArmourReductionPercent(-500m, 100m) + 500m / 1700m * 100m) < 0.0001m, "break " + EhpCalculator.ArmourReductionPercent(-500m, 100m));
            Assert(Math.Abs(EhpCalculator.ArmourDamageMultiplier(-500m, 100m) - (1m + 500m / 1700m)) < 0.0001m, "break mult " + EhpCalculator.ArmourDamageMultiplier(-500m, 100m));
        }));

        await test("Calc: a weapon-local physical mod scales only that weapon", () => Task.Run(() =>
        {
            var sword = Catalog.Value.Bases.Values.First(b => b.Id.EndsWith("OneHandSwordDemigods1"));
            var mod = Catalog.Value.ModsFor(sword, 100).First(m => m.Stats.Length == 1 && m.Stats[0].Id == "local_physical_damage_+%" && m.Stats[0].Max >= 20);
            var item = new GearItem { BaseId = sword.Id, Name = "Rolled blade", Mods = [new ModRoll { Id = mod.Id, Values = [20] }] };
            var guid = item.Id;
            var attackGem = Catalog.Value.Gems.Values.First(g => g.Kind == "active" && g.Tags.Contains("attack"));
            var build = BuildDocument.Create("Rolled") with
            {
                Level = 1,
                Equipment = new() { WeaponSet = 1, Items = [item], Slots = new() { ["Main1"] = guid } },
                Skills = new() { Groups = [GemGroup(attackGem.Id, "Strike", weaponSet: 1)] }
            };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var info = s.Skills.Single();
            decimal avg = (sword.Props.PhysMin!.Value + sword.Props.PhysMax!.Value) / 2 * 1.2m;
            decimal expected = Round1(avg * (1000m / sword.Props.AttackTime!.Value) * 1.05m);
            Assert(info.Dps == expected, $"dps {info.Dps} expected {expected}");
            Assert(info.AvgHit == Round1(avg), "avg " + info.AvgHit);
        }));

        await test("Calc: armour scales by local mods and resistances stack then cap", () => Task.Run(() =>
        {
            var body = Catalog.Value.Bases.Values.First(b => b.ClassName == "Body Armours" && (b.Props.Armour ?? 0) > 200);
            var armourMod = Catalog.Value.ModsFor(body, 100).FirstOrDefault(m => m.Stats.Length == 1 && m.Stats[0].Id == "local_physical_damage_reduction_rating_+%" && m.Stats[0].Max >= 20);
            var helmet = Catalog.Value.Bases.Values.First(b => b.ClassName == "Helmets");
            var resMod = Catalog.Value.ModsFor(helmet, 100).Where(m => m.Stats.Length == 1 && m.Stats[0].Id == "base_fire_damage_resistance_%").OrderByDescending(m => m.Stats[0].Max).First();
            var boots = Catalog.Value.Bases.Values.First(b => b.ClassName == "Boots");
            var gloves = Catalog.Value.Bases.Values.First(b => b.ClassName == "Gloves");

            var bodyItem = new GearItem { BaseId = body.Id, Name = "Chest", Mods = armourMod is null ? [] : [new ModRoll { Id = armourMod.Id, Values = [armourMod.Stats[0].Max] }] };
            var resValue = resMod.Stats[0].Max;
            var items = new List<GearItem> { bodyItem };
            var slots = new Dictionary<string, Guid> { ["Body"] = bodyItem.Id };
            foreach (var (slot, b) in new[] { ("Helmet", helmet), ("Gloves", gloves), ("Boots", boots) })
            {
                var it = new GearItem { BaseId = b.Id, Name = slot, Mods = [new ModRoll { Id = resMod.Id, Values = [resValue] }] };
                items.Add(it); slots[slot] = it.Id;
            }
            var build = BuildDocument.Create("Tank") with { Level = 70, Equipment = new() { WeaponSet = 1, Items = [.. items], Slots = slots }, Tree = new() { ClassIndex = 0 } };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            decimal armourFlat = (body.Props.Armour ?? 0) * (armourMod is null ? 1 : 1 + armourMod.Stats[0].Max / 100)
                + (helmet.Props.Armour ?? 0) + (gloves.Props.Armour ?? 0) + (boots.Props.Armour ?? 0);
            decimal expectedArmour = decimal.Round(armourFlat, 0, MidpointRounding.AwayFromZero);
            Assert(s.Armour == expectedArmour, $"armour {s.Armour} expected {expectedArmour}");
            decimal resSum = 3 * resValue;
            Assert(s.FireRes == Math.Min(resSum, CharacterCalculator.ResistanceCap), $"fire effective {s.FireRes} sum {resSum}");
            Assert(s.FireResSources == resSum, $"fire sources {s.FireResSources} sum {resSum}");
            Assert(s.ColdRes == 0m && s.LightRes == 0m && s.ColdResSources == 0m, "other res unaffected");
            Assert(s.PhysicalReductionEstimate is not null && s.PhysicalReductionEstimate is > 0 and <= CharacterCalculator.ArmourCapPercent, "dr estimate");
            Assert(s.EstimateMonsterLevel == 70, "estimate level");
        }));

        await test("Calc: honesty notes flag disabled groups, wrong weapon sets, missing weapons and support coverage", () => Task.Run(() =>
        {
            var fireball = Catalog.Value.Gems.Values.First(g => g.Name == "Fireball").Id;
            var attackGem = Catalog.Value.Gems.Values.First(g => g.Kind == "active" && g.Tags.Contains("attack")).Id;
            var support = Catalog.Value.Gems.Values.First(g => g.Kind == "support" && g.Levels.Contains(1)).Id;
            var build = BuildDocument.Create("Notes") with
            {
                Level = 1,
                Skills = new()
                {
                    Groups =
                    [
                        GemGroup(fireball, "Off") with { Enabled = false },
                        GemGroup(fireball, "Wrong set", weaponSet: 2),
                        GemGroup(attackGem, "No weapon", weaponSet: 1),
                        GemGroup(fireball, "With support", supports: [new() { GemId = support, Level = 1 }]),
                    ]
                }
            };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var byName = s.Skills.ToDictionary(i => i.GroupName);
            Assert(byName["Off"].NoteCodes.Contains("DisabledGroup"), "disabled note");
            Assert(byName["Wrong set"].NoteCodes.Contains("WrongWeaponSet"), "set note");
            Assert(byName["No weapon"].NoteCodes.Contains("NoWeapon") && !byName["No weapon"].HasData && byName["No weapon"].Dps == 0, "weapon note");
            // A support either contributes numbers (Applied) or is honestly reported as not applied (Partial).
            Assert(byName["With support"].NoteCodes.Contains("SupportsApplied") || byName["With support"].NoteCodes.Contains("SupportsPartial"), "support note");
        }));

        await test("Calc: stage baseline and socketed jewel both affect effective resistance", () => Task.Run(() =>
        {
            var resistMod = Catalog.Value.JewelMods.First(m => m.Id == "AllResistancesJewel");
            var jewel = new GearItem { Name = "Well", Rarity = "magic", ItemLevel = 80, Mods = [new ModRoll { Id = resistMod.Id, Values = [resistMod.Stats[0].Max] }] };
            var socketNode = Tree.Value.Nodes.Values.First(n => n.IsJewel && n.IsSupported);
            var starter = BuildDocument.Create("Res") with
            {
                Level = 80,
                Tree = new() { ClassIndex = 0, AllocatedNodes = [socketNode.Id], Jewels = new() { [socketNode.Id] = jewel.Id } },
                Equipment = new() { WeaponSet = 1, Items = [jewel] }
            };
            var endgame = starter with { ProgressStage = "endgame" };
            var sStarter = CharacterCalculator.Calculate(starter, Tree.Value, StatMap.Value, Catalog.Value);
            var sEnd = CharacterCalculator.Calculate(endgame, Tree.Value, StatMap.Value, Catalog.Value);
            var jewelFireRes = resistMod.Stats[0].Max;
            Assert(sStarter.FireRes == Math.Min(jewelFireRes, CharacterCalculator.ResistanceCap), "starter effective " + sStarter.FireRes);
            Assert(sEnd.FireRes == Math.Min(-40m + jewelFireRes, CharacterCalculator.ResistanceCap), "endgame effective " + sEnd.FireRes);
            // Keep the raw source contribution visible separately from the effective result.
            Assert(sStarter.FireResSources == jewelFireRes, "starter jewel sources " + sStarter.FireResSources);
            Assert(sEnd.FireResSources == jewelFireRes, "endgame jewel sources " + sEnd.FireResSources);
        }));

        await test("Calc: effective resistance preserves negatives and caps only the upper side", () => Task.Run(() =>
        {
            var starter = ResistanceCalculator.Calculate(0m, 50m, 0m);
            Assert(starter.Effective == 50m && starter.Sources == 50m, "starter +50: " + starter);

            var endgame = ResistanceCalculator.Calculate(-40m, 50m, 0m);
            Assert(endgame.Effective == 10m, "endgame +50: " + endgame.Effective);

            var negative = ResistanceCalculator.Calculate(0m, -20m, 0m);
            Assert(negative.Effective == -20m, "negative resistance: " + negative.Effective);

            var capped = ResistanceCalculator.Calculate(0m, 100m, 10m);
            Assert(capped.Effective == 85m && capped.Maximum == 85m, "maximum resistance: " + capped);
        }));

        await test("Calc 0.8.0: Lightning Arrow converts about 80 percent of phys to lightning with a bow", () => Task.Run(() =>
        {
            var bow = Catalog.Value.Bases.Values.Where(b => b.ClassName.Contains("Bow")).OrderByDescending(b => b.Props.PhysMax ?? 0).First();
            var la = Catalog.Value.Gems.Values.Where(g => g.Name == "Lightning Arrow" && g.Kind == "active").OrderBy(g => g.Id.Length).First();
            var item = new GearItem { BaseId = bow.Id, Name = "Hunt" };
            var build = BuildDocument.Create("LA") with
            {
                Level = 1,
                Equipment = new() { WeaponSet = 1, Items = [item], Slots = new() { ["Main1"] = item.Id } },
                Skills = new() { Groups = [GemGroup(la.Id, "LA")] }
            };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var info = s.Skills.Single();
            Assert(info.HasData, "has data");
            decimal total = info.Split.Total;
            Assert(total > 0, "total > 0");
            decimal lightningShare = info.Split.Lightning / total;
            Assert(lightningShare > 0.6m && lightningShare < 0.95m, "lightning share " + lightningShare.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            Console.WriteLine("LA PROBE: lightning share = " + lightningShare.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " crit=" + info.CritChancePercent.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        }));

        await test("Calc: an attack group honouring the inactive set is marked out", () => Task.Run(() =>
        {
            var sword = Catalog.Value.Bases.Values.First(b => b.Id.EndsWith("OneHandSwordDemigods1"));
            var item = new GearItem { BaseId = sword.Id, Name = "Blade" };
            var attackGem = Catalog.Value.Gems.Values.First(g => g.Kind == "active" && g.Tags.Contains("attack")).Id;
            var build = BuildDocument.Create("Sets") with
            {
                Level = 1,
                Equipment = new() { WeaponSet = 2, Items = [item], Slots = new() { ["Main1"] = item.Id } },
                Skills = new() { Groups = [GemGroup(attackGem, "Set1 strike", weaponSet: 1)] }
            };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var info = s.Skills.Single();
            Assert(info.NoteCodes.Contains("WrongWeaponSet"), "note " + string.Join(',', info.NoteCodes));
        }));

        await test("Calc: item quality and support levels never silently alter v1 numbers", () => Task.Run(() =>
        {
            var fireball = Catalog.Value.Gems.Values.First(g => g.Name == "Fireball").Id;
            var support = Catalog.Value.Gems.Values.First(g => g.Kind == "support" && g.Levels.Contains(1)).Id;
            var plain = CharacterCalculator.Calculate(BuildDocument.Create("Q") with { Level = 1, Skills = new() { Groups = [GemGroup(fireball, "F")] } }, Tree.Value, StatMap.Value, Catalog.Value);
            var boosted = CharacterCalculator.Calculate(BuildDocument.Create("Q") with
            {
                Level = 1,
                Skills = new() { Groups = [GemGroup(fireball, "F", supports: [new() { GemId = support, Level = 20, Quality = 20 }])] }
            }, Tree.Value, StatMap.Value, Catalog.Value);
            Assert(plain.Skills.Single().Dps == boosted.Skills.Single().Dps, "supports must not alter v1 dps");
        }));

        await test("Calc v3: the bow pool contains +3 projectile levels and a corrupted pool", () => Task.Run(() =>
        {
            var bow = Catalog.Value.Bases.Values.First(b => b.ItemClass == "Bow");
            var pool = Catalog.Value.ModsFor(bow, 100).ToList();
            Assert(pool.Any(m => m.Stats.Any(s => s.Id == "projectile_skill_gem_level_+" && s.Max == 3)), "bow +3 levels missing");
            Assert(pool.Any(m => m.Stats.Any(s => s.Id == "projectile_skill_gem_level_+" && s.Max == 4)), "bow +4 levels missing");
            var corrupted = Catalog.Value.CorruptedFor(bow).ToList();
            Assert(corrupted.Count >= 6, "bow corrupted pool " + corrupted.Count);
            Assert(corrupted.All(m => m.Kind == "corrupted"), "kind");
        }));

        await test("Calc v3: endgame stage starts elemental resists at -40, starter at 0", () => Task.Run(() =>
        {
            var starter = CharacterCalculator.Calculate(BuildDocument.Create("S") with { Level = 70, Tree = new() { ClassIndex = 1 } }, Tree.Value, StatMap.Value, Catalog.Value);
            var endgame = CharacterCalculator.Calculate(BuildDocument.Create("E") with { Level = 70, Tree = new() { ClassIndex = 1 }, ProgressStage = "endgame" }, Tree.Value, StatMap.Value, Catalog.Value);
            Assert(starter.FireRes == 0 && starter.ColdRes == 0 && starter.LightRes == 0, "starter res");
            Assert(endgame.FireRes == -40 && endgame.ColdRes == -40 && endgame.LightRes == -40, "endgame res " + endgame.FireRes);
            Assert(endgame.ChaosRes == 0, "chaos unaffected");
        }));

        await test("Calc v3: weapon '+2 projectile levels' raises Spark to its level-3 values", () => Task.Run(() =>
        {
            var bow = Catalog.Value.Bases.Values.First(b => b.ItemClass == "Bow");
            var mod = Catalog.Value.ModsFor(bow, 100).First(m => m.Stats.Any(s => s.Id == "projectile_skill_gem_level_+" && s.Max == 2));
            var spark = Catalog.Value.Gems.Values.First(g => g.Name == "Spark" && g.Skill is not null && g.Tags.Contains("projectile"));
            var level3 = spark.Skill!.Levels["3"];
            decimal avg3 = (level3["spell_minimum_base_lightning_damage"] + level3["spell_maximum_base_lightning_damage"]) / 2;
            var item = new GearItem { BaseId = bow.Id, Name = "Ranger bow", Mods = [new ModRoll { Id = mod.Id, Values = mod.Stats.Select(st => st.Max).ToArray() }] };
            var build = BuildDocument.Create("Lv") with
            {
                Level = 40,
                Equipment = new() { WeaponSet = 1, Items = [item], Slots = new() { ["Main1"] = item.Id } },
                Skills = new() { Groups = [GemGroup(spark.Id, "Sparks", weaponSet: 1)] }
            };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var info = s.Skills.Single();
            Assert(info.LevelFromItems == 2, "levelFromItems " + info.LevelFromItems);
            Assert(info.AvgHit == Round1(avg3), $"avg {info.AvgHit} expected {avg3}");
        }));

        await test("Calc v3: corrupted added fire damage reaches the attack split", () => Task.Run(() =>
        {
            var bow = Catalog.Value.Bases.Values.First(b => b.ItemClass == "Bow");
            var corrupt = Catalog.Value.CorruptedFor(bow).First(m => m.Stats.Any(s => s.Id == "local_minimum_added_fire_damage"));
            decimal fmin = corrupt.Stats.First(s => s.Id == "local_minimum_added_fire_damage").Max;
            decimal fmax = corrupt.Stats.First(s => s.Id == "local_maximum_added_fire_damage").Max;
            var attackGem = Catalog.Value.Gems.Values.First(g => g.Kind == "active" && g.Tags.Contains("attack") && g.Tags.Contains("projectile"));
            var item = new GearItem { BaseId = bow.Id, Name = "Scorched bow", Corrupted = true, CorruptedMods = [new ModRoll { Id = corrupt.Id, Values = [fmin, fmax] }] };
            var build = BuildDocument.Create("C") with
            {
                Level = 60,
                Equipment = new() { WeaponSet = 1, Items = [item], Slots = new() { ["Main1"] = item.Id } },
                Skills = new() { Groups = [GemGroup(attackGem.Id, "Arrows", weaponSet: 1)] }
            };
            var s = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            var info = s.Skills.Single();
            Assert(info.HasData, "attack data");
            Assert(info.Split.Fire == Round1((fmin + fmax) / 2), $"fire {info.Split.Fire} expected {(fmin + fmax) / 2}");
        }));

        await test("Calc v3: corrupted item validation rejects foreign corruption and extra rolls", () => Task.Run(() =>
        {
            var bow = Catalog.Value.Bases.Values.First(b => b.ItemClass == "Bow");
            var helmet = Catalog.Value.Bases.Values.First(b => b.ItemClass == "Helmet");
            var corruptPool = Catalog.Value.CorruptedFor(bow).ToList();
            var (corrupt, corrupt2) = (corruptPool[0], corruptPool[1]);
            var foreign = Catalog.Value.CorruptedFor(helmet).First(m => !Catalog.Value.CorruptedFor(bow).Any(x => x.Id == m.Id));
            var body = Catalog.Value.Bases.Values.First(b => b.ItemClass == "Body Armour");
            var bodyCorrupt = Catalog.Value.CorruptedFor(body).First();
            void Throws(PlanningException? e, string code) => Assert(e?.Code == code, "expected " + code + " got " + e?.Code);
            PlanningException? Run(GearItem item) { try { EquipmentRules.ValidateItem(Catalog.Value, item); return null; } catch (PlanningException e) { return e; } }
            Throws(Run(new GearItem { BaseId = bow.Id, Corrupted = true, CorruptedMods = [new ModRoll { Id = foreign.Id, Values = [] }] }), "PlanCorruptInvalid");
            Throws(Run(new GearItem { BaseId = bow.Id, Corrupted = true, CorruptedMods = [new ModRoll { Id = corrupt.Id, Values = [] }, new ModRoll { Id = corrupt2.Id, Values = [] }] }), "PlanCorruptLimit");
            var ok = new GearItem { BaseId = body.Id, Corrupted = true, CorruptedMods = [new ModRoll { Id = bodyCorrupt.Id, Values = bodyCorrupt.Stats.Select(x => x.Max).ToArray() }] };
            Assert(Run(ok) is null, "valid corruption rejected");
        }));

        await test("Calc v3: scoped damage stats are catalogued into the skill-scope store", () => Task.Run(() =>
        {
            var bucket = new PoeBuilder.Core.Calculation.StatBucket();
            PoeBuilder.Core.Calculation.StatInterpreter.Apply(bucket, "bow_damage_+%", 20, null);
            PoeBuilder.Core.Calculation.StatInterpreter.Apply(bucket, "physical_bow_damage_+%", 15, null);
            PoeBuilder.Core.Calculation.StatInterpreter.Apply(bucket, "projectile_skill_gem_level_+", 2, null);
            Assert(bucket.ScopedDamage.Any(x => x.Words.SequenceEqual(["bow"]) && x.Value == 20), "bow scope");
            Assert(bucket.ScopedDamage.Any(x => x.Words.SequenceEqual(["physical", "bow"]) && x.Value == 15), "physical+bow scope");
            Assert(bucket.GemLevels.Any(x => x.Scope == "projectile" && x.Value == 2), "gem level scope");
        }));

        await test("Calc: the pinned statmap loads with a healthy line count", () => Task.Run(() =>
        {
            Assert(StatMap.Value.Lines.Count > 1500, "lines " + StatMap.Value.Lines.Count);
            Assert(StatMap.Value.Lines.ContainsKey("+10% to Fire Resistance"), "anchor line");
            Assert(GameStatMap.Sha256.Length == 64, "sha pinned");
        }));

        await test("Calc: game manifest hashes match the pinned catalog files", () => Task.Run(() =>
        {
            string dataRoot = Path.Combine(AppContext.BaseDirectory, "Data", "Game");
            using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "manifest.json")));
            var files = manifest.RootElement.GetProperty("files");
            foreach (string name in new[] { "catalog.json", "statmap.json" })
            {
                string expected = files.GetProperty(name).GetString()!;
                string actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(dataRoot, name))));
                Assert(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase), name + " hash mismatch");
            }
        }));

        await test("Calc: the pinned unique catalog includes equipment and jewel identities", () => Task.Run(() =>
        {
            var source = Catalog.Value.Data.Uniques ?? [];
            Assert(source.Length == 449, "unique source coverage " + source.Length);
            Assert(source.Count(u => u.ItemClass.Equals("Jewel", StringComparison.OrdinalIgnoreCase)) == 15,
                "unique jewel coverage");
            Assert(Catalog.Value.Uniques.Count >= source.Select(u => u.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                "unique supplement did not preserve source identities");
            Assert(Catalog.Value.Uniques.ContainsKey("Hands of Wisdom and Action") &&
                   Catalog.Value.Uniques.ContainsKey("Morior Invictus") &&
                   Catalog.Value.Uniques.ContainsKey("Headhunter"), "required unique identities missing");
            Assert(source.All(u => u.Icon.StartsWith("Art/", StringComparison.Ordinal) && u.Icon.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)),
                "unique artwork paths are incomplete");
            Assert(Catalog.Value.Uniques.Values.Any(u => u.ItemClass != "Jewel"), "unique equipment identities missing");
        }));

        await test("Calc: tree key resource buckets (Eldritch Battery, MoM, life regen %, spirit reserve, source gain) apply", () => Task.Run(() =>
        {
            var bucket = new StatBucket();
            StatInterpreter.Apply(bucket, "energy_shield_to_mana", 100, null);
            StatInterpreter.Apply(bucket, "skill_mana_cost_+100%_final", 100, null);
            StatInterpreter.Apply(bucket, "life_regeneration_percent_per_second", 0.5m, null);
            StatInterpreter.Apply(bucket, "spirit_reserved_flat", 75, null);
            StatInterpreter.Apply(bucket, "damage_removed_from_mana_before_life_%", 100, null);
            Assert(bucket.EnergyShieldToManaPercent == 100 && bucket.ManaCostFinalPct == 100, "Eldritch Battery buckets");
            Assert(bucket.LifeRegenPercentPerSecond == 0.5m && bucket.SpiritReservedFlat == 75, "regen/spirit buckets");
            Assert(bucket.DamageTakenFromManaPercent == 100, "MoM mana routing bucket");
            StatInterpreter.Apply(bucket, "non_skill_base_lightning_damage_%_to_gain_as_cold", 6, null);
            Assert(bucket.SourceGainAs[("lightning", "cold")] == 6, "source gain as bucket");
        }));

        await test("Calc: keystone lines absent from the stat map resolve via TreeStatFallbacks (Eldritch Battery converts ES to Mana)", () => Task.Run(() =>
        {
            var esBody = Catalog.Value.Bases.Values.First(b => b.ItemClass == "Body Armour" && (b.Props.EnergyShield ?? 0) > 0);
            var item = new GearItem { BaseId = esBody.Id, Name = "ES body" };
            var build = BuildDocument.Create("E") with
            {
                Level = 90,
                Equipment = new() { WeaponSet = 1, Items = [item], Slots = new() { ["Body"] = item.Id } },
                Tree = new PassiveTreePlan() with { AllocatedNodes = [57513] }
            };
            var plain = CharacterCalculator.Calculate(build with { Tree = null }, Tree.Value, StatMap.Value, Catalog.Value);
            var converted = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            Assert(plain.EnergyShield > 0, "plain ES from body armour " + plain.EnergyShield);
            Assert(converted.EnergyShield == 0 && converted.Mana > plain.Mana,
                "Eldritch Battery ES->Mana es=" + converted.EnergyShield + " mana=" + converted.Mana + " vs " + plain.Mana);
        }));

        await test("Calc: item quality scales base defences and the ES-to-Mana conversion with it", () => Task.Run(() =>
        {
            var esBody = Catalog.Value.Bases.Values.First(b => b.ItemClass == "Body Armour" && (b.Props.EnergyShield ?? 0) > 100 && b.Implicits.Length == 0);
            decimal esBase = esBody.Props.EnergyShield!.Value;
            GearItem Make(int quality) => new() { BaseId = esBody.Id, Name = "ES body", Quality = quality };
            var tree = new PassiveTreePlan() with { AllocatedNodes = [57513] };
            BuildDocument With(GearItem item, PassiveTreePlan? withTree) => BuildDocument.Create("Quality") with
            {
                Level = 90,
                Equipment = new() { WeaponSet = 1, Items = [item], Slots = new() { ["Body"] = item.Id } },
                Tree = withTree
            };
            var plain = CharacterCalculator.Calculate(With(Make(20), null), Tree.Value, StatMap.Value, Catalog.Value);
            var ebQ0 = CharacterCalculator.Calculate(With(Make(0), tree), Tree.Value, StatMap.Value, Catalog.Value);
            var ebQ20 = CharacterCalculator.Calculate(With(Make(20), tree), Tree.Value, StatMap.Value, Catalog.Value);
            Assert(ebQ0.EnergyShield == 0 && ebQ20.EnergyShield == 0, "no ES after Eldritch Battery");
            Assert(Math.Abs(plain.EnergyShield - Round2(esBase * 1.2m)) <= 1m,
                "quality-lifted ES " + plain.EnergyShield + " expected " + Round2(esBase * 1.2m));
            // Isolate the quality contribution: the same EB build at 0% vs 20% quality must differ
            // by exactly the quality-scoped share of the converted Energy Shield.
            Assert(Math.Abs((ebQ20.Mana - ebQ0.Mana) - Round2(esBase * 0.2m)) <= 1m,
                "quality-converted mana share " + (ebQ20.Mana - ebQ0.Mana) + " expected " + Round2(esBase * 0.2m));
            Assert(ebQ20.Mana > ebQ0.Mana, "quality raises converted mana " + ebQ20.Mana + " <= " + ebQ0.Mana);
        }));

        await test("Calc: local Ward increases stay item-local and global Ward increases stay global", () => Task.Run(() =>
        {
            var bucket = new StatBucket();
            var item = new ItemContext();
            StatInterpreter.Apply(bucket, "base_maximum_ward", 209, null);
            StatInterpreter.Apply(bucket, "local_ward_+%", 25, item);
            StatInterpreter.Apply(bucket, "maximum_ward_+%", 10, null);
            Assert(bucket.WardFlat == 209 && bucket.WardInc == 10 && item.WardInc == 25,
                $"ward scoping flat={bucket.WardFlat} globalInc={bucket.WardInc} localInc={item.WardInc}");
        }));

        await test("Calc: Eldritch Battery doubles displayed mana costs of a linked spell", () => Task.Run(() =>
        {
            var gem = Catalog.Value.Gems.Values.First(g => g.Name == "Fireball");
            var skill = gem.Skill!;
            var cost = skill.Costs["1"]["Mana"];
            var plain = BuildDocument.Create("Spell") with { Level = 1, Skills = new() { Groups = [GemGroup(gem.Id, "Fireball")] } };
            var converted = BuildDocument.Create("Spell") with
            {
                Level = 1,
                Skills = new() { Groups = [GemGroup(gem.Id, "Fireball")] },
                Tree = new PassiveTreePlan() with { AllocatedNodes = [57513] }
            };
            var baseInfo = CharacterCalculator.Calculate(plain, Tree.Value, StatMap.Value, Catalog.Value).Skills.First();
            var batteried = CharacterCalculator.Calculate(converted, Tree.Value, StatMap.Value, Catalog.Value).Skills.First();
            Assert(baseInfo.ManaCost == cost && batteried.ManaCost == cost * 2,
                $"mana cost {baseInfo.ManaCost} -> {batteried.ManaCost} expected {cost} -> {cost * 2}");
        }));

        await test("Calc: UniqueTextParser maps exact English unique modifier lines to stat ids", () => Task.Run(() =>
        {
            var parsed = UniqueTextParser.ParseMods(
                "Rarity: Unique\n--------\n+60 to maximum Mana\n25% increased maximum Mana\n+30 to maximum Life\n" +
                "+15 to all Attributes\n+20% to Fire Resistance\n12% increased Fire Damage\n15% increased Attack Speed\n" +
                "Adds 10 to 15 Fire Damage\nRegenerate 0.5% of maximum Life per second\n--------\nCorrupted");
            var map = parsed.ToDictionary(p => p.Id, p => p.Value);
            Assert(map.TryGetValue("base_maximum_mana", out var mana) && mana == 60, "flat mana");
            Assert(map["maximum_mana_+%"] == 25, "percent mana");
            Assert(map["base_maximum_life"] == 30, "flat life");
            Assert(map["additional_all_attributes"] == 15, "all attributes");
            Assert(map["base_fire_damage_resistance_%"] == 20, "fire resistance");
            Assert(map["fire_damage_+%"] == 12, "fire damage increased");
            Assert(map["attack_speed_+%"] == 15, "attack speed");
            Assert(map["attack_minimum_added_fire_damage"] == 10 && map["attack_maximum_added_fire_damage"] == 15,
                "added fire range");
            Assert(map["life_regeneration_percent_per_second"] == 0.5m, "life regen percent");
            Assert(parsed.Count == 10, "parsed line count " + parsed.Count);
        }));

        await test("Calc: imported unique text modifiers lift Mana through the character summary", () => Task.Run(() =>
        {
            var ring = new GearItem { BaseId = "", Rarity = "unique", Name = "Mana Ring",
                Notes = "Rarity: Unique\nItem Class: Rings\n--------\n+60 to maximum Mana\n25% increased maximum Mana\n--------" };
            var plain = BuildDocument.Create("U") with { Level = 1 };
            var withUnique = BuildDocument.Create("U") with
            {
                Level = 1,
                Equipment = new() { WeaponSet = 1, Items = [ring], Slots = new() { ["Ring1"] = ring.Id } }
            };
            var s0 = CharacterCalculator.Calculate(plain, Tree.Value, StatMap.Value, Catalog.Value);
            var s1 = CharacterCalculator.Calculate(withUnique, Tree.Value, StatMap.Value, Catalog.Value);
            Assert(s0.Extras.GetValueOrDefault("UniqueTextMods") == 0, "plain has no unique weirdness");
            Assert(s1.Mana > s0.Mana, "unique mana raised " + s0.Mana + " -> " + s1.Mana);
            Assert(Math.Abs(s1.Mana - (s0.Mana + 60m) * 1.25m) <= 2m,
                "unique mana math " + s0.Mana + " -> " + s1.Mana);
            Assert(s1.Extras.GetValueOrDefault("UniqueTextMods") == 2, "unique mod count tracked " + s1.Extras.GetValueOrDefault("UniqueTextMods"));
        }));

        await test("Calc: reverse stat text table loads and matches generic English unique lines", () => Task.Run(() =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Data", "Game", "stat_text_reverse.json");
            ReverseStatTextMatcher.UseFile(path);
            Assert(ReverseStatTextMatcher.IsReady, "reverse table ready");

            var added = ReverseStatTextMatcher.TryMatch("Adds 32 to 48 Physical Damage");
            Assert(added is not null && added.Count == 2 &&
                   added[0].Id == "global_minimum_added_physical_damage" && added[0].Value == 32 &&
                   added[1].Id == "global_maximum_added_physical_damage" && added[1].Value == 48,
                "reverse Adds pair " + (added is null ? "null" : string.Join(",", added.Select(a => a.Id + "=" + a.Value))));

            var speed = ReverseStatTextMatcher.TryMatch("15% increased Attack Speed");
            Assert(speed is not null && speed.Count == 1 && speed[0].Value == 15, "reverse speed " + (speed is null ? "null" : speed[0].Id));

            Assert(ReverseStatTextMatcher.TryMatch("Rarity: Unique") is null, "header not matched by reverse");
        }));

        await test("Calc: added-damage ids route to their damage type after the AddPair fix", () => Task.Run(() =>
        {
            var bucket = new StatBucket();
            StatInterpreter.Apply(bucket, "attack_minimum_added_physical_damage", 10, null);
            StatInterpreter.Apply(bucket, "attack_maximum_added_physical_damage", 20, null);
            StatInterpreter.Apply(bucket, "global_minimum_added_fire_damage", 5, null);
            StatInterpreter.Apply(bucket, "global_maximum_added_fire_damage", 9, null);
            StatInterpreter.Apply(bucket, "spell_minimum_added_cold_damage", 3, null);
            StatInterpreter.Apply(bucket, "spell_maximum_added_cold_damage", 7, null);
            Assert(bucket.AddedAttackMin["physical"] == 10 && bucket.AddedAttackMax["physical"] == 20 &&
                   bucket.AddedAttackMin["fire"] == 5 && bucket.AddedAttackMax["fire"] == 9 &&
                   bucket.AddedSpellMin["cold"] == 3 && bucket.AddedSpellMax["cold"] == 7,
                $"added damage routing phys={bucket.AddedAttackMin.GetValueOrDefault("physical")}/{bucket.AddedAttackMax.GetValueOrDefault("physical")} " +
                $"fire={bucket.AddedAttackMin.GetValueOrDefault("fire")}/{bucket.AddedAttackMax.GetValueOrDefault("fire")} " +
                $"spell cold={bucket.AddedSpellMin.GetValueOrDefault("cold")}/{bucket.AddedSpellMax.GetValueOrDefault("cold")}");
        }));

        await test("Calc: reverse translation enriches UniqueTextParser with generic lines", () => Task.Run(() =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Data", "Game", "stat_text_reverse.json");
            ReverseStatTextMatcher.UseFile(path);
            var parsed = UniqueTextParser.ParseMods("Rarity: Unique\n--------\n18% increased Projectile Damage\nAdds 10 to 15 Fire Damage\n--------");
            var map = parsed.ToDictionary(p => p.Id, p => p.Value);
            Assert(map.ContainsKey("projectile_damage_+%") && map["projectile_damage_+%"] == 18,
                "reverse projectile damage " + (map.ContainsKey("projectile_damage_+%") ? map["projectile_damage_+%"].ToString() : "absent"));
            Assert(map["attack_minimum_added_fire_damage"] == 10 && map["attack_maximum_added_fire_damage"] == 15,
                "regex Added line still deterministic");
        }));
    }
}
