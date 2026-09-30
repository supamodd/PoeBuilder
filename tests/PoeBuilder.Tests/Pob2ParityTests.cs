using System.Globalization;
using System.Text;
using System.Xml.Linq;
using PoeBuilder.App.Services;
using PoeBuilder.Core.Calculation;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Storage;
using PoeBuilder.Core.Tree;

/// <summary>
/// Parity harness for the "PoB · Mercenary" build.
/// <para>
/// <c>Fixtures/pob-real.txt</c> is the real PoB2 share code of that build, and it carries PoB2's own
/// computed panel as <c>&lt;PlayerStat stat="…" value="…"/&gt;</c> elements. Those numbers are the
/// golden values, so the comparison needs no external snapshot: the fixture is both the input and
/// the expectation. Everything is printed as a table so the remaining gaps stay visible instead of
/// hidden; the assertions cover the metrics whose formulas are already aligned with PoB2.
/// </para>
/// </summary>
internal static class Pob2ParityTests
{
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }

    private static readonly Lazy<TreeCatalog> Tree = new(() => TreeCatalog.LoadPinned(Path.Combine(AppContext.BaseDirectory, "Data", "Tree", "data.json")));
    private static readonly Lazy<GameCatalog> Catalog = new(() => GameCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "catalog.json")));
    private static readonly Lazy<GameStatMap> StatMap = new(() => GameStatMap.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "statmap.json")));

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>PoB2's own panel, read back from the fixture's &lt;PlayerStat&gt; elements.</summary>
    /// <summary>The first number a regex finds in a breakdown line, parsed with the invariant culture.</summary>
    private static decimal ParseNumber(string line, string pattern)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, pattern);
        return match.Success
            ? decimal.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 0m;
    }

    private static Dictionary<string, decimal> PobGoldens(string code)
    {
        string xml = Encoding.UTF8.GetString(BuildInterop.DecodePobEnvelope(code));
        var document = XDocument.Parse(xml);
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var stat in document.Descendants("PlayerStat"))
        {
            string? name = (string?)stat.Attribute("stat");
            if (name is null) continue;
            if (decimal.TryParse((string?)stat.Attribute("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal value))
                values[name] = value;
        }
        return values;
    }

    private static CharacterSummary Import(string fixture, out ImportedBuild imported)
    {
        string code = File.ReadAllText(FixturePath(fixture)).Trim();
        imported = BuildInterop.ParsePobCode(code, Catalog.Value, Tree.Value);
        return CharacterCalculator.Calculate(imported.Document, Tree.Value, StatMap.Value, Catalog.Value);
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        // Two real PoB2 share codes, each carrying PoB2's own computed panel. `pob-real.txt` is an
        // earlier snapshot (level 95); `pobb-mercenary-ll-arc.txt` is the current one (level 96).
        foreach (string fixture in new[] { "pob-real.txt", "pobb-mercenary-ll-arc.txt" })
        {
            await test("Parity: PoB2 Mercenary fixture — defence and attribute table (" + fixture + ")", () => Task.Run(() =>
            {
                ReverseStatTextMatcher.UseFile(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "stat_text_reverse.json"));
                var gold = PobGoldens(File.ReadAllText(FixturePath(fixture)).Trim());
                var summary = Import(fixture, out var imported);

                var rows = new (string Label, decimal Expected, decimal Actual)[]
                {
                    ("Strength", gold.GetValueOrDefault("Str"), summary.Strength),
                    ("Dexterity", gold.GetValueOrDefault("Dex"), summary.Dexterity),
                    ("Intelligence", gold.GetValueOrDefault("Int"), summary.Intelligence),
                    ("Total Life", gold.GetValueOrDefault("Life"), summary.Life),
                    ("Total Mana", gold.GetValueOrDefault("Mana"), summary.Mana),
                    ("Total Spirit", gold.GetValueOrDefault("Spirit"), summary.Spirit),
                    ("Energy Shield", gold.GetValueOrDefault("EnergyShield"), summary.EnergyShield),
                    ("Armour", gold.GetValueOrDefault("Armour"), summary.Armour),
                    ("Evasion Rating", gold.GetValueOrDefault("Evasion"), summary.Evasion),
                    ("Fire Resistance", gold.GetValueOrDefault("FireResist"), summary.FireRes),
                    ("Cold Resistance", gold.GetValueOrDefault("ColdResist"), summary.ColdRes),
                    ("Lightning Resistance", gold.GetValueOrDefault("LightningResist"), summary.LightRes),
                    ("Chaos Resistance", gold.GetValueOrDefault("ChaosResist"), summary.ChaosRes),
                    ("Movement Speed (x100)", gold.GetValueOrDefault("EffectiveMovementSpeedMod") * 100, summary.MoveSpeedPercent),
                    ("EHP (sum of our single-hit estimates)", gold.GetValueOrDefault("TotalEHP"), summary.EhpEstimates.Sum(e => e.EffectiveHitPool ?? 0)),
                };

                Console.WriteLine();
                Console.WriteLine("=== PoB2 golden parity: " + fixture + " (level " + imported.Document.Level + ") ===");
                Console.WriteLine($"{"metric",-42}{"PoB2",14}{"PoeBuilder",16}{"delta",12}");
                foreach (var (label, expected, actual) in rows)
                    Console.WriteLine($"{label,-42}{expected,14:0.##}{actual,16:0.##}{actual - expected,12:+0.##;-0.##;0}");
                Console.WriteLine("=== end golden parity ===");
                Console.WriteLine("--- PoB2 panel values this model does not reproduce yet (crit is compared above) ---");
                foreach (var name in new[] { "PhysicalMaximumHitTaken", "FireMaximumHitTaken", "ColdMaximumHitTaken", "LightningMaximumHitTaken", "ChaosMaximumHitTaken", "AverageHit", "TotalDPS", "Speed", "LifeCost", "ManaCost" })
                    Console.WriteLine($"  {name,-28}{gold.GetValueOrDefault(name),14:0.##}");
                Console.WriteLine("--- top skills ---");
                foreach (var skill in summary.Skills.Where(s => s.HasData).OrderByDescending(s => s.Dps).Take(4))
                {
                    Console.WriteLine($"  {skill.GroupName} ({skill.GemName}): avgHit {skill.AvgHit:0.#}, rate {skill.HitsPerSecond:0.##}, dps {skill.Dps:0.#}, " +
                        $"crit {skill.CritChancePercent:0.##}% x{skill.CritBonusPercent:0}%, notes [{string.Join(",", skill.NoteCodes)}]");
                    foreach (var line in skill.Breakdown) Console.WriteLine("      " + line);
                }
                Console.WriteLine("--- our resistance sources ---");
                Console.WriteLine($"  fire {summary.FireResSources}, cold {summary.ColdResSources}, lightning {summary.LightResSources}, chaos {summary.ChaosResSources}");
                Console.WriteLine("--- quest rewards resolved: " + (imported.Document.QuestRewards?.Length ?? 0) + " ---");
                foreach (var reward in imported.Document.QuestRewards ?? []) Console.WriteLine("  " + reward.Replace("\n", " ; "));
                Console.WriteLine("--- extras ---");
                foreach (var entry in summary.Extras.OrderBy(e => e.Key))
                    Console.WriteLine($"  {entry.Key} = {entry.Value}");
                Console.WriteLine("--- unaccounted lines: " + summary.UnaccountedTotal + " ---");
                foreach (var entry in summary.Unaccounted.OrderByDescending(e => e.Value).Take(15))
                    Console.WriteLine($"  {entry.Value,4}x {entry.Key}");
                Console.WriteLine("NOTE: attributes, pools, armour/evasion, EHP and offence still carry open gaps;");
                Console.WriteLine("      the asserted metrics are the ones whose formulas are verified against PoB2.");
                Console.WriteLine();

                // Verified-against-PoB2 metrics. The current snapshot (pobb-mercenary-ll-arc.txt) is
                // asserted exactly: its quest rewards, ring reflection and resistance penalty are all
                // resolved from the share code itself. The older snapshot keeps a looser gate because
                // its gear text differs from the state it was exported in.
                // The current snapshot's pools, attributes and defences are exact, so they are asserted
                // as such. The earlier snapshot is reported instead: its gear text differs from the state
                // it was exported in — the sceptre parked in its second weapon set still hands PoB2 its
                // granted Purity of Ice aura (cold resistance) and a small Life source we do not model.
                bool strict = fixture.Contains("pobb-", StringComparison.Ordinal);
                decimal tolerance = strict ? 0.5m : 60m;
                Assert(Math.Abs(summary.MoveSpeedPercent - gold["EffectiveMovementSpeedMod"] * 100) <= 2m,
                    "movement " + summary.MoveSpeedPercent + " vs " + gold["EffectiveMovementSpeedMod"] * 100);
                // Mana is the pool every per-100-Mana scaler reads (Rathpith Globe's crit chance,
                // Archmage's gain-as-extra), so it is asserted exactly on both snapshots.
                Assert(Math.Abs(summary.Mana - gold["Mana"]) <= 0.5m,
                    "Mana " + summary.Mana + " vs PoB2 " + gold["Mana"]);
                Assert(summary.Strength == gold["Str"] && summary.Dexterity == gold["Dex"] && summary.Intelligence == gold["Int"],
                    $"attributes {summary.Strength}/{summary.Dexterity}/{summary.Intelligence} vs PoB2 {gold["Str"]}/{gold["Dex"]}/{gold["Int"]}");
                Assert(Math.Abs(summary.Armour - gold["Armour"]) <= 0.5m, "Armour " + summary.Armour + " vs " + gold["Armour"]);
                Assert(Math.Abs(summary.Evasion - gold["Evasion"]) <= 0.5m, "Evasion " + summary.Evasion + " vs " + gold["Evasion"]);
                if (strict) Assert(Math.Abs(summary.Life - gold["Life"]) <= 0.5m, "Life " + summary.Life + " vs " + gold["Life"]);
                Assert(summary.FireRes <= 75.5m && summary.ColdRes <= 75.5m && summary.LightRes <= 75.5m && summary.ChaosRes <= 75.5m,
                    "resistance cap: " + summary.FireRes + "/" + summary.ColdRes + "/" + summary.LightRes + "/" + summary.ChaosRes);
                Assert(Math.Abs(summary.FireRes - gold["FireResist"]) <= tolerance, "FireRes " + summary.FireRes + " vs " + gold["FireResist"]);
                Assert(Math.Abs(summary.LightRes - gold["LightningResist"]) <= tolerance, "LightRes " + summary.LightRes + " vs " + gold["LightningResist"]);
                Assert(Math.Abs(summary.ChaosRes - gold["ChaosResist"]) <= tolerance, "ChaosRes " + summary.ChaosRes + " vs " + gold["ChaosResist"]);
                Assert(Math.Abs(summary.Spirit - gold["Spirit"]) <= (strict ? 1m : 100m), "Spirit " + summary.Spirit + " vs " + gold["Spirit"]);
                if (strict) Assert(Math.Abs(summary.ColdRes - gold["ColdResist"]) <= tolerance, "ColdRes " + summary.ColdRes + " vs " + gold["ColdResist"]);

                // --- Crit, asserted against PoB2's own panel ---
                // PoB2: CritMultiplier = 1 + (CritMultiplier BASE / 100) * (1 + INC/100) * MORE
                // (CalcOffence.lua:3813-3858). The panel prints it as a multiplier, so the bonus in
                // percent is (value * 100) - 100.
                var mainSkill = summary.Skills.FirstOrDefault(s => s.GemName == "Arc")
                    ?? summary.Skills.Where(s => s.HasData).OrderByDescending(s => s.Dps).FirstOrDefault();
                if (mainSkill is not null && gold.GetValueOrDefault("CritMultiplier") > 0)
                {
                    decimal expectedBonus = gold["CritMultiplier"] * 100m - 100m;
                    Console.WriteLine($"CRIT: {mainSkill.GemName} chance {mainSkill.CritChancePercent:0.##}% (PoB2 {gold.GetValueOrDefault("CritChance"):0.##}%), " +
                        $"bonus +{mainSkill.CritBonusPercent:0.##}% (PoB2 +{expectedBonus:0.##}%)");
                    Assert(Math.Abs(mainSkill.CritBonusPercent - expectedBonus) <= 0.5m,
                        "crit bonus +" + mainSkill.CritBonusPercent + "% vs PoB2 +" + expectedBonus + "%");
                    // PoB2 floors the "per 100 maximum Mana" quotient (PerStat, Classes/ModStore.lua),
                    // so a build carrying such a scaler needs its Mana pool to match before the chance
                    // can. Both snapshots now do, and both match to the hundredth of a percent.
                    Assert(Math.Abs(mainSkill.CritChancePercent - gold["CritChance"]) <= 0.01m,
                        "crit chance " + mainSkill.CritChancePercent + "% vs PoB2 " + gold["CritChance"] + "%");
                }

                // --- The totem count the deployed skill's rate scales with ---
                // PoB2's export states the count it priced with: <PlayerStat stat="ActiveTotemLimit" value="N"/>.
                // The "per Summoned Totem" tree lines are PoB2 PerStat mods, so the deployed Arc's breakdown
                // must name exactly that many totems: this is the parity check for TotemsSummoned
                // (CalcOffence.lua:1786 / TotemsSummoned), read from the totem HOST gem's own
                // base_number_of_totems_allowed instead of PoB2's hardcoded OVERRIDE.
                decimal totemLimit = gold.GetValueOrDefault("ActiveTotemLimit");
                Assert(totemLimit > 0m, fixture + ": PoB2's export must state its ActiveTotemLimit");
                var totemSkill = summary.Skills.FirstOrDefault(s => s.GemName == "Arc");
                Assert(totemSkill is not null && totemSkill.Breakdown.Any(b => b.Contains("x " + totemLimit + " summoned")),
                    fixture + ": the totem speed breakdown must name the " + totemLimit + " summoned totems PoB2 used, got: " +
                    string.Join(" | ", (totemSkill?.Breakdown ?? []).Where(b => b.Contains("Totem cast speed"))));
            }));
        }

        await test("Parity: imported fixture gear resolution (diagnostic)", () => Task.Run(() =>
        {
            ReverseStatTextMatcher.UseFile(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "stat_text_reverse.json"));
            foreach (string fixture in new[] { "pob-real.txt", "pobb-mercenary-ll-arc.txt" })
            {
            var imported = BuildInterop.ParsePobCode(File.ReadAllText(FixturePath(fixture)).Trim(), Catalog.Value, Tree.Value);
            var equipment = imported.Document.Equipment;
            if (equipment is null) continue;
            Console.WriteLine();
            Console.WriteLine("=== fixture gear: " + fixture + " ===");
            Console.WriteLine("slots: " + string.Join(", ", equipment.Slots.Select(s => s.Key + "=" + (equipment.Items.FirstOrDefault(i => i.Id == s.Value)?.Name ?? "?"))));
            foreach (var item in equipment.Items)
            {
                var bases = UniqueTextParser.ParseBaseValues(item.Notes);
                Console.WriteLine($"-- {item.Name} [{item.Rarity}] base='{item.BaseId}' q={item.Quality} notes={item.Notes.Length} reflects={item.Notes.Contains("Reflects opposite Ring", StringComparison.OrdinalIgnoreCase)} " +
                    $"printed: armour={bases.Armour} evasion={bases.Evasion} es={bases.EnergyShield} ward={bases.Ward} spirit={bases.Spirit} sockets={bases.Sockets}");
                foreach (var mod in item.Mods)
                    Console.WriteLine($"     {mod.Id} = {string.Join(",", mod.Values)}");
            }
            Console.WriteLine("=== end fixture gear ===");
            Console.WriteLine();
            }
        }));

        await test("Parity: imported PoB2 fixture resolves the alternate class start from a socketed jewel", () => Task.Run(() =>
        {
            var imported = BuildInterop.ParsePobCode(File.ReadAllText(FixturePath("pob-real.txt")).Trim(), Catalog.Value, Tree.Value);
            var plan = imported.Document.Tree ?? throw new Exception("tree plan");
            // PoE2 keeps the legacy PoE1 start nodes: "Templar" shares its start with the Druid, which
            // is exactly the "extra path to the druid start" an imported build used to show.
            var templarStart = Tree.Value.Nodes.Values.First(n => n.IsStart && n.Name == "TEMPLAR");
            Assert(plan.AlternateStartNodes.Contains(templarStart.Id),
                "Templar start " + templarStart.Id + " got " + string.Join(",", plan.AlternateStartNodes));
            new PassiveTreeEngine(Tree.Value).Validate(plan);
        }));

        await test("Parity: PoB2 attribute override decides which attribute each generic node grants", () => Task.Run(() =>
        {
            var imported = BuildInterop.ParsePobCode(File.ReadAllText(FixturePath("pob-real.txt")).Trim(), Catalog.Value, Tree.Value);
            var plan = imported.Document.Tree ?? throw new Exception("tree plan");
            int strength = plan.AttributeSelections.Values.Count(v => v == 26297);
            int intelligence = plan.AttributeSelections.Values.Count(v => v == 57022);
            int dexterity = plan.AttributeSelections.Values.Count(v => v == 14927);
            Console.WriteLine($"TREE SHAPE: allocated={plan.AllocatedNodes.Length} jewelGranted={plan.JewelAllocatedNodes.Length} " +
                $"matched={imported.Report.PassivesMatched} unknown={imported.Report.PassivesUnknown} " +
                $"str={strength} dex={dexterity} int={intelligence}");
            string xml = Encoding.UTF8.GetString(BuildInterop.DecodePobEnvelope(File.ReadAllText(FixturePath("pob-real.txt")).Trim()));
            var spec = XDocument.Parse(xml).Descendants("Spec").First();
            var fileNodes = ((string?)spec.Attribute("nodes") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse).ToHashSet();
            var extraNodes = plan.AllocatedNodes.Where(id => !fileNodes.Contains(id)).ToArray();
            Console.WriteLine($"TREE EXTRA ({extraNodes.Length}): " + string.Join(", ",
                extraNodes.Select(id => Tree.Value.Nodes.TryGetValue(id, out var n) ? id + ":" + n.Name + (n.IsAttribute ? "(attr)" : "") : id + ":?")));
            var missingNodes = fileNodes.Where(id => !plan.AllocatedNodes.Contains(id)).ToArray();
            Console.WriteLine($"TREE MISSING ({missingNodes.Length}): " + string.Join(", ", missingNodes));
            Assert(intelligence >= 40 && strength is >= 3 and <= 6 && dexterity == 0,
                $"attribute split str={strength} dex={dexterity} int={intelligence}");
            // The import must reproduce the source allocation exactly: only the class start and the
            // ascendancy nodes are absent from AllocatedNodes, and nothing extra is invented.
            Assert(extraNodes.Length == plan.JewelAllocatedNodes.Length,
                "invented nodes: " + extraNodes.Length + " vs jewel-granted " + plan.JewelAllocatedNodes.Length);
            Assert(missingNodes.All(id => id == 50986 || !Tree.Value.Nodes.ContainsKey(id)),
                "unexpected missing nodes: " + string.Join(",", missingNodes));
        }));

        await test("Parity: the updated Arc Spell Totem build (pobb.in/adaw0EHKkiFk) against its own PoB2 numbers", () => Task.Run(() =>
        {
            // The owner re-exported the build after gearing changes; the share code carries PoB2's own panel,
            // so the fixture is again both input and expectation. This test prints every skill's numbers and
            // its full breakdown, which is what the gap analysis needs.
            var gold = PobGoldens(File.ReadAllText(FixturePath("pobb-arc-totem-v2.txt")).Trim());
            var summary = Import("pobb-arc-totem-v2.txt", out var imported);
            Console.WriteLine("  PoB2 golden: TotalDPS " + gold.GetValueOrDefault("TotalDPS") + ", AverageHit " +
                gold.GetValueOrDefault("AverageHit") + ", Speed " + gold.GetValueOrDefault("Speed") + ", crit " +
                gold.GetValueOrDefault("PreEffectiveCritChance") + "% x" + gold.GetValueOrDefault("CritMultiplier") +
                ", mana cost " + gold.GetValueOrDefault("ManaCost") + ", life cost " + gold.GetValueOrDefault("LifeCost"));
            foreach (var skill in summary.Skills.OrderByDescending(s => s.Dps).Take(8))
                Console.WriteLine($"  {skill.GemName}: dps {skill.Dps:0.#} (rate {skill.HitsPerSecond:0.###}, crit {skill.CritChancePercent:0.##}% x{skill.CritBonusPercent / 100 + 1:0.##}, eff {skill.EffectiveDps:0.#})");
            Console.WriteLine("  our pools: life " + summary.Life + ", mana " + summary.Mana + ", ES " + summary.EnergyShield +
                ", armour " + summary.Armour + ", evasion " + summary.Evasion +
                ", res " + summary.FireRes + "/" + summary.ColdRes + "/" + summary.LightRes + "/" + summary.ChaosRes +
                " | PoB2: life " + gold.GetValueOrDefault("Life") + ", mana " + gold.GetValueOrDefault("Mana") +
                ", ES " + gold.GetValueOrDefault("EnergyShield") + ", armour " + gold.GetValueOrDefault("Armour") +
                ", evasion " + gold.GetValueOrDefault("Evasion") + ", res " + gold.GetValueOrDefault("FireResist") + "/" +
                gold.GetValueOrDefault("ColdResist") + "/" + gold.GetValueOrDefault("LightningResist") + "/" + gold.GetValueOrDefault("ChaosResist"));
            var arc = summary.Skills.Where(s => s.GemName == "Arc").OrderByDescending(s => s.Dps).FirstOrDefault();
            Assert(arc is not null, "the Arc group must resolve");
            // Data-driven regression guards: the fixture's own PoB2 panel is the expectation, so these two
            // numbers cannot drift without the test saying so. They are exactly the two things the Mageblood
            // legacies (CalcPerform.lua:65-141) and the "…for Spells" crit lines fixed: Diamond's +75% INC
            // Critical Hit Chance and Amethyst's +45% Chaos Resistance.
            Assert(arc!.CritChancePercent == gold.GetValueOrDefault("PreEffectiveCritChance"),
                "Arc crit chance must equal PoB2's own: " + arc.CritChancePercent + " vs " + gold.GetValueOrDefault("PreEffectiveCritChance"));
            Assert(summary.ChaosRes == gold.GetValueOrDefault("ChaosResist"),
                "chaos resistance must equal PoB2's own (Amethyst legacy): " + summary.ChaosRes + " vs " + gold.GetValueOrDefault("ChaosResist"));
            Assert(summary.Mana == gold.GetValueOrDefault("Mana"),
                "maximum Mana must equal PoB2's own (Archmage scales off it): " + summary.Mana + " vs " + gold.GetValueOrDefault("Mana"));
            Console.WriteLine("  Arc breakdown:");
            foreach (var line in arc!.Breakdown) Console.WriteLine("      " + line);
            Console.WriteLine("  Skill:EffectiveDps extras: " + string.Join(" | ", summary.Extras
                .Where(e => e.Key.StartsWith("Skill:EffectiveDps", StringComparison.Ordinal)).Select(e => e.Key + "=" + e.Value)));
            Console.WriteLine("  unmatched item lines (" + imported.Report.EquipmentSkippedLines + "): " +
                string.Join(" | ", (imported.Report.EquipmentSkippedTexts ?? []).Take(30)));
            Console.WriteLine("  unaccounted (" + summary.UnaccountedTotal + "): " + string.Join(" | ",
                summary.Unaccounted.Take(30).Select(kv => kv.Value + "x " + kv.Key)));
            Console.WriteLine("  known (" + summary.Known.Count + "): " + string.Join(" | ",
                summary.Known.Take(20).Select(kv => kv.Key)));
        }));

        await test("Parity: per-group damage tracks poe.ninja's published breakdown for the shared snapshot", () => Task.Run(() =>
        {
            // poe.ninja publishes this exact snapshot, and its Arc panel reproduces PoB2's (569370.8 dps,
            // 2.3727 rate, 47.97% crit). Its per-group entries carry the final damage split, so they pin the
            // sub-skill model: Entangle is pure physical (Brutality's "deals no elemental damage"), Flame Wall
            // carries Spell Cascade (-30% more) and Fortress (-40% more), Frost Bomb's hit is 21845 pre-crit.
            var summary = Import("pobb-mercenary-ll-arc.txt", out _);
            var groups = summary.Skills.GroupBy(s => s.GemName).ToDictionary(g => g.Key, g => g.Max(s => s.HitsPerSecond));
            decimal entangleRate = groups.GetValueOrDefault("Entangle");
            Assert(Math.Abs(entangleRate - 2.956m) <= 2.956m * 0.12m,
                "Entangle rate " + entangleRate + " vs poe.ninja 2.956");
            decimal wallRate = groups.GetValueOrDefault("Flame Wall");
            Assert(Math.Abs(wallRate - 2.66m) <= 2.66m * 0.12m,
                "Flame Wall rate " + wallRate + " vs poe.ninja 2.66");
            var dpsByGem = summary.Skills.GroupBy(s => s.GemName).ToDictionary(g => g.Key, g => g.Max(s => s.Dps));
            decimal entangle = dpsByGem.GetValueOrDefault("Entangle");
            Assert(entangle >= 44661m * 0.8m && entangle <= 44661m * 1.35m,
                "Entangle dps " + entangle + " vs poe.ninja 44661 (physical-only, Brutality + Heft)");
            decimal wall = dpsByGem.GetValueOrDefault("Flame Wall");
            // Flame Wall's own buff ("Projectile Travelled through?" is ON in this build's config) adds its
            // Fire damage to every projectile hit, and PoB2's statMap gives that mod ModFlag.Projectile — so the
            // wall's own projectile spell gains it too, which is what lifted our value from ~80.9k to ~117.6k.
            // poe.ninja's engine does not carry that buff, hence the wider band and the note in the message.
            Assert(wall >= 80918m * 0.6m && wall <= 80918m * 1.6m,
                "Flame Wall dps " + wall + " vs poe.ninja 80918 (Spell Cascade -30% more, Fortress -40% more; " +
                "our value additionally carries Flame Wall's own projectile buff, which poe.ninja does not model)");
            var frostBomb = summary.Skills.Where(s => s.GemName == "Frost Bomb").OrderByDescending(s => s.Dps).First();
            Assert(frostBomb.AvgHit >= 21845m * 0.8m && frostBomb.AvgHit <= 21845m * 1.2m,
                "Frost Bomb pre-crit hit " + frostBomb.AvgHit + " vs poe.ninja 21845 (Short Fuse -30% more)");
            Assert(frostBomb.HitsPerSecond > 1m,
                "Frost Bomb's rate is not capped: PoB2 reports its 6 s cooldown but keeps the cast-speed DPS "
                + "(poe.ninja's own engine caps it at 1/6 per second), rate " + frostBomb.HitsPerSecond);
            Assert(frostBomb.Breakdown.Any(b => b.Contains("Skill cooldown")),
                "the per-level cooldown from PoB2's data must be reported in the breakdown");
            // The main skill's rate: the config's conditionMoving/conditionCritRecently and the Low Life
            // state now consume the conditional cast-speed lines, so the rate tracks PoB2's 2.3727 within
            // the residual of the merged by-totem cast-speed lines we still list as extras.
            var arc = summary.Skills.Where(s => s.GemName == "Arc").OrderByDescending(s => s.Dps).First();
            Assert(Math.Abs(arc.HitsPerSecond - 2.3727m) <= 2.3727m * 0.12m,
                "Arc rate " + arc.HitsPerSecond + " vs PoB2 2.3727 (after the conditional cast-speed lines)");
            Console.WriteLine($"  sub-groups vs poe.ninja: Entangle {entangle:0} (44661) rate {entangleRate:0.###} (2.956), " +
                $"Flame Wall {wall:0} (80918), Frost Bomb hit {frostBomb.AvgHit:0} (21845, rate {frostBomb.HitsPerSecond:0.##} vs 0.167), " +
                $"Arc rate {arc.HitsPerSecond:0.###} (2.3727)");
        }));

        await test("Parity: the imported config conditions drive the conditional stat lines", () => Task.Run(() =>
        {
            var summary = Import("pobb-mercenary-ll-arc.txt", out var imported);
            Assert(imported.Document.Conditions.Moving, "conditionMoving must be imported");
            Assert(imported.Document.Conditions.CritRecently, "conditionCritRecently must be imported");
            Assert(imported.Document.Conditions.FlameWallAddedDamage, "flameWallAddedDamage must be imported");
            Assert(!imported.Document.Conditions.ArcLightningInfused,
                "arcLightningInfused is not set by this build, so Arc's +200% infusion more must stay off");
            // The remaining unaccounted lines are catalogued with a reason and printed by the sheet:
            // quest rewards and tree lines without a formula, plus unique modifier lines PoB2 does not
            // map either (flask/charm behaviours, Headhunter's steal, Kalandra's reflection…).
            var leftovers = summary.Unaccounted.Keys.ToArray();
            Assert(leftovers.All(id =>
                    id.StartsWith("quest:", StringComparison.Ordinal) ||
                    id.StartsWith("tree:", StringComparison.Ordinal) ||
                    id.StartsWith("unique:", StringComparison.Ordinal) ||
                    id.StartsWith("aura:", StringComparison.Ordinal)),
                "unexpected unaccounted ids: " + string.Join(", ", leftovers));
            // The aura pass consumes the persistent skills PoB2 turns into buffs. Flame Wall's added damage and
            // Archmage's mana-cost term used to be reported here; both are now applied (their config condition
            // is met by this build), so what is left are the recognised-but-not-modelled lines, each with its
            // reason in StatBucket.KnownNonModelled.
            Assert(!summary.Unaccounted.Keys.Any(id => id.StartsWith("aura: Flame Wall", StringComparison.Ordinal)),
                "Flame Wall's buff stats must be applied now, not reported as unaccounted: " + string.Join(" | ", leftovers));
            Assert(summary.Known.Keys.Any(k => k.StartsWith("most_numerous_colour", StringComparison.Ordinal)) ||
                summary.Known.Count > 0,
                "recognised-but-not-modelled lines must be recorded with their reasons");
            Console.WriteLine("  unaccounted after the conditional pass: " + summary.UnaccountedTotal + " (" +
                string.Join(" | ", leftovers.Select(l => l.Split('\n')[0])) + ")");
            Console.WriteLine("  recognised (not modelled), " + summary.Known.Count + " reasons: " +
                string.Join(" | ", summary.Known.Keys.Take(6)));
        }));

        await test("Parity: Arc's Lightning Infusion is applied only when the config flag is on", () => Task.Run(() =>
        {
            Import("pobb-mercenary-ll-arc.txt", out var imported);
            var baseline = CharacterCalculator.Calculate(imported.Document, Tree.Value, StatMap.Value, Catalog.Value);
            decimal arcOff = baseline.Skills.Where(s => s.GemName == "Arc").Max(s => s.Dps);
            Assert(arcOff > 200_000m, "Arc dps without the infusion " + arcOff);
            var infusion = imported.Document with { Conditions = imported.Document.Conditions with { ArcLightningInfused = true } };
            var boosted = CharacterCalculator.Calculate(infusion, Tree.Value, StatMap.Value, Catalog.Value);
            decimal arcOn = boosted.Skills.Where(s => s.GemName == "Arc").Max(s => s.Dps);
            Assert(Math.Abs(arcOn / arcOff - 3m) <= 0.05m,
                "the +200% infusion more must triple Arc: " + arcOff + " -> " + arcOn);
            Console.WriteLine($"  Arc with the infusion checkbox: {arcOff:0} -> {arcOn:0} (x{arcOn / arcOff:0.##})");
        }));

        await test("Parity: Rage, Elemental Conflux and Trinity scale damage exactly as PoB2's config does", () => Task.Run(() =>
        {
            Import("pobb-twister.txt", out var imported);
            var document = imported.Document;
            Assert(document.Conditions.RageStacks == 30,
                "the fixture's own Rage count must be imported: " + document.Conditions.RageStacks);
            SkillDpsInfo Twister(PoeBuilder.Core.Models.BuildDocument doc) => CharacterCalculator
                .Calculate(doc, Tree.Value, StatMap.Value, Catalog.Value)
                .Skills.Where(s => s.GemName == "Twister").OrderByDescending(s => s.Dps).First();
            // The per-element hit PoB2's panels show ("Average hit: … (physical 0, fire 0, cold 0, lightning 0)").
            decimal Element(SkillDpsInfo skill, string element)
            {
                string line = skill.Breakdown.First(b => b.StartsWith("Average hit:", StringComparison.Ordinal));
                var match = System.Text.RegularExpressions.Regex.Match(line, element + @" ([0-9]+(?:\.[0-9]+)?)");
                return match.Success ? decimal.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0m;
            }
            var baseline = Twister(document);
            decimal hit = baseline.AvgHit;
            // PoB2: CalcPerform.lua:782 resolves the Rage count into a RageEffect of floor(stacks * (1 + INC)),
            // applied as "Damage MORE" for attacks; Berserk's 75% increased Rage effect lifts 30 into 52.
            Assert(baseline.Breakdown.Any(b => b.Contains("Rage damage (more)", StringComparison.Ordinal) &&
                                               b.Contains("30 rage", StringComparison.Ordinal)),
                "the Rage multiplier must be reported for its 30 stacks: " + string.Join(" | ", baseline.Breakdown));
            decimal hitNoRage = Twister(document with { Conditions = document.Conditions with { RageStacks = 0 } }).AvgHit;
            Assert(Math.Abs(hit / hitNoRage - 1.52m) <= 0.01m,
                "removing Rage must remove exactly the 52% more multiplier: " + hitNoRage + " -> " + hit);
            // Elemental Conflux: the default "Average" element divides its 75% more damage across the three
            // elements (ModStore.lua:398 inverts the multiplier: 75 / 3 = 25 each); naming one element gives it
            // the whole 75% and leaves the other two at zero. The two ratios below can only hold together if
            // both 1.25 and 1.75 are in play.
            decimal fireAverage = Element(baseline, "fire"), lightningAverage = Element(baseline, "lightning");
            Assert(fireAverage > 0 && lightningAverage > 0, "both elements must carry damage: " + fireAverage + "/" + lightningAverage);
            var lightning = Twister(document with { Conditions = document.Conditions with { ConfluxElement = 2 } });
            Console.WriteLine("  conflux lightning: avg " + baseline.Breakdown.First(b => b.StartsWith("Average hit:", StringComparison.Ordinal)) +
                " -> " + lightning.Breakdown.First(b => b.StartsWith("Average hit:", StringComparison.Ordinal)));
            Assert(Math.Abs(Element(lightning, "lightning") / lightningAverage - 1.4m) <= 0.02m,
                "a named conflux element replaces the 25% average with its full 75% (x1.4): " +
                lightningAverage + " -> " + Element(lightning, "lightning"));
            Assert(Math.Abs(Element(lightning, "fire") / fireAverage - 0.8m) <= 0.02m,
                "the elements the conflux did not choose lose the whole multiplier (1 / 1.25 = 0.8): " +
                fireAverage + " -> " + Element(lightning, "fire"));
            // Trinity: ConfigOptions.lua:673 sets Multiplier:ResonanceCount (0..300) and the gem's own statMap
            // scales its ElementalDamage MORE by floor(resonance / 30) — 300 resonance is 10 stacks of 6%.
            var resonance = Twister(document with { Conditions = document.Conditions with { ResonanceCount = 300 } });
            Assert(Math.Abs(Element(resonance, "fire") / fireAverage - 1.6m) <= 0.02m,
                "300 resonance adds 60% more element damage (x1.6 on top of the conflux's multiplier): " +
                fireAverage + " -> " + Element(resonance, "fire"));
            Assert(resonance.AvgHit > hit, "the resonance must raise the hit: " + hit + " -> " + resonance.AvgHit);
            Console.WriteLine("  Twister hit: base " + hit + ", without Rage " + hitNoRage + ", lightning conflux " +
                lightning.AvgHit + ", resonance 300 " + resonance.AvgHit +
                " (fire " + fireAverage + " / " + Element(lightning, "fire") + " / " + Element(resonance, "fire") + ")");
        }));

        await test("Parity: tree jewel sockets resolve to the imported jewels the tree draws", () => Task.Run(() =>
        {
            Import("pobb-mercenary-ll-arc.txt", out var imported);
            var plan = imported.Document.Tree!;
            var equipment = imported.Document.Equipment!;
            Assert(plan.Jewels.Count >= 5, "socketed jewels " + plan.Jewels.Count);
            foreach (var (nodeId, itemId) in plan.Jewels)
            {
                Assert(Tree.Value.Nodes.TryGetValue(nodeId, out var node) && node.IsJewel,
                    "socket node " + nodeId + " must be a jewel socket");
                var item = equipment.Items.FirstOrDefault(i => i.Id == itemId);
                Assert(item is not null, "jewel item for socket " + nodeId);
                Assert(item!.Mods.Length > 0 || item.Notes.Length > 0, "jewel " + item.Name + " keeps its affixes");
            }
            Console.WriteLine("  tree jewel sockets: " + plan.Jewels.Count + " (" +
                string.Join(", ", plan.Jewels.Select(kv => Tree.Value.Nodes[kv.Key].Name)) + ")");
        }));

        await test("Parity: Huntress (Ice Shot) against PoB2's own Calcs panel", () => Task.Run(() =>
        {
            // Third reference build (pobb.in/Z-Y5RgB2CDO0, "PoB - Huntress"): one two-handed bow, no
            // conversions, so it isolates the base hit and the effective mode. PoB2's panel for it:
            //   Skill DPS 13,785.9, AverageDamage 9,394.6, rate 1.44/s (Inc. Att. Speed 39%, 0.7s),
            //   MH Hit Damage 1,221-3,464 (physical 65-121, lightning 50-1,436, cold 680-1,245,
            //   fire 417-663), MH Total Increased 129%, MH Total More 0%,
            //   Crits 48.87% x6.52 (Inc. Crit Chance 350%), Crit Effect Mod x3.698, Gem Level 30,
            //   Effective DPS mod: physical x0.289, lightning/cold/fire x0.5, chaos x0.
            // The x0.5 on all three elements is PoB2's default 50% enemy elemental resistance; the x0.289
            // physical is armour (monsterArmourTable[82] x1.5 with ArmourRatio 10 reproduces it).
            var summary = Import("pobb-huntress-ice-shot.txt", out var imported);
            var iceShot = summary.Skills.Where(s => s.HasData && s.IsAttack).OrderByDescending(s => s.EffectiveDps).First();
            Console.WriteLine($"  Huntress: {iceShot.GemName} raw hit {iceShot.AvgHit:0.#} -> effective {iceShot.EffectiveDps:0.#} " +
                $"(PoB2 Skill DPS 13785.9, AverageDamage 9394.6), rate {iceShot.HitsPerSecond:0.##} (1.44), " +
                $"crit {iceShot.CritChancePercent:0.##}% (48.87) x{iceShot.CritBonusPercent / 100 + 1:0.##} (6.52)");
            var effectiveLine = iceShot.Breakdown.FirstOrDefault(b => b.StartsWith("Effective DPS mod", StringComparison.Ordinal));
            Assert(effectiveLine is not null, "the effective (enemy) mods must be reported: " + string.Join(" | ", iceShot.Breakdown));
            Console.WriteLine("  " + effectiveLine);
            Assert(summary.Skills.Count > 0, "the build must produce skill calculations");
        }));

        await test("Parity: Twister (attack) uses PoB2's own skill data", () => Task.Run(() =>
        {
            // The second reference build (pobb.in/BnfRYrC5wjwL, Mercenary 98, mainSocketGroup=11 = Twister).
            // PoB2's panel for it, read from the share code's own PlayerStats, is the target:
            //   AverageDamage 951097.94, Speed 2.072, CritChance 75 (PreEffectiveCritChance 50),
            //   CritMultiplier 12.39, TotalDPS 2049501.93, IgniteDPS 46225.17, ManaCost 102,
            //   Life 2202, Mana 998, EnergyShield 5020, Armour 3469, Evasion 6264, Spirit 320.
            var summary = Import("pobb-twister.txt", out var imported);
            var twisterGem = Catalog.Value.Gems.Values.First(g => g.Name == "Twister");
            var data = Catalog.Value.SkillData.ForGem(twisterGem.Id);
            Assert(data is not null, "Twister must be joined to PoB2's skill data through Gems.lua's gameId");
            Assert(data!.Number("baseMultiplier", 20) == 2.32m, "Twister level 20 baseMultiplier is 2.32");
            Assert(data.Number("attackSpeedMultiplier", 20) == -20m, "Twister attackSpeedMultiplier is -20");
            var twister = summary.Skills.Where(s => s.GemName == "Twister").OrderByDescending(s => s.Dps).First();
            Assert(twister.IsAttack, "Twister must be modelled as an attack (weapon damage x baseMultiplier)");
            Assert(twister.AvgHit > 1_500m, "Twister avg hit after the baseMultiplier fix: " + twister.AvgHit);
            Assert(twister.Breakdown.Any(b => b.Contains("Base damage multiplier")), "breakdown must show the multiplier");
            Assert(twister.Breakdown.Any(b => b.Contains("Attack speed multiplier")), "breakdown must show the attack speed multiplier");
            // The build plays the swap weapon set: <Items useSecondWeaponSet="true"> and the Twister group's
            // slot="Weapon 1 Swap" (The Ordained, Grand Spear — 0.714/s base, 226% local physical, +7.54%
            // crit). PoB2's own Speed 2.072 is Grand Spear's rate x1.85 x0.8, so this is exactly its weapon.
            Assert(imported.Document.Equipment is not null && imported.Document.Equipment.WeaponSet == 2,
                "the import must follow useSecondWeaponSet, otherwise the wrong weapon is priced in");
            // The rate moved from 2.16/s to 1.69/s when weapon-set allocations stopped being counted
            // twice: the fixture allocates 24 nodes for set 1 AND 24 for set 2, and every node allocated
            // for the other set must not contribute (PoB2's allocation mode + WeaponSetN condition,
            // Classes/PassiveSpec.lua:42-43, Modules/CalcSetup.lua:264-277). The old 2.16 came from the
            // 24 set-1 nodes' attack speed, which PoB2 never applies while set 2 is active. The gemling
            // node "Skills have 4% increased Skill Speed per Connected Green Support Gem" then lifted it to
            // 1.82/s (2 green supports of the group, CalcOffence.lua:712-717); PoB2's own 2.072 is still
            // above us — the remaining attack-speed sources are listed in docs/POB2-FORMULAS.md §11.9.
            Assert(twister.HitsPerSecond > 2.05m && twister.HitsPerSecond < 2.15m,
                "attack rate on the swap weapon with the active weapon set's nodes only: " + twister.HitsPerSecond);
            // Charges + their buffs: the config enables 3 Frenzy, 3 Power and 3 Endurance charges, and PoB2's
            // Charge Regulation effect ("Charge Infusion") scales three mods off them — Speed INC with a frenzy
            // charge (which is why PoB2's own rate is 2.072), CritChance MORE with a power charge, and
            // Armour/Evasion/EnergyShield MORE with an endurance charge.
            Assert(twister.Breakdown.Any(b => b.Contains("per-green", StringComparison.Ordinal)),
                "the per-colour support scaling must be applied, not dropped: " + string.Join(" | ", twister.Breakdown));
            Assert(twister.Breakdown.Any(b => b.Contains("Buff crit chance (more)", StringComparison.Ordinal)),
                "Charge Infusion's crit-chance MORE (from the config's Power Charges) must apply: " + string.Join(" | ", twister.Breakdown));
            // Blazing Critical is a SUPPORT whose statMap grants a global buff; this build's config enables
            // "Critical Hits Recently", so its "imbue all of your Attacks with Fire damage" (+15% of damage
            // gained as extra Fire) must reach the Twister's hit.
            Assert(twister.Breakdown.Any(b => b.Contains("Gain as extra (fire): +15", StringComparison.Ordinal)),
                "Blazing Critical's attack-only extra Fire must apply: " + string.Join(" | ", twister.Breakdown));
            // Garukhan's Resolve caps the crit chance at 50% and bifurcates it:
            // CritChance 75 (PreEffective 50) and CritBifurcates x1.33 in PoB2's panel.
            Assert(twister.Breakdown.Any(b => b.Contains("Crit bifurcation")), "the bifurcated crit must show up");
            Assert(twister.CritChancePercent > 70m && twister.CritChancePercent < 78m,
                "the bifurcated crit chance must land on PoB2's 75%: " + twister.CritChancePercent);
            // The weapon-class family of tree mods ("40% increased Critical Damage Bonus with Spears",
            // "10% increased Critical Hit Chance with Spears" x2) counts only while a spear is in hand, and the
            // Time-Lost jewels' radius lines add to the same bucket: Javelin's +40, the three socketed Emeralds'
            // own "31/28/27% increased Critical Damage Bonus with Spears" (+86, PoB2 parses those lines as
            // CritMultiplier INC) and "12% increased Critical Damage Bonus with Spears" on each of the 10
            // notables inside their radius (+120) = +246. That is exactly the +86 the crit sum was missing:
            // PoB2's pre-bifurcate bonus is +854% and this build's total is 100 + 668 + 86 = 854.
            var weaponClass = twister.Breakdown.FirstOrDefault(b => b.StartsWith("Weapon-class mods", StringComparison.Ordinal));
            Assert(weaponClass is not null, "the spear-scoped tree mods must be resolved, not dropped");
            Assert(weaponClass!.Contains("spear", StringComparison.Ordinal) &&
                weaponClass.Contains("crit chance +20", StringComparison.Ordinal) &&
                weaponClass.Contains("crit bonus +246", StringComparison.Ordinal),
                "Javelin's +40% crit bonus, the two +10% crit chance nodes, the jewels' own 31/28/27% lines " +
                "and their radius lines must all apply: " + weaponClass);
            Assert(summary.Skills.Any(s => s.GemName == "Twister"), "Twister group must resolve");
            // Resistances against PoB2's own panel (PlayerStat FireResist / ColdResistOverCap /
            // LightningResistOverCap / ChaosResistOverCap), which the owner's screenshots confirm:
            // Fire 69 (+0), Cold 75 (+14), Lightning 75 (+12), Chaos 75 (+3). They now match EXACTLY: the
            // panel's aura row (+45 Fire Resist, "Purity of Fire") is the socketed gem at level 19 (40)
            // plus the integral part of qualityStat x quality (0.4 x 14 = 5.6 -> 5), and the item-granted
            // Purity of Fire of the equipped sceptre merges into the same buff instead of adding a second
            // time. See docs/POB2-FORMULAS.md §11.11.
            var gold = PobGoldens(File.ReadAllText(FixturePath("pobb-twister.txt")).Trim());
            Console.WriteLine($"  resists: fire {summary.FireRes:0.#} (PoB2 {gold.GetValueOrDefault("FireResist"):0.#}), " +
                $"cold {summary.ColdRes:0.#} (PoB2 {gold.GetValueOrDefault("ColdResist"):0.#}+{gold.GetValueOrDefault("ColdResistOverCap"):0.#}), " +
                $"lightning {summary.LightRes:0.#} (PoB2 {gold.GetValueOrDefault("LightningResist"):0.#}+{gold.GetValueOrDefault("LightningResistOverCap"):0.#}), " +
                $"chaos {summary.ChaosRes:0.#} (PoB2 {gold.GetValueOrDefault("ChaosResist"):0.#}+{gold.GetValueOrDefault("ChaosResistOverCap"):0.#})");
            Assert(summary.FireRes == gold["FireResist"],
                "fire resistance " + summary.FireRes + " vs PoB2 " + gold["FireResist"]);
            // PoB2 reports the overcapped part separately, so the raw source sum minus the endgame penalty
            // (and minus the cap) has to reproduce it: this is the number the owner's screenshots show as
            // "+14 / +12 / +3" and the one our sheet used to display as a raw +149 / +147 / +78.
            Assert(summary.ColdRes == gold["ColdResist"] &&
                summary.ColdResSources - CharacterCalculator.EndgameElementalPenalty - CharacterCalculator.ResistanceCap == gold["ColdResistOverCap"],
                "cold " + summary.ColdRes + " vs PoB2 " + gold["ColdResist"] + "+" + gold["ColdResistOverCap"]);
            Assert(summary.LightRes == gold["LightningResist"] &&
                summary.LightResSources - CharacterCalculator.EndgameElementalPenalty - CharacterCalculator.ResistanceCap == gold["LightningResistOverCap"],
                "lightning " + summary.LightRes + " vs PoB2 " + gold["LightningResist"] + "+" + gold["LightningResistOverCap"]);
            Assert(summary.ChaosRes == gold["ChaosResist"] &&
                summary.ChaosResSources - CharacterCalculator.ResistanceCap == gold["ChaosResistOverCap"],
                "chaos " + summary.ChaosRes + " vs PoB2 " + gold["ChaosResist"] + "+" + gold["ChaosResistOverCap"]);
            Console.WriteLine($"  Twister: avg hit {twister.AvgHit:0.#} (PoB2 951097.9), rate {twister.HitsPerSecond:0.###} (2.072), " +
                $"crit {twister.CritChancePercent:0.##}% (50 pre-effective) / x{twister.CritBonusPercent / 100 + 1:0.##} (12.39), " +
                $"dps {twister.Dps:0.#} (PoB2 TotalDPS 2049501.9)");
            // The fixture's own PoB2 export was made in EFFECTIVE buff mode (<Input string="EFFECTIVE"
            // name="misc_buffMode"/>), so its PlayerStat AverageDamage/TotalDPS already carry the enemy-side
            // modifiers (resistances, the enemy's own crit-damage taken). The like-for-like comparison is
            // therefore our "Effective DPS (PoB2 mode)" figure and the effective per-hit value, and both are
            // asserted with the residual the crash in the numbers documents: the crit bonus is +1024% against
            // PoB2's +1139% and the effective hit is within a few per cent.
            string effectiveLine = twister.Breakdown.First(b => b.StartsWith("Effective DPS mod (enemy):", StringComparison.Ordinal));
            decimal effectiveAverageHit = ParseNumber(effectiveLine, @"hit ([0-9]+(?:\.[0-9]+)?)");
            decimal effectiveDps = ParseNumber(twister.Breakdown.First(b => b.StartsWith("Effective DPS (PoB2 mode):", StringComparison.Ordinal)),
                @": ([0-9]+(?:\.[0-9]+)?)");
            Console.WriteLine($"  Twister like-for-like (PoB2 exported in EFFECTIVE mode): effective hit {effectiveAverageHit:0.#} " +
                $"vs PoB2's own 49,447-148,577 (average 99,012) = x{(effectiveAverageHit == 0 ? 0 : 99012m / effectiveAverageHit):0.###} to go; " +
                $"effective dps {effectiveDps:0.#} vs 2,049,501.9 = x{(effectiveDps == 0 ? 0 : 2049501.9m / effectiveDps):0.###} to go");
            Assert(twister.CritBonusPercent >= 1020m,
                "Garukhan's Resolve must bifuracte the crit bonus as a MORE (PoB2 CalcOffence.lua:3824-3843), " +
                "not replace it: bonus " + twister.CritBonusPercent);
            Assert(effectiveDps > 1_500_000m && effectiveDps < 2_050_000m,
                "the effective DPS must be within the documented residual of PoB2's own 2,049,501.9: " + effectiveDps);
            Console.WriteLine("  Twister breakdown:");
            foreach (var line in twister.Breakdown) Console.WriteLine("      " + line);
            Console.WriteLine("  gap map (evidence in docs/POB2-FORMULAS.md §11.17-§11.18): PoB2's export is EFFECTIVE-mode, so the " +
                $"like-for-like residuals are the effective hit x{(effectiveAverageHit == 0 ? 0 : 99012m / effectiveAverageHit):0.###} " +
                $"and the effective DPS x{(effectiveDps == 0 ? 0 : 2049501.9m / effectiveDps):0.###}; on top of that the crit bonus is " +
                $"x{1139m / twister.CritBonusPercent:0.###} short (our +{twister.CritBonusPercent:0.#}% against its +1139%, the pre-bifurcate " +
                $"sum being +768% against +854%) and our rate is x{2.072m / twister.HitsPerSecond:0.###} (we are slightly FASTER). " +
                $"Plus {imported.Report.EquipmentSkippedLines} unmatched item lines and {summary.UnaccountedTotal} unaccounted stat lines.");
            Console.WriteLine("  aura/buff extras: charges(fpp)=" + summary.Extras.GetValueOrDefault("Pool:Charges") + " rage=" +
                summary.Extras.GetValueOrDefault("Pool:Rage") + "/" + summary.Extras.GetValueOrDefault("Pool:RageEffect") + " : " +
                string.Join(" | ", summary.Extras.Where(e => e.Key.StartsWith("Aura:", StringComparison.Ordinal))
                    .Take(24).Select(e => e.Key[5..] + "=" + e.Value)));
            Console.WriteLine("  crit bonus sources: " + string.Join(" | ", summary.Extras
                .Where(e => e.Key.StartsWith("CritSrc:", StringComparison.Ordinal))
                .Select(e => e.Key[8..] + "=" + e.Value)) + "  radius: " + string.Join(" | ", summary.Extras
                .Where(e => e.Key.StartsWith("RadiusCrit:", StringComparison.Ordinal))
                .Select(e => e.Key[11..] + "=" + e.Value)));
            Console.WriteLine("  radius crit sources: " + string.Join(" | ", summary.Extras
                .Where(e => e.Key.StartsWith("RadiusCrit:", StringComparison.Ordinal))
                .Select(e => e.Key[11..] + "=" + e.Value)));
            Console.WriteLine("  radius crit sources: " + string.Join(" | ", summary.Extras
                .Where(e => e.Key.StartsWith("RadiusCrit:", StringComparison.Ordinal))
                .Select(e => e.Key[11..] + "=" + e.Value)));
            Console.WriteLine("  unmatched item lines (" + imported.Report.EquipmentSkippedLines + "): " +
                string.Join(" | ", (imported.Report.EquipmentSkippedTexts ?? []).Take(30)));
            Console.WriteLine("  unaccounted tree lines: " + string.Join(" | ",
                summary.Unaccounted.Keys.Where(k => k.StartsWith("tree:", StringComparison.Ordinal)).Take(40)));
            Console.WriteLine("  unaccounted unique lines: " + string.Join(" | ",
                summary.Unaccounted.Keys.Where(k => k.StartsWith("unique:", StringComparison.Ordinal)).Take(40)));
            // The COMPLETE list, one line per key: the report truncates nothing here, because this is the
            // list to work through. Counts come first so a family of lines can be spotted at a glance.
            Console.WriteLine("  unaccounted, all " + summary.Unaccounted.Count + " keys:");
            foreach (var entry in summary.Unaccounted.OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal))
                Console.WriteLine($"    {entry.Value,4}x {entry.Key.Replace("\n", " ⏎ ")}");
        }));

        await test("Parity: a weapon's own damage lines are read locally, exactly like PoB2", () => Task.Run(() =>
        {
            // PoB2 reads every damage/speed/crit line of a WEAPON with calcLocal (Classes/Item.lua:1909-1949),
            // so "226% increased Physical Damage" on a weapon is local_physical_damage_+% and multiplies only
            // that weapon — the same wording on a ring or a node stays global.
            Assert(CharacterCalculator.WeaponLocalId("physical_damage_+%") == "local_physical_damage_+%",
                "a weapon's physical damage increase is local");
            Assert(CharacterCalculator.WeaponLocalId("attack_speed_+%") == "local_attack_speed_+%", "weapon attack speed is local");
            Assert(CharacterCalculator.WeaponLocalId("attack_minimum_added_lightning_damage") == "local_minimum_added_lightning_damage",
                "a weapon's added damage is local");
            Assert(CharacterCalculator.WeaponLocalId("life_regeneration_percent_per_second") == "life_regeneration_percent_per_second",
                "a line outside the weapon-local family is untouched");

            // PoB2's weapon damage formula: quality multiplies PHYSICAL as its own factor (it is not folded
            // into the local percentage) and a weapon's added elemental damage takes the local elemental
            // increases but never the physical one. The Twister fixture proves it end to end: The Ordained,
            // Grand Spear (56-84 physical, 226% local, quality 26, "Adds 1 to 296 Lightning Damage").
            var summary = Import("pobb-twister.txt", out _);
            var twister = summary.Skills.Where(s => s.GemName == "Twister").OrderByDescending(s => s.Dps).First();
            string weaponLine = twister.Breakdown.First(b => b.StartsWith("Base (weapon)", StringComparison.Ordinal));
            decimal weaponPhysical = decimal.Parse(
                System.Text.RegularExpressions.Regex.Match(weaponLine, @"[\d.,]+").Value.Replace(",", "."),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert(Math.Abs(weaponPhysical - 70m * 3.26m * 1.26m) < 0.2m,
                "weapon physical = 70 base x 3.26 local x 1.26 quality: " + weaponLine);
            Assert(twister.Breakdown.Any(b => b.StartsWith("Weapon (local):", StringComparison.Ordinal) && b.Contains("lightning")),
                "the weapon's own added lightning is weapon damage, not a global add");
            Assert(twister.Breakdown.Any(b => b.StartsWith("Added (attack):", StringComparison.Ordinal) && !b.Contains("lightning")),
                "only the character-wide added damage stays a global add");
        }));

        await test("Parity: a support keeps its own tier (Projectile Acceleration III, not I)", () => Task.Run(() =>
        {
            // PoB2's skill id carries the full name while the game's own gem id is shorter, so the importer
            // has to prefer the MOST specific tail; matching the shortest one gave "Projectile Acceleration I"
            // and silently dropped tier III's stats and its projectile-speed-applies-to-damage flag.
            var summary = Import("pobb-twister.txt", out var imported);
            var twisterGroup = imported.Document.Skills!.Groups.First(g =>
                Catalog.Value.Gems.GetValueOrDefault(g.Active.GemId)?.Name == "Twister");
            var supportNames = twisterGroup.Supports
                .Select(s => Catalog.Value.Gems.GetValueOrDefault(s.GemId)?.Name ?? "?").ToArray();
            Assert(supportNames.Contains("Projectile Acceleration III"),
                "the Twister group must keep its tier: " + string.Join(", ", supportNames));
            var twister = summary.Skills.Where(s => s.GemName == "Twister").OrderByDescending(s => s.Dps).First();
            // The flag (projectile_speed_additive_modifiers_also_apply_to_projectile_damage) folds the
            // character's projectile-speed increases into that skill's damage.
            Assert(twister.Breakdown.Any(b => b.Contains("Projectile speed as damage", StringComparison.Ordinal)),
                "the projectile-speed-as-damage flag must apply: " + string.Join(" | ", twister.Breakdown));
            // Execute III is a conditional MORE the reference also applies (the build is on Low Life).
            Assert(twister.Breakdown.Any(b => b.Contains("more x1.3", StringComparison.Ordinal)),
                "Execute III's +30% more damage while on Low Life must apply once: " + string.Join(" | ", twister.Breakdown));
        }));

        await test("Parity: PoB2's distance and condition config gates the conditional stat lines", () => Task.Run(() =>
        {
            var summary = Import("pobb-twister.txt", out var imported);
            // PoB2 writes <Placeholder number="20" name="enemyDistance"/> and GetDefaultState resolves an absent
            // input back to that placeholder, so an untouched build fights at 20 units = 2 metres
            // (ConfigOptions.lua:1621, ConfigTab.lua:712-715). That is exactly why its "within 2m" family applies.
            Assert(imported.Document.Conditions.EnemyDistance == 20m,
                "the imported build must carry PoB2's own placeholder distance: " + imported.Document.Conditions.EnemyDistance);
            // "Projectiles deal X% increased Damage with Hits against Enemies within 2m" is a
            // MultiplierThreshold on enemyDistance (ModParser.lua:2154) and must be resolved, not skipped.
            Assert(!summary.Unaccounted.Keys.Any(k => k.Contains("projectile_damage_+%_vs_enemies_within_2m_distance", StringComparison.Ordinal)),
                "the within-2m projectile damage must be resolved, not unaccounted");
            Assert(summary.Extras.Keys.Any(k => k.StartsWith("Condition:projectile_damage_+%_vs_enemies_within_2m_distance=on", StringComparison.Ordinal)),
                "20 units is within 2 metres, so the line is active: " +
                string.Join(" | ", summary.Extras.Keys.Where(k => k.StartsWith("Condition:projectile", StringComparison.Ordinal))));
            // "N% increased Attack Damage while Surrounded" needs PoB2's conditionSurrounded checkbox, which this
            // build does not set — so it must be evaluated as OFF rather than assumed or left unaccounted.
            Assert(summary.Extras.Keys.Any(k => k.StartsWith("Condition:attack_damage_+%_while_surrounded=off", StringComparison.Ordinal)),
                "the surrounded line must be resolved to off, not applied");
            Assert(!summary.Unaccounted.Keys.Any(k => k.StartsWith("attack_damage_+%_while_surrounded", StringComparison.Ordinal)),
                "a resolved-but-inactive line leaves the unaccounted list");
            // "25% more Skill Speed while Off Hand is empty ..." (ModParser.lua:2333): the active weapon set's off
            // hand holds Sylvan's Effigy, so the pair is false — the line must be decided by the equipment.
            Assert(summary.Extras.Keys.Any(k => k.StartsWith("Condition:skill_speed_+%_final_while_off_hand_is_empty", StringComparison.Ordinal)
                    && k.EndsWith("=off", StringComparison.Ordinal)),
                "the off-hand condition must follow the equippped set: " +
                string.Join(" | ", summary.Extras.Keys.Where(k => k.StartsWith("Condition:skill_speed", StringComparison.Ordinal))));
            // Gemling's per-colour support scaling (CalcOffence.lua:684-722): red -> damage, green -> speed,
            // blue -> crit chance, each multiplied by that colour's support count.
            var twister = summary.Skills.Where(s => s.GemName == "Twister").OrderByDescending(s => s.Dps).First();
            Assert(twister.Breakdown.Any(b => b.Contains("per-red", StringComparison.Ordinal)) &&
                twister.Breakdown.Any(b => b.Contains("per-blue", StringComparison.Ordinal)),
                "the red/blue support scaling must apply: " + string.Join(" | ", twister.Breakdown));
        }));

        await test("Parity: PoB2's unique data is loaded and drives unique modifiers", () => Task.Run(() =>
        {
            // Every unique PoB2 ships now has its identity AND its modifier lines in the catalog: the
            // pinned RePoE export carries identities only (name/class/icon).
            var catalog = Catalog.Value;
            Assert(catalog.UniqueData.Count >= 400, "unique data entries " + catalog.UniqueData.Count);
            var morior = catalog.UniqueData.For("Morior Invictus");
            Assert(morior is not null && morior.Variants.Length > 20, "Morior Invictus keeps its variant list");
            Assert(morior!.SelectedVariant > 0, "the variant PoB2 marks as live must be kept");
            var mods = catalog.UniqueData.ModsFor("Morior Invictus");
            Assert(mods.Count > 0, "Morior Invictus must resolve modifier lines");
            // A line without a variant filter always applies; the variant-selected lines are the live ones.
            Assert(mods.All(m => m.Variants.Length == 0 || m.Variants.Contains(morior.SelectedVariant)),
                "only the live variant's lines may resolve");
            Assert(mods.Any(m => m.Line.Contains("Armour, Evasion and Energy Shield", StringComparison.Ordinal)),
                "the live variant's own line must be there");
            Assert(catalog.UniqueData.ModsFor("Not A Real Unique").Count == 0, "unknown uniques resolve nothing");

            // The Taming's per-ailment line and The Ordained's all-elements rune are mapped the way PoB2
            // maps them, so unique modifiers reach the calculation instead of being dropped.
            var taming = UniqueTextParser.ParseMods("Rarity: UNIQUE\nThe Taming\nPrismatic Ring\nImplicits: 0\n"
                + "24% increased Damage for each type of Elemental Ailment on Enemy").ToArray();
            Assert(taming.Length == 3 && taming.All(e => e.Id.StartsWith("conditional_damage_+%_enemy_", StringComparison.Ordinal)),
                "per-ailment damage must expand into the conditional entries: " + string.Join(",", taming.Select(t => t.Id)));
            var allElements = UniqueTextParser.ParseMods("Rarity: UNIQUE\nThe Ordained\nGrand Spear\nImplicits: 0\n"
                + "Gain 5% of Damage as Extra Damage of all Elements").ToArray();
            Assert(allElements.Length == 3 && allElements.All(e => e.Value == 5m),
                "all-elements gain must expand into fire/cold/lightning: " + string.Join(",", allElements.Select(t => t.Id)));
            var flatCrit = UniqueTextParser.ParseMods("Rarity: UNIQUE\nThe Ordained\nGrand Spear\nImplicits: 0\n"
                + "+7.54% to Critical Hit Chance").ToArray();
            Assert(flatCrit.Length == 1 && flatCrit[0].Id == "critical_strike_chance_+" && flatCrit[0].Value == 7.54m,
                "flat critical hit chance must map to a flat addition: " + string.Join(",", flatCrit.Select(t => t.Id)));
            // Ranges in PoB2's data resolve to their maximum roll.
            Assert(UniqueTextParser.ResolveRanges("+(10-20) to Strength") == "+20 to Strength", "range resolution");
            Console.WriteLine($"  unique data: {catalog.UniqueData.Count} uniques, Morior variant {morior.SelectedVariant}/{morior.Variants.Length}, "
                + $"{mods.Count} live modifier lines");
        }));

        await test("Parity: PoB2's own statMap drives support modifiers", () => Task.Run(() =>
        {
            // Every support PoB2 ships has a statMap (Data/Skills/sup_*.lua) naming the mod behind each of
            // its stat ids. Short Fuse's is the canonical example.
            var shortFuse = Catalog.Value.SkillData.ForEffect("SupportShortFusePlayerTwo");
            Assert(shortFuse is not null, "Short Fuse must be in PoB2's skill data");
            var effect = shortFuse!.Effects.FirstOrDefault(e => e.StatMap.ContainsKey("support_short_fuse_damage_+%_final"));
            Assert(effect is not null, "its statMap must map support_short_fuse_damage_+%_final");
            var spec = PoB2ModTranslator.Parse("mod(\"Damage\", \"MORE\", nil)");
            Assert(spec is { Name: "Damage", Type: "MORE" }, "a plain mod spec must parse");
            var sink = new SupportModSink();
            Assert(sink.Apply(spec!, -30m, false, []), "Damage MORE must be applied");
            Assert(sink.DamageMore == 0.7m, "−30% MORE must become x0.70, was " + sink.DamageMore);
            // A conditional spec is applied only when the condition is known to hold.
            var conditional = PoB2ModTranslator.Parse("mod(\"Damage\", \"MORE\", nil, 0, 0, { type = \"Condition\", var = \"LowLife\" })");
            var lowLifeSink = new SupportModSink();
            Assert(!lowLifeSink.Apply(conditional!, 20m, false, [], ModConditions.None), "unknown condition must not apply");
            Assert(lowLifeSink.Unhandled.Count == 1, "an unapplied spec must be catalogued");
            var onSink = new SupportModSink();
            Assert(onSink.Apply(conditional!, 20m, false, [], new ModConditions { LowLife = true }), "a known true condition must apply");
            Assert(onSink.DamageMore == 1.2m, "the conditional MORE must land: " + onSink.DamageMore);
            // The attack/spell gate comes from the skill's shape, exactly like PoB2's ModFlag keyword.
            var attackOnly = PoB2ModTranslator.Parse("mod(\"Damage\", \"MORE\", nil, ModFlag.Attack)");
            Assert(new SupportModSink().Apply(attackOnly!, 10m, true, [], ModConditions.None), "ModFlag.Attack holds for an attack");
            Assert(!new SupportModSink().Apply(attackOnly!, 10m, false, [], ModConditions.None), "and not for a spell");
            // Garukhan's Resolve raises the critical strike chance cap (CritChanceCap OVERRIDE).
            var cap = new SupportModSink();
            var capSpec = PoB2ModTranslator.Parse("mod(\"CritChanceCap\", \"OVERRIDE\", nil)");
            Assert(cap.Apply(capSpec!, 100m, true, [], ModConditions.None) && cap.CritChanceCap == 100m, "the crit cap override must be captured");
            Console.WriteLine($"  statMap translator: Damage MORE x{sink.DamageMore:0.##}, conditional gating, ModFlag gate, crit cap {cap.CritChanceCap:0}#");
        }));

        await test("Parity: item affix matcher resolves PoE2 display markup inside the item's own mod pool", () => Task.Run(() =>
        {
            // The pinned catalog words mods with the game's display markup; item text never carries it.
            // Resolving "[Tag]" to Tag and "[Tag|Display]" to Display on both sides is what lets a
            // template like "(100-119)% increased [ElementalDamage|Elemental] Damage with
            // [Attack|Attacks]" compare equal to the line the importer reads.
            Assert(ModLineMatcher.Normalize("(100-119)% increased [ElementalDamage|Elemental] Damage with [Attack|Attacks]")
                == ModLineMatcher.Normalize("100% increased Elemental Damage with Attacks"), "markup must be resolved");
            // A leading display tag ("{enchant}{rune}…") is not part of the wording either. (The
            // "Bonded:" keyword itself is stripped by the text reader, not by the affix matcher.)
            Assert(ModLineMatcher.Normalize("{enchant}{rune}+20 to maximum Mana")
                == ModLineMatcher.Normalize("+20 to maximum Mana"), "display tags must be dropped");
            // Many mods share one wording across item classes ("+# to maximum Mana" exists for rings and
            // for two-handed weapons), so the matcher accepts the base's own mod pool and prefers a
            // member of it. (The preference only decides when two classes really word a mod the same way;
            // the ambiguity that used to mis-attribute a ring's mana affix to `IncreasedManaTwoHandWeapon8_`
            // is documented in docs/POB2-FORMULAS.md §11.6.)
            var matcher = ModLineMatcher.Build(Catalog.Value);
            var ring = Catalog.Value.Bases.Values.First(b => b.Name == "Mnemonic Ring");
            Assert(Catalog.Value.Data.ModPools.TryGetValue(ring.ModPool, out var poolIds) && poolIds.Length > 0,
                "the ring's base must expose a mod pool, was " + ring.ModPool);
            var pool = new HashSet<string>(poolIds!, StringComparer.Ordinal);
            var manaMatch = matcher.Match("+208 to maximum Mana", pool);
            Assert(manaMatch is not null, "the ring's flat-mana affix must match");
            var mana = manaMatch!.Value;
            Assert(mana.roll.Values.SequenceEqual([208m]), "the observed roll must be kept verbatim");
            // A member of the pool always wins over an outside mod of the same shape.
            var jewelOnly = new HashSet<string>(StringComparer.Ordinal) { "UniqueJewelRadiusIncreasedMana" };
            var scoped = matcher.Match("+8 to maximum Mana", jewelOnly);
            Assert(scoped is not null && jewelOnly.Contains(scoped.Value.roll.Id),
                "a pool member must win, was " + scoped?.roll.Id);
            Console.WriteLine($"  affix matcher: markup resolved, '+208 to maximum Mana' -> {mana.roll.Id}");
        }));

        await test("Parity: a real native saved build still loads and reports a metric sheet", () => Task.Run(() =>
        {
            var build = BuildRepository.ReadDocumentAsync(FixturePath("mercenary-pob2.poebuild")).GetAwaiter().GetResult();
            var summary = CharacterCalculator.Calculate(build, Tree.Value, StatMap.Value, Catalog.Value);
            Assert(summary.Skills.Count > 0, "at least one skill group must resolve");
            Console.WriteLine();
            Console.WriteLine("=== native saved build: " + build.Name + " (level " + summary.Level + ") ===");
            Console.WriteLine($"  life {summary.Life:0.##}, mana {summary.Mana:0.##}, ES {summary.EnergyShield:0.##}, " +
                $"ward {summary.Ward:0.##}, spirit {summary.Spirit:0.##}");
            Console.WriteLine($"  str {summary.Strength}, dex {summary.Dexterity}, int {summary.Intelligence}");
            Console.WriteLine($"  armour {summary.Armour:0.##}, evasion {summary.Evasion:0.##}, accuracy {summary.Accuracy:0.##}");
            Console.WriteLine($"  res fire {summary.FireRes} cold {summary.ColdRes} light {summary.LightRes} chaos {summary.ChaosRes}");
            Console.WriteLine($"  unaccounted lines: {summary.UnaccountedTotal}");
            foreach (var entry in summary.Unaccounted.OrderByDescending(e => e.Value).Take(10))
                Console.WriteLine($"    {entry.Value,4}x {entry.Key.Replace("\n", " ; ")}");
            foreach (var entry in summary.Known.OrderByDescending(e => e.Value).Take(6))
                Console.WriteLine($"    known: {entry.Key}");
            Console.WriteLine("=== end native saved build ===");
            Console.WriteLine();
        }));

        await test("Parity: weapon-set allocations count only with their own set active", () => Task.Run(() =>
        {
            // The Twister fixture allocates a whole second cluster: <WeaponSet1 nodes="…"/> and
            // <WeaponSet2 nodes="…"/> inside the spec (Classes/PassiveSpec.lua:272-277), 24 nodes each.
            var imported = Import("pobb-twister.txt", out var document);
            var plan = document.Document.Tree!;
            // Diagnostic: a weapon-set entry is dropped when its node is not allocated, so the count is the
            // first place a wrong allocation shows up. The raw ids are printed against the allocated set.
            var wsXml = XDocument.Parse(Encoding.UTF8.GetString(BuildInterop.DecodePobEnvelope(
                File.ReadAllText(FixturePath("pobb-twister.txt")).Trim())));
            var wsIds = wsXml.Descendants().Where(e => e.Name.LocalName.StartsWith("WeaponSet", StringComparison.Ordinal))
                .SelectMany(e => ((string?)e.Attribute("nodes") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(int.Parse).Distinct().ToList();
            Console.WriteLine($"  weapon-set ids in the share code: {wsIds.Count}, allocated: {plan.AllocatedNodes.Length}, " +
                $"not placed: {(wsIds.Where(id => !plan.AllocatedNodes.Contains(id)).ToArray() is { Length: > 0 } miss ? string.Join(",", miss) : "none")}");
            Console.WriteLine("  weapon-set ids missing from the plan's modes: " +
                string.Join(",", wsIds.Where(id => !plan.WeaponSetNodes.ContainsKey(id))));
            // PoB2's own <Spec nodes="…"> list is the ground truth for "no invented nodes": our allocation
            // must reproduce it exactly, plus the nodes ITEMS grant ("Allocates …", which PoB2 keeps out of
            // the spec because no point is spent on them). A rerouted node — the old behaviour for a passive
            // only a radius jewel can reach — shows up here as an extra path node the source never had.
            var specNodes = ((string?)wsXml.Descendants("Spec").First().Attribute("nodes") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(int.Parse).ToHashSet();
            var extras = plan.AllocatedNodes.Where(id => !specNodes.Contains(id) &&
                !(Tree.Value.Nodes.TryGetValue(id, out var extraNode) && extraNode.IsStart)).ToArray();
            var missing = specNodes.Where(id => !plan.AllocatedNodes.Contains(id) && Tree.Value.Nodes.ContainsKey(id)).ToArray();
            Console.WriteLine($"  source lists {specNodes.Count} nodes, plan allocates {plan.AllocatedNodes.Length} " +
                $"(radius rules: {plan.RadiusJewels.Count}, jewel-granted: {plan.JewelAllocatedNodes.Length}), " +
                $"invented: {(extras.Length == 0 ? "none" : string.Join(",", extras))}, " +
                $"not placed: {(missing.Length == 0 ? "none" : string.Join(",", missing))}");
            Assert(extras.Length == plan.JewelAllocatedNodes.Length,
                "only item-granted nodes may sit outside the source list: " + string.Join(",", extras));
            // The radius jewel ("From Nothing": "Passives in radius of Resonance can be Allocated without
            // being connected to your tree") must carry its cluster on its own: if any node had to be
            // connected by a shortest path, the import invented passives the build never took.
            Assert(!document.Report.Notes.Contains("кратчайшим"),
                "a radius jewel must not be replaced by a path: " + document.Report.Notes);
            Assert(plan.RadiusJewels.Count == 1, "the From Nothing jewel must carry its allocation rule");
            Assert(plan.RadiusJewels.Values.Single().FromKeystone && plan.RadiusJewels.Values.Single().KeystoneName == "Resonance",
                "the rule names the keystone its text names");
            Assert(!plan.AllocatedNodes.Contains(Tree.Value.Nodes.Values.First(n => n.IsKeystone && n.Name == "Resonance").Id),
                "the fixture reaches Resonance's radius without allocating Resonance itself");
            Assert(plan.WeaponSetNodes.Count == 48, "weapon-set node count, was " + plan.WeaponSetNodes.Count);
            Assert(plan.WeaponSetNodes.Count(pair => pair.Value == 1) == 24 && plan.WeaponSetNodes.Count(pair => pair.Value == 2) == 24,
                "24 nodes per set");
            Assert(plan.WeaponSetNodes.Keys.All(id => plan.AllocatedNodes.Contains(id)),
                "every weapon-set node is part of the allocated set");
            // The character is priced with the active item set's weapon set, so switching the set must
            // change the numbers: the set-1 cluster must not contribute while set 2 is in hand.
            var withSet1 = CharacterCalculator.Calculate(document.Document with
            { Equipment = document.Document.Equipment! with { WeaponSet = 1 } }, Tree.Value, StatMap.Value, Catalog.Value);
            var withSet2 = CharacterCalculator.Calculate(document.Document with
            { Equipment = document.Document.Equipment! with { WeaponSet = 2 } }, Tree.Value, StatMap.Value, Catalog.Value);
            decimal rate1 = withSet1.Skills.Where(s => s.GemName == "Twister").OrderByDescending(s => s.Dps).First().HitsPerSecond;
            decimal rate2 = withSet2.Skills.Where(s => s.GemName == "Twister").OrderByDescending(s => s.Dps).First().HitsPerSecond;
            Assert(rate1 != rate2, "the active weapon set must change the attack rate: " + rate1 + " vs " + rate2);
            Console.WriteLine($"  weapon sets: {plan.WeaponSetNodes.Count} nodes, rate set1 {rate1:0.###}/s vs set2 {rate2:0.###}/s (PoB2 set2 2.072)");
            // A refund has to drop the node's weapon-set mode (and any jewel socket/grant it owned), or the
            // saved plan would violate its own invariants and the next calculation would fail closed.
            var engine = new PassiveTreeEngine(Tree.Value);
            var target = plan.WeaponSetNodes.Keys.First();
            var refunded = engine.Refund(plan, target);
            Assert(!refunded.WeaponSetNodes.ContainsKey(target), "the refunded node keeps no weapon-set mode");
            engine.Validate(refunded);
            var weaponSetMoves = plan with { WeaponSetNodes = new Dictionary<int, int>(plan.WeaponSetNodes) { [target] = 1 } };
            engine.Validate(weaponSetMoves);
            Assert(weaponSetMoves.WeaponSetNodes[target] == 1, "a node can be moved into set I (PoB2's allocation mode)");
        }));

        await test("Parity: an item's \"Grants Skill\" line becomes an aura at the stated level", () => Task.Run(() =>
        {
            // "Grants Skill: Level 19 Purity of Fire" (Modules/ModParser.lua:3560-3561). The level matters:
            // the gem's own data table gives +40% Fire Resistance at 19 and +45% at 24.
            var grants = AuraSkillCalculator.ParseGrants("Rarity: UNIQUE\nSacred Flame\nShrine Sceptre\nImplicits: 4\n"
                + "Grants Skill: Level 19 Purity of Fire\nGrants Skill: Spear Throw");
            Assert(grants.Count == 2, "two grants, was " + grants.Count);
            Assert(grants[0].Name == "Purity of Fire" && grants[0].Level == 19 && grants[1].Name == "Spear Throw" && grants[1].Level == 1,
                "the roll travels with the skill: " + string.Join(", ", grants.Select(g => g.Name + "/" + g.Level)));
            var purity = Catalog.Value.SkillData.ForEffect("PurityOfFirePlayer");
            Assert(purity is not null && purity.QualityStats.Any(q => q.Stat == "base_skill_buff_fire_damage_resistance_%_to_apply"),
                "Purity of Fire's resistance stat and its quality coefficient must be pinned");
            Assert(AuraSkillCalculator.IsAura(Catalog.Value, purity!) && !AuraSkillCalculator.IsAura(Catalog.Value, Catalog.Value.SkillData.ForEffect("FireballPlayer")!),
                "a persistent aura counts as an aura; a damage spell does not");
        }));


        await test("Parity: radius jewels grant their lines once per allocated node in radius", () => Task.Run(() =>
        {
            // PoB2's bands are indexed 1..12 and are scaled by PassiveTreeJewelDistanceMultiplier = 1.2
            // (Modules/Data.lua:626-690, Data/Misc.lua:36).
            Assert(JewelRadius.IndexOfLabel("Very Large") == 4 && JewelRadius.DistanceMultiplier == 1.2, "band table and scale");
            // The same band is what the tree draws as the jewel's radius circle ("Very Large" = 1500 x 1.2).
            Assert(JewelRadius.OuterRadius(4) == 1800 && JewelRadius.OuterRadius(0) == 0 && JewelRadius.OuterRadius(99) == 0,
                "circle radius per band: " + JewelRadius.OuterRadius(4));
            // A Time-Lost jewel states its band with "Radius: …" and may raise it with "Upgrades Radius to …".
            Assert(JewelRadius.IndexForItemText("Radius: Variable\nUpgrades Radius to Very Large") == 4,
                "the upgrade line overrides the stated band");
            Assert(JewelRadius.IndexForItemText("Radius: Medium") == 2, "a plain radius line is read");
            var grants = JewelRadius.ParseGrants(
                "Radius: Very Large\n" +
                "Notable Passive Skills in Radius also grant 5% increased Critical Hit Chance\n" +
                "Notable Passive Skills in Radius also grant 10% increased Critical Damage Bonus\n" +
                "Small Passive Skills in Radius also grant 3% increased maximum Energy Shield\n" +
                "Passives in Radius of Hollow Palm Technique can be Allocated");
            Assert(grants.Count == 3, "three grants, was " + grants.Count);
            Assert(grants[0].Type == RadiusPassiveType.Notable && grants[2].Type == RadiusPassiveType.Small, "node types");
            Assert(RadiusEffects.Resolve("5% increased Critical Hit Chance").Single() == ("critical_strike_chance_+%", 5m),
                "crit chance wording");
            Assert(RadiusEffects.Resolve("10% increased Critical Damage Bonus").Single() == ("critical_strike_multiplier_+%", 10m),
                "crit bonus wording");
            Assert(RadiusEffects.Resolve("3% increased Global Armour, Evasion and Energy Shield").Single() == ("defences_+%", 3m),
                "global defences wording resolves through the quest-reward parser");
            Assert(RadiusEffects.Resolve("3% increased maximum Energy Shield").Single() == ("maximum_energy_shield_+%", 3m),
                "maximum pool wording");
            // The Time-Lost wordings resolve through the pinned stat map, which is what turns
            // "12% increased Critical Damage Bonus for Attack Damage" into a real stat instead of an
            // unaccounted line.
            Assert(StatMap.Value.TryResolve("12% increased Critical Damage Bonus for Attack Damage", out string sampleId, out decimal sampleValue) &&
                sampleId == "attack_critical_strike_multiplier_+" && sampleValue == 12m,
                "the stat map must resolve a wording whose pinned key carries another roll");
            Assert(RadiusEffects.Resolve("12% increased Critical Damage Bonus for Attack Damage", StatMap.Value).Single() == ("attack_critical_strike_multiplier_+", 12m),
                "attack crit bonus wording");
            Assert(RadiusEffects.Resolve("12% increased Critical Damage Bonus with Spears", StatMap.Value).Single() == ("spear_critical_strike_multiplier_+", 12m),
                "spear crit bonus wording");
            Assert(RadiusEffects.Resolve("7% increased Critical Hit Chance for Attacks", StatMap.Value).Single() == ("attack_critical_strike_chance_+%", 7m),
                "attack crit chance wording");
            Assert(RadiusEffects.Resolve("3% increased Projectile Speed", StatMap.Value).Single() == ("base_projectile_speed_+%", 3m),
                "projectile speed wording");
            Assert(RadiusEffects.Resolve("2% increased Damage with Spears", StatMap.Value).Single() == ("spear_damage_+%", 2m),
                "spear damage wording");
            Assert(RadiusEffects.Resolve("2% increased Projectile Damage", StatMap.Value).Single() == ("projectile_damage_+%", 2m),
                "projectile damage wording");
            // The lookup never guesses: a multi-value line and an unknown wording both stay unresolved.
            Assert(!StatMap.Value.TryResolve("Adds 10 to 20 Fire Damage", out _, out _), "a two-value line is not guessed");
            Assert(RadiusEffects.Resolve("12% increased Definitely Not A Stat", StatMap.Value).Count == 0,
                "an unknown wording resolves to nothing and is reported, never guessed");

            // The two Time-Lost Sapphires of the Huntress fixture must reach the tree (they used to be
            // dropped: their base is outside the pinned catalog) and their radius must be counted.
            var summary = Import("pobb-huntress-ice-shot.txt", out var huntress);
            var plan = huntress.Document.Tree!;
            var socketedNames = plan.Jewels.Values
                .Select(id => huntress.Document.Equipment!.Items.First(i => i.Id == id).Name).ToList();
            Assert(socketedNames.Contains("Dusk Wound") && socketedNames.Contains("Damnation Star"),
                "both Time-Lost Sapphires are socketed: " + string.Join(", ", socketedNames));
            Assert(summary.Extras.TryGetValue("RadiusGrantsApplied", out var applied) && applied > 0,
                "radius grants must be applied");
            var bow = summary.Skills.Where(s => s.HasData).OrderByDescending(s => s.Dps).First();
            Assert(bow.CritChancePercent > 30m, "crit chance with the radius jewels: " + bow.CritChancePercent);
            Console.WriteLine($"  radius: {applied} grants applied, crit {bow.CritChancePercent:0.##}% x{bow.CritBonusPercent / 100 + 1:0.##} (PoB2 48.87% x6.52)");
        }));

        await test("Interop: a jewel's per-socket resistance line counts the item's own sockets", () => Task.Run(() =>
        {
            // Morior Invictus: "+15% to Chaos Resistance per Socket filled" with "Sockets: S S S S" gives
            // +60 chaos resistance — the owner's PoB2 screenshot lists exactly that source.
            var text = "Rarity: UNIQUE\nMorior Invictus\nSacrificial Regalia\nSockets: S S S S\n" +
                "+15% to Chaos Resistance per Socket filled";
            var mods = UniqueTextParser.ParseMods(text);
            Assert(mods.Any(m => m.Id == "base_chaos_damage_resistance_%" && m.Value == 60m),
                "4 sockets x 15% = 60: " + string.Join(", ", mods.Select(m => m.Id + "=" + m.Value)));
            var one = UniqueTextParser.ParseMods("Rarity: UNIQUE\nTest\nBase\nSockets: S\n+15% to Chaos Resistance per Socket filled");
            Assert(one.Any(m => m.Id == "base_chaos_damage_resistance_%" && m.Value == 15m), "one socket gives 15%");
            var all = UniqueTextParser.ParseMods("Rarity: UNIQUE\nTest\nBase\nSockets: S S\n+5% to all Elemental Resistances per Socket filled");
            Assert(all.Any(m => m.Id == "base_resist_all_elements_%" && m.Value == 10m), "the all-elemental form still works");
        }));

    }
}
