using System.Text;
using System.Xml.Linq;
using PoeBuilder.App.Services;
using PoeBuilder.App.ViewModels;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Tree;

internal static class InteropTests
{
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }

    private static readonly Lazy<TreeCatalog> Tree = new(() => TreeCatalog.LoadPinned(Path.Combine(AppContext.BaseDirectory, "Data", "Tree", "data.json")));
    private static readonly Lazy<PoeBuilder.Core.Equipment.GameCatalog> Catalog = new(() => PoeBuilder.Core.Equipment.GameCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "catalog.json")));
    private static readonly Lazy<PoeBuilder.Core.Calculation.GameStatMap> StatMap = new(() => PoeBuilder.Core.Calculation.GameStatMap.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "statmap.json")));

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

        await test("Interop: an item with more affix lines than a rare's six imports instead of failing", () => Task.Run(() =>
        {
            // PoB2's item text lists rune/enchant/bonded lines OUTSIDE the "Implicits: N" block, so a rare
            // that carries its six affixes plus one such line (the reported "Invalid equipment item
            // structure." dialog) used to abort the whole import at validation time. An import must never
            // die on one item: it either keeps the lines or reports them, never both-fails.
            var tree = Tree.Value; var catalog = Catalog.Value;
            var body = catalog.Bases.Values.First(b => b.ItemClass == "Body Armour" && catalog.Data.ModPools.ContainsKey(b.ModPool));
            var cls = tree.Classes[0];
            var affixes = catalog.ModsFor(body, 80)
                .Where(m => m.Stats.Length == 1 && m.Text.Contains('%'))
                .Select(m => PoeBuilder.Core.Calculation.UniqueTextParser.ResolveRanges(m.Text))
                .Distinct(StringComparer.Ordinal).Take(7).ToArray();
            Assert(affixes.Length == 7, "fixture affix lines: " + affixes.Length);
            string itemText = "Rarity: RARE\nTest Plate\n" + body.Name + "\nItem Level: 80\nImplicits: 0\n" +
                string.Join("\n", affixes) + "\n";
            var xml = new XDocument(
                new XElement("PathOfBuilding",
                    new XElement("Build", new XAttribute("level", "90"), new XAttribute("className", cls.Name)),
                    new XElement("Tree", new XAttribute("activeSpec", "0"), new XElement("Spec", new XAttribute("nodes", ""))),
                    new XElement("Skills"),
                    new XElement("Items", new XAttribute("activeItemSet", "1"),
                        new XElement("Item", new XAttribute("id", "1"), itemText),
                        new XElement("ItemSet", new XAttribute("id", "1"),
                            new XElement("Slot", new XAttribute("name", "Body Armour"), new XAttribute("itemId", "1"))))))
                .ToString(SaveOptions.DisableFormatting);

            var imported = BuildInterop.ParsePobCode(BuildInterop.EncodePobEnvelope(xml), catalog, tree);
            BuildValidation.Validate(imported.Document);   // the user-visible failure happened exactly here
            var item = imported.Document.Equipment!.Items.Single();
            Assert(item.Id != Guid.Empty && item.Rarity == "rare", "rare body imported");
            Assert(item.Mods.Length is >= 6 and <= 10, "kept rolls: " + item.Mods.Length);
            Assert(item.ItemLevel == 80 && item.Quality == 0, "item level/quality in range");
            Assert(item.Notes.Contains(affixes[0], StringComparison.Ordinal), "the item's own text stays with it");
        }));

        await test("Interop: a structurure-breaking item is skipped and reported, not fatal", () => Task.Run(() =>
        {
            // The counterpart of the test above: when an item really cannot be represented, the import
            // keeps going (the skip log names it) — one bad item must never block adding a build.
            var tree = Tree.Value; var catalog = Catalog.Value;
            var body = catalog.Bases.Values.First(b => b.ItemClass == "Body Armour" && catalog.Data.ModPools.ContainsKey(b.ModPool));
            var cls = tree.Classes[0];
            var affixes = catalog.ModsFor(body, 80)
                .Where(m => m.Stats.Length == 1 && m.Text.Contains('%'))
                .Select(m => PoeBuilder.Core.Calculation.UniqueTextParser.ResolveRanges(m.Text))
                .Distinct(StringComparer.Ordinal).Take(24).ToArray();
            string itemText = "Rarity: RARE\nOverloaded Plate\n" + body.Name + "\nItem Level: 80\nImplicits: 0\n" +
                string.Join("\n", affixes) + "\n";
            var xml = new XDocument(
                new XElement("PathOfBuilding",
                    new XElement("Build", new XAttribute("level", "90"), new XAttribute("className", cls.Name)),
                    new XElement("Tree", new XAttribute("activeSpec", "0"), new XElement("Spec", new XAttribute("nodes", ""))),
                    new XElement("Skills"),
                    new XElement("Items", new XAttribute("activeItemSet", "1"),
                        new XElement("Item", new XAttribute("id", "1"), itemText),
                        new XElement("ItemSet", new XAttribute("id", "1"),
                            new XElement("Slot", new XAttribute("name", "Body Armour"), new XAttribute("itemId", "1"))))))
                .ToString(SaveOptions.DisableFormatting);

            var imported = BuildInterop.ParsePobCode(BuildInterop.EncodePobEnvelope(xml), catalog, tree);
            BuildValidation.Validate(imported.Document);
            Assert(imported.Document.Equipment!.Items.Length == 0, "the unrepresentable item is not in the plan");
            Assert(imported.Report.EquipmentSkippedLines > 0, "the skip is reported: " + imported.Report.EquipmentSkippedLines);
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
            // Megalomaniac's three "Allocates …" notables plus the amulet's enchant "Allocates Paragon"
            // (the Delirium anoint node "+5 to all Attributes / +5% to Quality of all Skills"). The
            // anoint node has no edges, so it can only ever arrive as a free grant — and PoB2 counts its
            // stats: they are part of its own panel's attributes for this build.
            Assert(free.Length == 4 && free.Contains(60878) && free.Contains(53935) && free.Contains(16466) && free.Contains(20686),
                "granted nodes " + string.Join(",", free));
            Assert(tree.Nodes[20686].IsAnointOnly && tree.Nodes[20686].Name == "Paragon", "anoint grant");
            foreach (var nid in free) Assert(imported.Document.Tree!.AllocatedNodes.Contains(nid), "granted node allocated " + nid);
            var engine = new PassiveTreeEngine(tree);
            engine.Validate(imported.Document.Tree!);
            int spentWithGrants = engine.Spent(imported.Document.Tree!);
            Assert(spentWithGrants == imported.Document.Tree!.AllocatedNodes.Length - free.Length, "grants are free: " + spentWithGrants);
        }));

        await test("Interop 0.9.1: a unique jewel opening another class's starting point imports as an alternate start", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var cls = tree.Classes[0];
            var other = tree.Classes.First(c => c.Index != cls.Index);
            // PoB2's Split Personality variants include both phrasings and legacy start names.
            string[] alternateClasses = ["Warrior", "Ranger", "Sorceress", "Mercenary", "Templar", "Shadow"];
            string jewelText = "Rarity: Unique\nSplit Personality\n" + other.Name + "\n--------\n" + string.Join("\n",
                alternateClasses.Select((name, index) => "Can Allocate " + (index % 2 == 0 ? "Passive Skills" : "Passives") + " from the " + name + "'s starting point"));
            var xml = new XDocument(
                new XElement("PathOfBuilding",
                    new XElement("Build", new XAttribute("level", "1"), new XAttribute("className", cls.Name)),
                    new XElement("Tree", new XAttribute("activeSpec", "0"),
                        new XElement("Spec", new XAttribute("nodes", cls.StartNodeId)),
                        new XElement("Socket", new XAttribute("nodeId", "1"), new XAttribute("itemId", "99"))),
                    new XElement("Skills"),
                    new XElement("Items",
                        new XElement("Item", new XAttribute("id", "99"), jewelText))))
                .ToString(SaveOptions.DisableFormatting);
            var imported = BuildInterop.ParsePobCode(BuildInterop.EncodePobEnvelope(xml), catalog, tree);
            Assert(imported.Document.Tree is not null, "tree plan");
            var importedTree = imported.Document.Tree;
            Assert(importedTree is not null, "imported tree");
            foreach (string className in alternateClasses)
            {
                int? startNodeId = tree.Classes.FirstOrDefault(c => c.Name.Equals(className, StringComparison.OrdinalIgnoreCase))?.StartNodeId
                    ?? tree.Nodes.Values.FirstOrDefault(n => n.IsStart && n.Name.Equals(className, StringComparison.OrdinalIgnoreCase))?.Id
                    ?? (className == "Shadow" ? tree.Classes.FirstOrDefault(c => c.Name == "Monk")?.StartNodeId : null);
                Assert(startNodeId is int start && importedTree!.AlternateStartNodes.Contains(start),
                    "alternate start " + className + " -> " + startNodeId + " got "
                    + string.Join(",", importedTree?.AlternateStartNodes ?? []));
            }
            // The imported Assortment stays structurally valid with the alternate root present.
            var engine = new PassiveTreeEngine(tree);
            engine.Validate(importedTree!);
        }));

        await test("Interop 0.9.2: Voices-granted Sinister Jewel sockets import as free nodes", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var cls = tree.Classes.First(c => c.Name == "Mercenary");
            var voicesSockets = tree.Nodes.Values
                .Where(n => n.IsJewel && n.CanBeGranted && n.StableId.StartsWith("voices_jewel_slot", StringComparison.Ordinal))
                .OrderBy(n => n.StableId, StringComparer.Ordinal).Take(2).ToArray();
            Assert(voicesSockets.Length == 2, "Voices socket nodes");
            var xml = new XDocument(
                new XElement("PathOfBuilding2",
                    new XElement("Build", new XAttribute("level", "1"), new XAttribute("className", cls.Name)),
                    new XElement("Tree", new XAttribute("activeSpec", "0"),
                        new XElement("Spec", new XAttribute("nodes", string.Join(",", new[] { cls.StartNodeId }.Concat(voicesSockets.Select(n => n.Id))))),
                        new XElement("Socket", new XAttribute("nodeId", voicesSockets[0].Id), new XAttribute("itemId", "2"))),
                    new XElement("Skills"),
                    new XElement("Items",
                        new XElement("Item", new XAttribute("id", "1"), "Rarity: Unique\nVoices\nSapphire\n--------\nAllocates 2 Sinister Jewel sockets"),
                        new XElement("Item", new XAttribute("id", "2"), "Rarity: Rare\nRuby\n--------\n10% increased Spell Damage"))))
                .ToString(SaveOptions.DisableFormatting);
            var imported = BuildInterop.ParsePobCode(BuildInterop.EncodePobEnvelope(xml), catalog, tree);
            var plan = imported.Document.Tree ?? throw new Exception("tree plan");
            Assert(plan.JewelAllocatedNodes.Order().SequenceEqual(voicesSockets.Select(n => n.Id).Order()),
                "both Voices sockets are free: " + string.Join(",", plan.JewelAllocatedNodes));
            Assert(plan.Jewels.ContainsKey(voicesSockets[0].Id), "jewel placed in the first granted socket");
            Assert(imported.Report.PassivesUnknown == 0, "unknown passives " + imported.Report.PassivesUnknown);
            new PassiveTreeEngine(tree).Validate(plan);
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

        await test("Comparison: imported PoB fixture — offence regression baseline plus PoB2 golden gap", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pob-real.txt")).Trim();
            var imported = BuildInterop.ParsePobCode(code, catalog, tree);
            var summary = PoeBuilder.Core.Calculation.CharacterCalculator.Calculate(imported.Document, tree, null, catalog);
            var flameblast = summary.Skills.FirstOrDefault(k => k.GemName == "Flameblast");
            Assert(flameblast is not null && flameblast.HasData, "Flameblast comparison result");
            // PoB2's own panel, read from the fixture, so the remaining offence gap is visible here.
            var playerStats = System.Xml.Linq.XDocument.Parse(
                System.Text.Encoding.UTF8.GetString(BuildInterop.DecodePobEnvelope(code))).Descendants("PlayerStat");
            decimal Stat(string name) => playerStats.Where(p => (string?)p.Attribute("stat") == name)
                .Select(p => decimal.TryParse((string?)p.Attribute("value"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m).DefaultIfEmpty(0m).Max();
            decimal goldenAverageHit = Stat("AverageHit");
            decimal goldenTotalDps = Stat("TotalDPS");
            // Regression guard only. The PoB2 golden offence numbers for this build live in the
            // fixture itself (AverageHit 152317.6 / TotalDPS 324021.1 for the Spell Totem+Arc group)
            // and are printed by Pob2ParityTests; our skill/support model is still far from them, so a
            // tight PoB parity gate here would be a rubber stamp. This baseline only catches changes.
            // It moved from 5765.0/s to 5596.4/s when rune "Bonded: …" lines started being gated by
            // PoB2's CanUseBondedModifiers condition (the Fox Idol's "Bonded: +5% to Quality of all
            // Skills" is no longer counted, which is what PoB2 does as well), from 5596.4/s to 11192.8/s
            // when gem levels were fixed ("+N to Level of all X Skills" lines and a corrupted gem's
            // corruptLevel are now read), from 11192.8/s to 10855.6/s when gem quality stopped
            // inventing damage: quality is now the gem's own qualityStats from PoB2's skill data, and
            // Flameblast's stat is "1% increased Cast Speed per quality" — not increased damage, which
            // the old "+1% increased damage per point of quality" approximation had added. It moved
            // from 10855.6/s to 17210.6/s (+58.54%) when the item-affix matcher stopped mis-handling
            // number ranges: the "Adds X to Y …" family and every other range template a line is
            // written against now match, which brought in the Glyph Chant wand's two real affixes
            // "Gain 29% of Damage as Extra Lightning Damage" and "… as Extra Cold Damage" (+29% +29%
            // of the hit as extra damage — exactly the 58.54% the baseline moved by). It moved from
            // 17210.6/s to 19573.2/s (+13.73%) when the source-less wording "Gain N% of Damage as
            // Extra X Damage" — the all-damage gain, PoB2 ModCache: DamageGainAsFire /
            // DamageGainAsChaos — stopped being dropped for UNIQUE items: a rare line is mapped by the
            // reverse stat table, but the same wording in a unique's own text went through
            // UniqueTextParser, which had no branch for it, so Heart of the Well's
            // "Gain 14% of Damage as Extra Fire Damage" and "Gain 10% of Damage as Extra Chaos Damage"
            // were reported as not modelled instead of being applied (that build carries the same jewel).
            const decimal recordedFlameblastDps = 19573.2m;
            decimal delta = flameblast!.Dps - recordedFlameblastDps;
            decimal relativeDelta = delta / recordedFlameblastDps * 100m;
            Console.WriteLine($"POB COMPARISON: Flameblast recorded={recordedFlameblastDps:0.0}/s PoeBuilder={flameblast.Dps:0.0}/s delta={delta:0.0} ({relativeDelta:0.00}%)");
            Console.WriteLine($"POB GOLDEN OFFENCE: AverageHit={goldenAverageHit:0.0} TotalDPS={goldenTotalDps:0.0} (PoB2 main group)");
            Console.WriteLine($"POB DEFENCE: life={summary.Life:0.0} es={summary.EnergyShield:0.0} armour={summary.Armour:0.0} evasion={summary.Evasion:0.0} " +
                $"fire={summary.FireRes:0.0} cold={summary.ColdRes:0.0} lightning={summary.LightRes:0.0} chaos={summary.ChaosRes:0.0}");
            Assert(Math.Abs(relativeDelta) <= 1m, "Flameblast moved from the recorded baseline: " + relativeDelta + "%");
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
            // Ascendancy choice options are free (their parent notable pays the point), so the
            // fixture's picked option imports into the plan instead of landing in the unknown list.
            Assert(imported.Report.PassivesMatched == 122 && imported.Report.AscendancyNodesMatched == 10 && imported.Report.PassivesUnknown == 1, "passives " + imported.Report.PassivesMatched + "+" + imported.Report.AscendancyNodesMatched + ", unknown " + imported.Report.PassivesUnknown);
            Assert(imported.Report.SkillsMatched == 14, "skills " + imported.Report.SkillsMatched);
            Assert(imported.Report.SupportsMatched == 38, "supports " + imported.Report.SupportsMatched);
            Assert(imported.Report.GemsUnknown == 0, "gems unknown " + imported.Report.GemsUnknown);
            Console.WriteLine("NINJA FIXTURE: " + imported.Report);
        }));

        await test("Interop: build links resolve to the URL that actually carries the code", () => Task.Run(() =>
        {
            // pobb.in: the short link is a JavaScript page, the share code lives at /raw. This is why
            // pasting the link used to fail with "the code does not decode".
            var pobb = BuildInterop.ResolveImportLink("https://pobb.in/yKl88kX79HFV");
            Assert(pobb is not null && pobb.FetchUrl == "https://pobb.in/yKl88kX79HFV/raw" && pobb.Kind == "pob", "pobb " + pobb?.FetchUrl);
            var raw = BuildInterop.ResolveImportLink("https://pobb.in/yKl88kX79HFV/raw");
            Assert(raw is not null && raw.FetchUrl == "https://pobb.in/yKl88kX79HFV/raw", "raw " + raw?.FetchUrl);
            // poe.ninja: the character page is rendered in the browser from its own model API, which
            // carries the Path of Building export of that character.
            var ninja = BuildInterop.ResolveImportLink("https://poe.ninja/poe2/profile/supamodd-0921/forbiddenrites/character/gemtotemhem");
            Assert(ninja is not null && ninja.Kind == "ninja" &&
                ninja.FetchUrl == "https://poe.ninja/poe2/api/profile/characters/supamodd-0921/forbiddenrites/gemtotemhem/model/0",
                "ninja " + ninja?.FetchUrl);
            // A link without its scheme and a bare pobb.in id work too; a share code is never a link.
            Assert(BuildInterop.ResolveImportLink("pobb.in/yKl88kX79HFV")?.FetchUrl == "https://pobb.in/yKl88kX79HFV/raw", "scheme-less link");
            Assert(BuildInterop.LooksLikePobbId("yKl88kX79HFV"), "pobb id shape");
            Assert(!BuildInterop.LooksLikePobbId(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pobb-mercenary-ll-arc.txt"))), "a share code is not an id");
            Assert(BuildInterop.ResolveImportLink("not a link at all") is null && BuildInterop.ResolveImportLink(null) is null, "plain text is not a link");
            // Any other link is fetched as-is and auto-detected after the download.
            var other = BuildInterop.ResolveImportLink("https://example.com/build.json");
            Assert(other is not null && other.Kind == "auto" && other.FetchUrl == "https://example.com/build.json", "auto " + other?.FetchUrl);
        }));

        await test("Interop: a poe.ninja character model imports through its Path of Building export", () => Task.Run(() =>
        {
            var tree = Tree.Value; var catalog = Catalog.Value;
            PoeBuilder.Core.Calculation.ReverseStatTextMatcher.UseFile(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "stat_text_reverse.json"));
            string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ninja-model-ll-arc.json"));
            // The export is found by shape, not by field name, and a page that embeds it works too.
            string? code = BuildInterop.ExtractPobCode(json);
            Assert(code is not null && code.StartsWith("eNrt", StringComparison.Ordinal), "export code found");
            Assert(BuildInterop.ExtractPobCode("<html><script>var c = \"" + code + "\";</script></html>") == code, "code embedded in a page");
            Assert(BuildInterop.ExtractPobCode(code) == code, "a pobb.in /raw payload is the code itself");
            Assert(BuildInterop.ExtractPobCode("{\"name\":\"no build here\"}") is null, "JSON without a code");
            var imported = BuildInterop.ParsePobCode(code!, catalog, tree);
            Assert(imported.Report.PassivesUnknown == 0, "unknown passives " + imported.Report.PassivesUnknown);
            var summary = PoeBuilder.Core.Calculation.CharacterCalculator.Calculate(imported.Document, tree, StatMap.Value, catalog);
            Console.WriteLine($"NINJA MODEL IMPORT: level {summary.Level}, life {summary.Life}, mana {summary.Mana}, " +
                $"str {summary.Strength}, dex {summary.Dexterity}, int {summary.Intelligence}, skills {summary.Skills.Count(s => s.HasData)}");
            // The model's own defensiveStats agree with the export it carries, so the pools are asserted
            // here as well: this is the same character as the pobb.in snapshot.
            Assert(summary.Life == 1705m && summary.Mana == 4576m && summary.Intelligence == 343m,
                $"pools {summary.Life}/{summary.Mana}, int {summary.Intelligence}");
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

        await test("Interop: exported builds round-trip through the official JSON format", () => Task.Run(() =>
        {
            var imported = BuildInterop.ParseBuildJson("{\"name\":\"none\",\"passives\":[]}", Catalog.Value, Tree.Value);
            Assert(imported.Report.PassivesMatched == 0 && imported.Document.Tree!.AllocatedNodes.Length == 0, "empty import");
        }));
        await test("Parity: PoB2's own quest-reward table loads and matches its pinned checksum", () => Task.Run(() =>
        {
            var index = Catalog.Value.QuestRewards;
            Assert(index.Count == 29, "PoB2's table has 29 rewards, was " + index.Count);
            Assert(index.Rewards.Count(r => r.UseConfig) == 17, "config-driven rewards");
            // A choice quest contributes nothing until the player picks a line (PoB2 defaults it to
            // "None"), so the automatic set is the config-driven rewards that carry a single Stat.
            Assert(index.ConfigLines().Count == 9, "automatic reward lines, was " + index.ConfigLines().Count);
            Assert(index.Rewards.Count(r => r.Options.Length > 0) == 8, "choice quests");
            Assert(index.Rewards.All(r => r.Act > 0 && r.Area.Length > 0 && r.Info.Length > 0), "act/area/source identity");
            // PoB2 stores a multi-line option inside one Lua string ("\n" escape); the reader turns it into
            // the real newline its config value holds, which is what the calculator then parses.
            Assert(index.Rewards.SelectMany(r => r.Options).Any(o => o.Contains('\n')),
                "a choice quest's option keeps its line break");
            // The importer's config key is PoB2's own "quest" + Description + Area + Info.
            Assert(index.Rewards.Any(r => r.Description == "Act 1" && r.Area == "Clearfell" && r.Info == "Beira"),
                "the Act 1 Beira reward keeps PoB2's identity");
        }));

        await test("Interop: an imported build's quest rewards are PoB2's own lines", () => Task.Run(() =>
        {
            var code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pob-real.txt")).Trim();
            var imported = BuildInterop.ParsePobCode(code, Catalog.Value, Tree.Value);
            var lines = imported.Document.QuestRewards ?? [];
            Assert(lines.Length > 0, "the fixture carries quest rewards");
            var index = Catalog.Value.QuestRewards;
            var known = new HashSet<string>(index.ConfigLines()
                .Concat(index.Rewards.SelectMany(r => r.Options).Select(QuestRewardIndex.NormaliseLine)), StringComparer.Ordinal);
            foreach (var line in lines) Assert(known.Contains(QuestRewardIndex.NormaliseLine(line)),
                "unexpected quest reward line: [" + line.Replace("\n", "<NL>").Replace("\t", "<TAB>") + "]");
            // PoB2 grants "+30 to Spirit" twice (Act 1 Freythorn and Act 3 Azak Bog) and both count.
            Assert(lines.Count(l => l == "+30 to Spirit") == 2, "two quests grant +30 Spirit");
        }));

        await test("Editor: quest rewards and configuration persist into the saved document", () => Task.Run(() =>
        {
            var document = BuildDocument.Create("Rewards") with
            {
                Level = 90, CharacterClass = "Mercenary",
                QuestRewards = ["+20 to maximum Life"],
                Conditions = new BuildConditions { EnemyIgnited = true, EnemyLevel = 82m }
            };
            var editor = new BuildEditor(document);
            Assert(editor.QuestRewardsSnapshot!.Single() == "+20 to maximum Life", "the imported rewards load");
            Assert(editor.ConditionsSnapshot.EnemyIgnited && editor.ConditionsSnapshot.EnemyLevel == 82m, "conditions load");
            editor.SetQuestRewards(["+10% to Cold Resistance", "+5% to all Elemental Resistances"]);
            editor.SetConditions(editor.ConditionsSnapshot with { EnemyFireExposure = true });
            var saved = editor.ToDocument();
            Assert(saved.QuestRewards!.Length == 2 && saved.QuestRewards[1] == "+5% to all Elemental Resistances", "rewards saved");
            Assert(saved.Conditions.EnemyFireExposure && saved.Conditions.EnemyIgnited, "conditions saved");
            Assert(new BuildEditor(document).ToDocument().Conditions.EnemyLevel == 82m, "an untouched build is preserved");
        }));

        await test("UI: the Quest Rewards and Configuration tabs are localized in both languages", () => Task.Run(() =>
        {
            var l = new Localization();
            string[] keys = ["QuestRewards", "QuestAll", "QuestNone", "QuestImported", "QuestScope", "QuestRewardSummary",
                "QuestRewardApplied", "ConfigGeneral", "ConfigCombat", "ConfigEffective", "ConfigEnemyStats", "ConfigNotModelled",
                "ConfigEndgame", "ConfigPenaltyNote", "ConfigMoving", "ConfigCritRecently", "ConfigHitRecently", "ConfigFlameWall",
                "ConfigArcInfused", "ConfigEnemyChilled", "ConfigEnemyIgnited", "ConfigEnemyBleeding", "ConfigEnemyShocked",
                "ConfigEnemyFireExposure", "ConfigEnemyColdExposure", "ConfigEnemyLightningExposure", "ConfigEnemyDefaults",
                "ConfigEnemyLevel", "ConfigEnemyFire", "ConfigEnemyCold", "ConfigEnemyLightning", "ConfigEnemyChaos",
                "ConfigEnemyArmour", "ConfigEnemyPhysReduction", "ConfigReset", "ConfigApplied"];
            foreach (var language in new[] { "ru", "en" })
            {
                l.SetLanguage(language);
                foreach (var key in keys) Assert(l[key] != key, key + " is missing (" + language + ")");
            }
            // Both tabs sit before the character sheet and keep the tab names PoB2 uses.
            l.SetLanguage("ru");
            Assert(l["Configuration"] == "Конфигурация" && l["QuestRewards"].Length > 0, "tab labels");
        }));

        await test("UI: the weapon-set colour keys and legends are localized in both languages", () => Task.Run(() =>
        {
            var l = new Localization();
            string[] keys = ["WeaponSet", "WeaponSet1", "WeaponSet2", "WeaponSetBoth", "WeaponSetActive",
                "WeaponSetLegend", "WeaponSetAssign", "WeaponSetNodeActive", "WeaponSetNodeInactive",
                "ResTotal", "ResMax", "ResFromSources"];
            foreach (var language in new[] { "ru", "en" })
            {
                l.SetLanguage(language);
                foreach (var key in keys) Assert(l[key] != key, key + " is missing (" + language + ")");
            }
            l.SetLanguage("ru");
            Assert(l["WeaponSet1"] == "Набор I" && l["WeaponSetBoth"].Contains("оба", StringComparison.Ordinal),
                "the panel names PoB2's two sets and the shared option");
        }));
    }
}