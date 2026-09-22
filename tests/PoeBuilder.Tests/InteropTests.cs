using System.Text;
using System.Xml.Linq;
using PoeBuilder.App.Services;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Tree;

internal static class InteropTests
{
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }

    private static readonly Lazy<TreeCatalog> Tree = new(() => TreeCatalog.LoadPinned(Path.Combine(AppContext.BaseDirectory, "Data", "Tree", "data.json")));
    private static readonly Lazy<PoeBuilder.Core.Equipment.GameCatalog> Catalog = new(() => PoeBuilder.Core.Equipment.GameCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "catalog.json")));

    private static (int startId, int neighborId, string neighborStable) ClassPair(TreeCatalog tree)
    {
        var cls = tree.Classes[0];
        var start = tree.Nodes.Values.First(n => n.IsStart && n.ClassStarts.Contains(cls.Index));
        var neighbor = tree.Neighbors[start.Id].First(id => tree.Nodes[id].IsSupported);
        return (start.Id, neighbor, tree.Nodes[neighbor].StableId ?? throw new Exception("neighbor without stable id"));
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Interop: PoB envelope round-trips and stays URL-safe", () => Task.Run(() =>
        {
            var xml = "<PathOfBuilding><Build level=\"42\"/></PathOfBuilding>";
            var code = BuildInterop.EncodePobEnvelope(xml);
            Assert(!code.Contains('+') && !code.Contains('/') && !code.Contains('='), "alphabet must be URL-safe");
            Assert(Encoding.UTF8.GetString(BuildInterop.DecodePobEnvelope(code)) == xml, "round-trip must restore the payload");
            bool threw = false;
            try { BuildInterop.DecodePobEnvelope("!!!not base64!!!"); }
            catch (FormatException) { threw = true; }
            Assert(threw, "garbage must throw FormatException");

            var raw = Convert.FromBase64String(code.Replace('-', '+').Replace('_', '/') + new string('=', (4 - code.Length % 4) % 4));
            raw[^1] ^= 1;
            var corrupted = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            bool checksumRejected = false;
            try { BuildInterop.DecodePobEnvelope(corrupted); }
            catch (InvalidDataException) { checksumRejected = true; }
            Assert(checksumRejected, "corrupted Adler-32 must be rejected");
        }));

        await test("Interop: Build Planner JSON allocates passives and resolves the ascendancy", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var (startId, neighborId, neighborStable) = ClassPair(tree);
            var definition = tree.Ascendancies[0];
            var spark = catalog.Gems.Values.Where(g => g.Name == "Spark" && !g.Id.Contains("Unique", StringComparison.OrdinalIgnoreCase)).OrderBy(g => g.Id.Length).First();
            var support = catalog.Gems.Values.First(g => g.Kind == "support" && g.Levels.Contains(1));
            int level = spark.Levels.Contains(18) ? 18 : spark.Levels[0];
            string json = $$"""
{"name":"Import sample","ascendancy":"{{definition.Id}}","passives":["{{neighborStable}}"],
"skills":[{"id":"{{spark.Id}}","level_interval":[{{level}},20],"support_skills":["{{support.Id}}"]}]}
""";
            var imported = BuildInterop.ParseBuildJson(json, catalog, tree);
            Assert(imported.Report.PassivesMatched == 1, "passives " + imported.Report.PassivesMatched);
            Assert(imported.Report.PassivesUnknown == 0, "unknown passives " + imported.Report.PassivesUnknown);
            Assert(imported.Report.SkillsMatched == 1 && imported.Report.SupportsMatched == 1, "skills");
            Assert(imported.Document.Tree!.Ascendancy!.Id == definition.Id, "ascendancy id");
            var cls = tree.Classes.First(c => c.Index == definition.ClassIndex);
            Assert(imported.Document.CharacterClass == cls.Name, "class " + imported.Document.CharacterClass);
            var group = imported.Document.Skills!.Groups.Single();
            Assert(group.Active.GemId == spark.Id && group.Active.Level == level, "active gem level " + group.Active.Level);
            Assert(group.Supports.Single().GemId == support.Id, "support gem");
            Assert(imported.Document.Tree.AllocatedNodes.Contains(neighborId), "neighbor allocated");
        }));

        await test("Interop: poe.ninja-style payload (Mercenary3 + spell_criticals7) imports", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            Assert(tree.Ascendancies.Any(a => a.Id == "Mercenary3"), "Mercenary3 must exist in the pinned tree");
            var node = tree.Nodes.Values.FirstOrDefault(n => n.StableId == "spell_criticals7") ?? throw new Exception("spell_criticals7 missing");
            var totem = catalog.Gems.Values.FirstOrDefault(g => g.Id.EndsWith("SkillGemSpellTotem")) ?? throw new Exception("SpellTotem missing");
            string json = $$"""{"name":"ninja sample","ascendancy":"Mercenary3","passives":["spell_criticals7"],"skills":[{"id":"{{totem.Id}}"}]}""";
            var imported = BuildInterop.ParseBuildJson(json, catalog, tree);
            Assert(imported.Report.PassivesMatched == 1, "passives " + imported.Report.PassivesMatched);
            Assert(imported.Report.SkillsMatched == 1, "skills " + imported.Report.SkillsMatched);
            var expected = tree.Classes.First(c => c.Index == tree.Ascendancies.First(a => a.Id == "Mercenary3").ClassIndex);
            Assert(imported.Document.CharacterClass == expected.Name, "class " + imported.Document.CharacterClass);
            Assert(imported.Document.Tree!.ClassIndex == expected.Index, "class index");
        }));

        await test("Interop: PoB share code parses nodes, class and gems by name", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var (startId, neighborId, _) = ClassPair(tree);
            var cls = tree.Classes[0];
            var definition = tree.Ascendancies[0];
            var spark = catalog.Gems.Values.Where(g => g.Name == "Spark" && !g.Id.Contains("Unique", StringComparison.OrdinalIgnoreCase)).OrderBy(g => g.Id.Length).First();
            var support = catalog.Gems.Values.First(g => g.Kind == "support" && g.Levels.Contains(1));
            string xml = $$"""
<PathOfBuilding>
  <Build level="90" targetVersion="4_0" className="{{cls.Name}}" ascendancyClassName="{{definition.Name}}"/>
  <Tree activeSpec="0"><Spec nodes="{{startId}},{{neighborId}}"/></Tree>
  <Skills activeSkillSet="1"><SkillSet id="1"><Skill mainActiveSkill="1"><Gem nameSpec="{{spark.Name}}" skillId="{{spark.Id.Split('/').Last().Replace("SkillGem","")}}Player" level="20" quality="17" enabled="true"/><Gem nameSpec="{{support.Name}}" skillId="{{support.Id.Split('/').Last().Replace("SupportGem","Support")}}Player" level="1" quality="13" enabled="true"/></Skill></SkillSet></Skills>
</PathOfBuilding>
""";
            var imported = BuildInterop.ParsePobCode(BuildInterop.EncodePobEnvelope(xml), catalog, tree);
            Assert(imported.Report.PassivesMatched >= 2, "passives " + imported.Report.PassivesMatched);
            Assert(imported.Document.CharacterClass == cls.Name, "class " + imported.Document.CharacterClass);
            Assert(imported.Document.Level == 90, "level " + imported.Document.Level);
            var group = imported.Document.Skills!.Groups.Single();
            Assert(group.Active.GemId == spark.Id, "active by nameSpec/skillId");
            Assert(group.Supports.Single().GemId == support.Id, "support by nameSpec/skillId");
            Assert(group.Active.Level == 20 && group.Active.Quality == 17, "active level/quality from code");
            Assert(group.Supports.Single().Level == 1 && group.Supports.Single().Quality == 13, "support level/quality from code");
        }));

        await test("Interop: PoB active ItemSet selects only active gear", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var bases = catalog.Bases.Values.Where(b => b.ItemClass == "Body Armour").Take(2).ToArray();
            Assert(bases.Length == 2, "body armour fixtures");
            var cls = tree.Classes[0];
            XElement Item(string id, ItemBase item) => new("Item", new XAttribute("id", id), "Rarity: Normal\n" + item.Name + "\n");
            var xml = new XDocument(
                new XElement("PathOfBuilding",
                    new XElement("Build", new XAttribute("level", "1"), new XAttribute("className", cls.Name)),
                    new XElement("Tree", new XAttribute("activeSpec", "0"), new XElement("Spec", new XAttribute("nodes", ""))),
                    new XElement("Skills"),
                    new XElement("Items", new XAttribute("activeItemSet", "2"),
                        Item("1", bases[0]), Item("2", bases[1]),
                        new XElement("ItemSet", new XAttribute("id", "1"),
                            new XElement("Slot", new XAttribute("name", "Body Armour"), new XAttribute("itemId", "1"))),
                        new XElement("ItemSet", new XAttribute("id", "2"),
                            new XElement("Slot", new XAttribute("name", "Body Armour"), new XAttribute("itemId", "2"))))))
                .ToString(SaveOptions.DisableFormatting);

            var imported = BuildInterop.ParsePobCode(BuildInterop.EncodePobEnvelope(xml), catalog, tree);
            var equipment = imported.Document.Equipment!;
            Assert(equipment.Items.Length == 1, "only active set item imported: " + equipment.Items.Length);
            Assert(equipment.Slots.TryGetValue("Body", out var bodyId), "active body slot");
            Assert(equipment.Items.Single().Id == bodyId && equipment.Items.Single().BaseId == bases[1].Id,
                "active ItemSet 2 must win");
        }));

        await test("Interop: unknown ids are reported honestly, never silently dropped", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            string json = """{"name":"bad","passives":["zz_bogus_node","strength999"],"skills":[{"id":"Metadata/Items/Gems/NotAGemEver"}]}""";
            var imported = BuildInterop.ParseBuildJson(json, catalog, tree);
            Assert(imported.Report.PassivesUnknown >= 2, "unknown passives " + imported.Report.PassivesUnknown);
            Assert(imported.Report.GemsUnknown >= 1, "unknown gems " + imported.Report.GemsUnknown);
            Assert(imported.Report.UnknownIds.Contains("zz_bogus_node") && imported.Report.UnknownIds.Contains("Metadata/Items/Gems/NotAGemEver"), "unknown id list");
            Assert(imported.Document.Skills!.Groups.Length == 0, "no fabricated groups");
        }));

        await test("Interop: level_interval [0,100] clamps to a real gem level", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var spark = catalog.Gems.Values.Where(g => g.Name == "Spark" && !g.Id.Contains("Unique", StringComparison.OrdinalIgnoreCase)).OrderBy(g => g.Id.Length).First();
            string json = $$"""{"name":"interval","passives":[],"skills":[{"id":"{{spark.Id}}","level_interval":[0,100]}]}""";
            var imported = BuildInterop.ParseBuildJson(json, catalog, tree);
            var group = imported.Document.Skills!.Groups.Single();
            Assert(group.Active.Level >= 1 && spark.Levels.Contains(group.Active.Level), "level " + group.Active.Level);
        }));

        await test("Interop: the user's real PoB2 share code imports (fixture)", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pob-real.txt")).Trim();
            var imported = BuildInterop.ParsePobCode(code, catalog, tree);
            Assert(imported.Document.Tree!.Ascendancy!.Id == "Mercenary3", "ascendancy " + (imported.Document.Tree.Ascendancy?.Id ?? "none"));
            var asc = tree.Ascendancies.First(a => a.Id == "Mercenary3");
            Assert(imported.Document.CharacterClass == tree.Classes.First(c => c.Index == asc.ClassIndex).Name, "class " + imported.Document.CharacterClass);
            Assert(imported.Document.Level == 95, "level " + imported.Document.Level);
            Assert(imported.Report.PassivesMatched + imported.Report.PassivesUnknown == 130, "every node accounted: " + (imported.Report.PassivesMatched + imported.Report.PassivesUnknown));
            Assert(imported.Report.PassivesMatched == 130 && imported.Report.PassivesUnknown == 0, "passives " + imported.Report.PassivesMatched + "/" + imported.Report.PassivesUnknown);
            Assert(imported.Report.AscendancyNodesMatched == 10, "asc matched " + imported.Report.AscendancyNodesMatched);
            Assert(imported.Report.SkillsMatched == 17, "skills " + imported.Report.SkillsMatched);
            Assert(imported.Report.SupportsMatched == 38, "supports " + imported.Report.SupportsMatched);
            Assert(imported.Report.GemsUnknown == 0, "gems " + imported.Report.GemsUnknown);
            Console.WriteLine("POB2 FIXTURE: " + imported.Report + " UNKNOWN=[" + string.Join(", ", imported.Report.UnknownIds) + "]");
        }));

        await test("Interop 0.9.0: PoB jewels socket into tree nodes and 'Allocates' grants free nodes", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pob-real.txt")).Trim();
            var imported = BuildInterop.ParsePobCode(code, catalog, tree);
            var jewels = imported.Document.Tree!.Jewels;
            Assert(jewels.Count == 5, "socketed jewels " + jewels.Count);
            foreach (var nid in new[] { 7960, 21984, 26196, 55190, 61419 })
                Assert(jewels.ContainsKey(nid), "socket node " + nid);
            var free = imported.Document.Tree!.JewelAllocatedNodes;
            Assert(free.Length == 3 && free.Contains(60878) && free.Contains(53935) && free.Contains(16466), "megalomaniac grants " + string.Join(",", free));
            foreach (var nid in free) Assert(imported.Document.Tree!.AllocatedNodes.Contains(nid), "granted node allocated " + nid);
            var engine = new PoeBuilder.Core.Tree.PassiveTreeEngine(tree);
            engine.Validate(imported.Document.Tree!);
            int spentWithGrants = engine.Spent(imported.Document.Tree!);
            Assert(spentWithGrants == imported.Document.Tree!.AllocatedNodes.Length - free.Length, "grants are free: " + spentWithGrants);
        }));

        await test("Interop 0.8.0: PoB fixture carries equipment, jewels and uniques into the plan", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pob-real.txt")).Trim();
            var imported = BuildInterop.ParsePobCode(code, catalog, tree);
            Console.WriteLine("GEAR REPORT: matched=" + imported.Report.EquipmentMatched + " skipped=" + imported.Report.EquipmentSkippedLines + " jewels=" + imported.Report.JewelsImported + " uniques=" + imported.Report.UniquesImported);
            Assert(imported.Report.EquipmentMatched >= 1, "slotted gear " + imported.Report.EquipmentMatched);
            Assert(imported.Report.JewelsImported >= 1, "jewels " + imported.Report.JewelsImported);
            Assert(imported.Report.UniquesImported >= 1, "uniques " + imported.Report.UniquesImported);
            Assert(imported.Document.Equipment!.Items.Count() >= imported.Report.EquipmentMatched, "items in plan");
            foreach (var item in imported.Document.Equipment.Items)
                Assert(item.Rarity is "normal" or "magic" or "rare" or "unique", "rarity " + item.Name + " " + item.Rarity);
            var unique = imported.Document.Equipment.Items.FirstOrDefault(i => i.Rarity == "unique");
            if (unique is not null) Assert(unique.Notes.Length > 0, "unique keeps its full text");
        }));

        await test("Calc 0.8.0: the imported PoB build computes big real DPS (FlameWall tens per second)", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pob-real.txt")).Trim();
            var imported = BuildInterop.ParsePobCode(code, catalog, tree);
            var s = PoeBuilder.Core.Calculation.CharacterCalculator.Calculate(imported.Document, tree, null, catalog);
            Console.WriteLine("POB2 CALC: dps-top=" + string.Join("/", s.Skills.Where(k => k.HasData).OrderByDescending(k => k.Dps).Take(3).Select(k => k.GemName + " " + k.Dps.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " notes=[" + string.Join(",", k.NoteCodes) + "]")));
            Assert(s.Skills.Any(k => k.HasData && k.Dps > 100), "some skill has real damage from gear");
        }));

        await test("Comparison: imported PoB fixture stays within the recorded DPS baseline", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pob-real.txt")).Trim();
            var imported = BuildInterop.ParsePobCode(code, catalog, tree);
            var summary = PoeBuilder.Core.Calculation.CharacterCalculator.Calculate(imported.Document, tree, null, catalog);
            var flameblast = summary.Skills.FirstOrDefault(k => k.GemName == "Flameblast");
            Assert(flameblast is not null && flameblast.HasData, "Flameblast comparison result");
            const decimal pobDps = 3206m;
            decimal delta = flameblast!.Dps - pobDps;
            decimal relativeDelta = delta / pobDps * 100m;
            Console.WriteLine($"POB COMPARISON: Flameblast PoB={pobDps:0.0}/s PoeBuilder={flameblast.Dps:0.0}/s delta={delta:0.0} ({relativeDelta:0.00}%)");
            Console.WriteLine($"POB DEFENCE: life={summary.Life:0.0} es={summary.EnergyShield:0.0} armour={summary.Armour:0.0} evasion={summary.Evasion:0.0} " +
                $"fire={summary.FireRes:0.0} cold={summary.ColdRes:0.0} lightning={summary.LightRes:0.0} chaos={summary.ChaosRes:0.0}");
            Assert(Math.Abs(relativeDelta) <= 5m, "Flameblast delta exceeds comparison tolerance: " + relativeDelta);
        }));

        await test("Interop: the user's real poe.ninja JSON imports fully (fixture)", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ninja-real.json"));
            var imported = BuildInterop.ParseBuildJson(json, catalog, tree);
            Assert(imported.Document.Tree!.Ascendancy!.Id == "Mercenary3", "ascendancy");
            var asc = tree.Ascendancies.First(a => a.Id == "Mercenary3");
            Assert(imported.Document.CharacterClass == tree.Classes.First(c => c.Index == asc.ClassIndex).Name, "class " + imported.Document.CharacterClass);
            Assert(imported.Report.PassivesMatched + imported.Report.AscendancyNodesMatched + imported.Report.PassivesUnknown == 133, "every id accounted");
            Assert(imported.Report.PassivesMatched == 122 && imported.Report.AscendancyNodesMatched == 9 && imported.Report.PassivesUnknown == 2, "passives " + imported.Report.PassivesMatched + "+" + imported.Report.AscendancyNodesMatched + ", unknown " + imported.Report.PassivesUnknown);
            Assert(imported.Report.SkillsMatched == 14, "skills " + imported.Report.SkillsMatched);
            Assert(imported.Report.SupportsMatched == 38, "supports " + imported.Report.SupportsMatched);
            Assert(imported.Report.GemsUnknown == 0, "gems unknown " + imported.Report.GemsUnknown);
            Console.WriteLine("NINJA FIXTURE: " + imported.Report);
        }));

        await test("Interop: export → re-import keeps passives, ascendancy and skills", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var (startId, neighborId, _) = ClassPair(tree);
            var definition = tree.Ascendancies[0];
            var engine = new PassiveTreeEngine(tree);
            var plan = engine.Allocate(new() { DatasetId = tree.DatasetId, ClassIndex = tree.Classes[0].Index, PointLimit = 0 }, startId, BuildInterop.AttributeChoice);
            plan = engine.Allocate(plan, neighborId, BuildInterop.AttributeChoice);
            var ascEngine = new PassiveTreeEngine(definition.Graph);
            var ascStart = definition.Graph.Nodes.Values.First(n => n.IsAscendancyStart);
            var ascNeighbor = definition.Graph.Neighbors[ascStart.Id].First(id => definition.Graph.Nodes[id].IsSupported);
            var graphPlan = ascEngine.Allocate(definition.ToGraphPlan(new() { Id = definition.Id }), ascStart.Id, BuildInterop.AttributeChoice);
            graphPlan = ascEngine.Allocate(definition.ToGraphPlan(new() { Id = definition.Id, AllocatedNodes = [.. graphPlan.AllocatedNodes] }), ascNeighbor, BuildInterop.AttributeChoice);
            plan = plan with { Ascendancy = new() { Id = definition.Id, AllocatedNodes = [.. graphPlan.AllocatedNodes] } };
            var spark = catalog.Gems.Values.Where(g => g.Name == "Spark" && !g.Id.Contains("Unique", StringComparison.OrdinalIgnoreCase)).OrderBy(g => g.Id.Length).First();
            var doc = BuildDocument.Create("Round trip") with
            {
                CharacterClass = tree.Classes.First(c => c.Index == tree.Classes[0].Index).Name,
                Tree = plan,
                Skills = new() { Groups = [new() { Name = "Spark", Active = new() { GemId = spark.Id, Level = spark.Levels[0] }, Supports = [] }] }
            };
            string exported = BuildInterop.ExportBuildJson(doc, catalog, tree);
            var back = BuildInterop.ParseBuildJson(exported, catalog, tree);
            Assert(back.Report.PassivesMatched == plan.AllocatedNodes.Length,
                "passives " + back.Report.PassivesMatched + " vs " + plan.AllocatedNodes.Length);
            Assert(back.Report.AscendancyNodesMatched == plan.Ascendancy!.AllocatedNodes.Length,
                "asc nodes " + back.Report.AscendancyNodesMatched + " vs " + plan.Ascendancy.AllocatedNodes.Length);
            Assert(back.Report.PassivesUnknown == 0, "export must not produce unknown ids");
            Assert(back.Document.Tree!.Ascendancy!.Id == definition.Id, "ascendancy round-trip");
            Assert(back.Document.Skills!.Groups.Single().Active.GemId == spark.Id, "skill round-trip");
            Assert(exported.Contains("\"ascendancy\": \"" + definition.Id + "\""), "exported ascendancy field");
        }));
    }
}
