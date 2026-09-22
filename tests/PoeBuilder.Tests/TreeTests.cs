using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using PoeBuilder.App.ViewModels;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Storage;
using PoeBuilder.Core.Tree;

internal static class TreeTests
{
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static void Rule(string code, Action action)
    {
        try { action(); } catch (TreeRuleException e) when (e.Code == code) { return; }
        throw new Exception($"Expected tree rule: {code}");
    }
    private static PassiveNode Node(int id, bool attribute = false, bool locked = false, bool mastery = false, bool ascendancy = false, bool anoint = false, int[]? starts = null) =>
        new(id, "fixture_" + id, "Node " + id, "", [], id * 100, 0, 1, false, false, false, attribute, mastery, ascendancy, locked, anoint, starts ?? []);
    private static TreeCatalog Fixture()
    {
        var nodes = new[] { Node(1, starts: [6]), Node(2), Node(3, attribute: true), Node(4), Node(5), Node(6, starts: [2]), Node(7, locked: true), Node(8), Node(9, mastery: true), Node(10, ascendancy: true), Node(11, anoint: true), Node(12) }.ToDictionary(n => n.Id);
        (int From, int To)[] links = [(1, 2), (2, 3), (3, 4), (2, 5), (4, 5), (1, 6), (6, 8), (1, 7), (7, 8), (1, 9), (9, 8), (1, 10), (10, 8), (1, 11), (11, 8)];
        var variants = new[] { new PassiveVariant(26297, "Strength", "", ["+5 to Strength"]), new PassiveVariant(14927, "Dexterity", "", ["+5 to Dexterity"]), new PassiveVariant(57022, "Intelligence", "", ["+5 to Intelligence"]) }.ToDictionary(v => v.Id);
        return new("fixture", nodes, [new(6, "Warrior", 1, new Dictionary<int, int>()), new(2, "Ranger", 6, new Dictionary<int, int>())], links.Select(e => new TreeEdge(e.From, e.To, null, null)).ToArray(), variants);
    }
    public static async Task Run(Func<string, Func<Task>, Task> test, string tempRoot)
    {
        var fixture = Fixture(); var engine = new PassiveTreeEngine(fixture); var empty = new PassiveTreePlan { DatasetId = "fixture" };
        Task Check(Action action) { action(); return Task.CompletedTask; }
        await test("Tree: shortest path charges only new nodes and does not mutate input", () => Check(() =>
        {
            Assert(engine.FindPath(empty, 4).SequenceEqual([2, 3, 4]));
            var first = engine.Allocate(empty, 3, 14927);
            Assert(first.AllocatedNodes.SequenceEqual([2, 3]) && first.AttributeSelections[3] == 14927 && empty.AllocatedNodes.Length == 0);
            Assert(engine.FindPath(first, 4).SequenceEqual([4])); Assert(engine.FindPath(first, 3).Length == 0);
            Assert(engine.FindPath(empty, 1).Length == 0);
        }));
        await test("Tree: manual budget failure is atomic", () => Check(() =>
        {
            var limited = empty with { PointLimit = 2 };
            Rule("TreeOverBudget", () => engine.Allocate(limited, 4, 26297));
            Assert(limited.AllocatedNodes.Length == 0 && limited.AttributeSelections.Count == 0);
            Assert(engine.Allocate(limited, 3, 57022).AllocatedNodes.Length == 2);
        }));
        await test("Tree: other class starts and special nodes cannot be targets or shortcuts", () => Check(() =>
        {
            foreach (int id in new[] { 6, 7, 9, 10, 11 }) Rule("TreeUnsupported", () => engine.FindPath(empty, id));
            Rule("TreeNoPath", () => engine.FindPath(empty, 8)); Rule("TreeNoPath", () => engine.FindPath(empty, 12));
            Assert(engine.FindPath(empty with { ClassIndex = 2 }, 8).SequenceEqual([8]));
            Rule("TreeInvalidSaved", () => engine.Validate(empty with { AllocatedNodes = [1] }));
        }));
        await test("Tree: refund prunes disconnected branches and attribute choices", () => Check(() =>
        {
            var p = engine.Allocate(empty, 4, 26297);
            Assert(engine.RefundSet(p, 2).SequenceEqual([2, 3, 4]));
            var cleared = engine.Refund(p, 2); Assert(cleared.AllocatedNodes.Length == 0 && cleared.AttributeSelections.Count == 0);
            Assert(engine.RefundSet(p, 3).SequenceEqual([3, 4]));
            Assert(engine.Refund(p, 1).AllocatedNodes.SequenceEqual(p.AllocatedNodes));
        }));
        await test("Tree: alternate connection survives a refund", () => Check(() =>
        {
            var p = engine.Allocate(engine.Allocate(empty, 4, 26297), 5, 26297);
            Assert(engine.RefundSet(p, 3).SequenceEqual([3]));
            var next = engine.Refund(p, 3); Assert(next.AllocatedNodes.SequenceEqual([2, 4, 5])); engine.Validate(next);
        }));
        await test("Tree: attribute switching preserves graph IDs and clones choices", () => Check(() =>
        {
            var p = engine.Allocate(empty, 3, 26297); var next = engine.SetAttribute(p, 3, 57022);
            Assert(next.AttributeSelections[3] == 57022 && p.AttributeSelections[3] == 26297);
            Assert(next.AllocatedNodes.SequenceEqual([2, 3]));
            Assert(fixture.Describe(3, next).Name == "Intelligence");
            Rule("TreeInvalidAttribute", () => engine.SetAttribute(next, 2, 26297));
            Rule("TreeInvalidAttribute", () => engine.Allocate(empty, 3, 999));
        }));
        await test("Tree: invalid saved graph states are blocked", () => Check(() =>
        {
            Rule("TreeDatasetMismatch", () => engine.Validate(empty with { DatasetId = "future" }));
            Rule("TreeInvalidClass", () => engine.Validate(empty with { ClassIndex = 20 }));
            Rule("TreeDisconnected", () => engine.Validate(empty with { AllocatedNodes = [4] }));
            Rule("TreeInvalidSaved", () => engine.Validate(empty with { AllocatedNodes = [65000] }));
            Rule("TreeInvalidAttribute", () => engine.Validate(empty with { AllocatedNodes = [2, 3] }));
            Rule("TreeInvalidAttribute", () => engine.Validate(empty with { AttributeSelections = new() { [2] = 26297 } }));
        }));
        await test("Tree: undo redo uses deep snapshots and drops abandoned future", () => Check(() =>
        {
            var history = new TreeHistory(); var a = engine.Allocate(empty, 3, 26297);
            history.Record(empty); history.Record(a); a.AttributeSelections[3] = 57022;
            var b = engine.Allocate(a, 4, 26297); var previous = history.Undo(b);
            Assert(previous.AttributeSelections[3] == 26297 && previous.AllocatedNodes.Length == 2);
            var again = history.Redo(previous); Assert(again.AllocatedNodes.Length == 3 && again.AttributeSelections[3] == 57022);
            history.Undo(again); history.Record(previous); Assert(!history.CanRedo); history.Clear(); Assert(!history.CanUndo);
        }));
        await test("Tree: undo memory is bounded to 100 snapshots", () => Check(() =>
        {
            var history = new TreeHistory(); for (int i = 0; i < 130; i++) history.Record(empty with { PointLimit = i });
            int count = 0; var current = empty; while (history.CanUndo) { current = history.Undo(current); count++; }
            Assert(count == 100 && current.PointLimit == 30);
        }));
        var folder = Path.Combine(AppContext.BaseDirectory, "Data", "Tree");
        TreeCatalog? real = null;
        await test("GGG: pinned 0.5.5 export loads real records and released class list", () => Check(() =>
        {
            real = TreeCatalog.LoadPinned(Path.Combine(folder, "data.json"));
            Assert(real.ExportNodeCount == 5153, $"raw records: {real.ExportNodeCount}");
            Assert(real.Nodes.Count == 4483, $"main records: {real.Nodes.Count}");
            Assert(real.Classes.Select(c => c.Index).SequenceEqual([1, 2, 6, 7, 8, 9, 10, 11]));
            Assert(real.Classes.Single(c => c.Index == 6).StartNodeId == 47175);
            Assert(real.Nodes.Values.All(n => !n.IsAscendancy) && real.Nodes.Values.Count(n => n.IsStart) == 6);
            Assert(real.Neighbors.Values.All(ids => !ids.Contains(0)));
        }));
        await test("GGG: every shipped asset matches the provenance manifest", async () =>
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "manifest.json")));
            foreach (var asset in manifest.RootElement.GetProperty("files").EnumerateObject())
            {
                var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(folder, asset.Name))));
                Assert(hash.Equals(asset.Value.GetString(), StringComparison.OrdinalIgnoreCase), asset.Name);
            }
        });
        await test("GGG: altered tree data is refused before parsing", async () =>
        {
            var path = Path.Combine(tempRoot, "altered-tree.json"); await File.WriteAllTextAsync(path, "{}");
            try { TreeCatalog.LoadPinned(path); throw new Exception("Altered data accepted"); } catch (InvalidDataException) { }
        });
        await test("GGG: all eight class starts allocate and refund a valid route", () => Check(() =>
        {
            Assert(real is not null, "GGG snapshot did not load"); var graph = new PassiveTreeEngine(real!);
            foreach (var cls in real!.Classes)
            {
                var plan = new PassiveTreePlan { ClassIndex = cls.Index };
                graph.Validate(plan);
                var neighbor = real.Neighbors[cls.StartNodeId].First(id => graph.CanTraverse(id, plan) && !real.Nodes[id].IsStart);
                var next = graph.Allocate(plan, neighbor, 26297);
                Assert(next.AllocatedNodes.SequenceEqual([neighbor]), cls.Name); graph.Validate(next);
                Assert(graph.Refund(next, neighbor).AllocatedNodes.Length == 0);
            }
        }));
        await test("GGG: Witch overrides change descriptions without changing allocation IDs", () => Check(() =>
        {
            Assert(real is not null); var witch = real!.Classes.Single(c => c.Index == 1);
            int changed = 0;
            foreach (var pair in witch.Overrides.Where(p => real.Nodes.ContainsKey(p.Key)))
            {
                var info = real.Describe(pair.Key, new() { ClassIndex = 1 });
                Assert(info == real.Variants[pair.Value]); Assert(real.Nodes[pair.Key].Id == pair.Key); changed++;
            }
            Assert(changed > 0);
        }));
        await test("GGG: real attribute route persists the chosen variant", () => Check(() =>
        {
            Assert(real is not null); var graph = new PassiveTreeEngine(real!); var plan = new PassiveTreePlan();
            int target = real!.Nodes.Values.First(n => n.IsAttribute && n.IsSupported && Reachable(n.Id)).Id;
            bool Reachable(int id) { try { graph.FindPath(plan, id); return true; } catch (TreeRuleException) { return false; } }
            var allocated = graph.Allocate(plan, target, 14927);
            Assert(allocated.AttributeSelections.Count > 0 && allocated.AttributeSelections.Values.All(id => id == 14927));
            Assert(real.Describe(target, allocated).Stats.SequenceEqual(["+5 to Dexterity"])); graph.Validate(allocated);
        }));
        await test("GGG: blocked main-tree nodes cannot be allocated", () => Check(() =>
        {
            Assert(real is not null); var graph = new PassiveTreeEngine(real!); var plan = new PassiveTreePlan();
            int blocked = 0;
            foreach (var node in real!.Nodes.Values.Where(n => !n.IsSupported)) { Rule("TreeUnsupported", () => graph.FindPath(plan, node.Id)); blocked++; }
            Assert(blocked > 300);
        }));
        await test("GGG: display markup cleanup retains English wording", () => Check(() =>
        {
            Assert(TreeCatalog.PlainText("+5 to [Attributes|Attribute] and [Strength]") == "+5 to Attribute and Strength");
            Assert(TreeCatalog.PlainText("<underline>{Skill}</underline>") == "{Skill}");
        }));
        await test("Build schema 1 migrates in memory and keeps original bytes until save", async () =>
        {
            var repo = new BuildRepository(Path.Combine(tempRoot, "migration")); Directory.CreateDirectory(repo.RootDirectory);
            var doc = BuildDocument.Create("Legacy — Ёж") with { Notes = "Do not erase", CharacterClass = "Своя подпись" };
            var json = JsonSerializer.SerializeToNode(doc, BuildRepository.JsonOptions)!; json["schemaVersion"] = 1; json.AsObject().Remove("tree");
            var bytes = json.ToJsonString(); var path = repo.PathFor(doc.Id); await File.WriteAllTextAsync(path, bytes);
            var read = await BuildRepository.ReadDocumentAsync(path);
            Assert(read.SchemaVersion == 4 && read.Tree is null && read.Notes == doc.Notes && read.CharacterClass == doc.CharacterClass);
            Assert(await File.ReadAllTextAsync(path) == bytes);
            await repo.SaveAsync(read); Assert(await File.ReadAllTextAsync(path + ".bak") == bytes);
        });
        await test("Build schema 4 round-trips tree, choices, duplicate, export and import", async () =>
        {
            var repo = new BuildRepository(Path.Combine(tempRoot, "tree-roundtrip"));
            var p = engine.Allocate(empty with { PointLimit = 42 }, 3, 14927);
            var saved = await repo.SaveAsync(BuildDocument.Create("Tree") with
            {
                Tree = p,
                Notes = "Notes",
                Reservation = new ResourceReservationPlan { LifeReservedFlat = 10, SpiritReservedPercent = 25 },
                Defence = new DefenceScenarioPlan { SpellRawHit = 250, SpellDamageType = "Chaos", SpellHitChancePercent = 80 }
            });
            var path = repo.PathFor(saved.Id); var loaded = await BuildRepository.ReadDocumentAsync(path);
            Assert(loaded.Tree!.AllocatedNodes.SequenceEqual([2, 3]) && loaded.Tree.AttributeSelections[3] == 14927 && loaded.Tree.PointLimit == 42);
            Assert(loaded.Reservation is not null && loaded.Reservation.LifeReservedFlat == 10 &&
                   loaded.Reservation.SpiritReservedPercent == 25, "reservation plan round-trip");
            Assert(loaded.Defence is not null && loaded.Defence.SpellRawHit == 250 &&
                   loaded.Defence.SpellDamageType == "Chaos" && loaded.Defence.SpellHitChancePercent == 80,
                "defence scenario plan round-trip");
            var copy = await repo.DuplicateAsync(loaded, "Copy"); var imported = await repo.ImportAsNewAsync(path);
            foreach (var doc in new[] { copy, imported }) Assert(doc.Id != saved.Id && doc.Tree!.AttributeSelections[3] == 14927);
            var export = Path.Combine(tempRoot, "tree-export.poebuild"); await BuildRepository.WriteDocumentAsync(export, loaded);
            Assert((await BuildRepository.ReadDocumentAsync(export)).Tree!.DatasetId == "fixture");
        });
        await test("Tree editor tracks dirty state without exposing mutable internal arrays", () => Check(() =>
        {
            var doc = BuildDocument.Create("Editor"); var editor = new BuildEditor(doc);
            var plan = engine.Allocate(empty, 3, 26297); editor.SetTree(plan); Assert(editor.IsDirty);
            plan.AllocatedNodes[0] = 65000; plan.AttributeSelections[3] = 57022;
            Assert(editor.TreeSnapshot!.AllocatedNodes[0] == 2 && editor.TreeSnapshot.AttributeSelections[3] == 26297);
            var exported = editor.ToDocument(); exported.Tree!.AllocatedNodes[0] = 12345;
            Assert(editor.TreeSnapshot.AllocatedNodes[0] == 2); editor.AcceptSaved(editor.ToDocument()); Assert(!editor.IsDirty);
        }));
        await test("Tree build validation rejects nulls, duplicates, invalid limits and unknown fields", async () =>
        {
            var doc = BuildDocument.Create("Invalid tree"); var path = Path.Combine(tempRoot, "invalid-tree.poebuild");
            async Task Reject(JsonNode json)
            {
                await File.WriteAllTextAsync(path, json.ToJsonString());
                try { await BuildRepository.ReadDocumentAsync(path); throw new Exception("Invalid tree accepted"); } catch (BuildFormatException) { }
            }
            foreach (var plan in new[] { empty with { AllocatedNodes = [2, 2] }, empty with { AllocatedNodes = null! }, empty with { AttributeSelections = null! }, empty with { DatasetId = null! }, empty with { PointLimit = -1 } })
                await Reject(JsonSerializer.SerializeToNode(doc with { Tree = plan }, BuildRepository.JsonOptions)!);
            var unknown = JsonSerializer.SerializeToNode(doc with { Tree = empty }, BuildRepository.JsonOptions)!;
            unknown["tree"]!["futureMechanic"] = 1; await Reject(unknown);
            var invalidLegacy = JsonSerializer.SerializeToNode(doc with { Tree = empty }, BuildRepository.JsonOptions)!;
            invalidLegacy["schemaVersion"] = 1; await Reject(invalidLegacy);
        });
        await test("Foreign dataset plans remain preservable but cannot be allocated", async () =>
        {
            var foreign = new PassiveTreePlan { DatasetId = "future-dataset", AllocatedNodes = [65500], AttributeSelections = new() { [65500] = 444 } };
            var path = Path.Combine(tempRoot, "foreign.poebuild"); await BuildRepository.WriteDocumentAsync(path, BuildDocument.Create("Foreign") with { Tree = foreign });
            var read = await BuildRepository.ReadDocumentAsync(path); var editor = new BuildEditor(read); editor.Notes = "metadata still editable";
            Assert(editor.ToDocument().Tree!.AllocatedNodes.SequenceEqual([65500]) && editor.ToDocument().Tree!.AttributeSelections[65500] == 444);
            Rule("TreeDatasetMismatch", () => engine.Allocate(editor.TreeSnapshot!, 2, 26297));
        });
    }
}
