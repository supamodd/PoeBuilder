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
    private static PassiveNode Node(int id, bool attribute = false, bool locked = false, bool mastery = false, bool ascendancy = false, bool anoint = false, int[]? starts = null,
        bool keystone = false, bool jewel = false, double? x = null) =>
        new(id, "fixture_" + id, keystone ? "Resonance" : "Node " + id, "", [], x ?? id * 100, 0, 1, false, keystone, jewel, attribute, mastery, ascendancy, locked, anoint, starts ?? []);
    private static TreeCatalog Fixture()
    {
        // Nodes 20-23 exist for the radius-allocation rules: 20 is a keystone named "Resonance", 21 is an
        // orphan inside ITS radius, 22 is a jewel socket wired to the Warrior start and 23 is an orphan
        // inside THE SOCKET's radius. Orphans have no edges at all, so only a radius jewel can reach them —
        // exactly the situation "From Nothing" and "Intuitive Leap" create on the real tree.
        var nodes = new[]
        {
            Node(1, starts: [6]), Node(2), Node(3, attribute: true), Node(4), Node(5), Node(6, starts: [2]), Node(7, locked: true),
            Node(8), Node(9, mastery: true), Node(10, ascendancy: true), Node(11, anoint: true), Node(12),
            Node(20, keystone: true, x: 50100), Node(21, x: 50000), Node(22, jewel: true, x: 2200), Node(23, x: 2300)
        }.ToDictionary(n => n.Id);
        (int From, int To)[] links = [(1, 2), (2, 3), (3, 4), (2, 5), (4, 5), (1, 6), (6, 8), (1, 7), (7, 8), (1, 9), (9, 8), (1, 10), (10, 8), (1, 11), (11, 8), (2, 22)];
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
        await test("Tree: an alternate class start (unique jewel) opens that class's region", () => Check(() =>
        {
            // Node 8 is unreachable from the Warrior start (node 1) without crossing the Ranger start
            // (node 6), which the base rules treat as a wall. Opening node 6 as an alternate start via
            // a unique jewel roots the region from it, exactly like PoB2's alternateClassStart.
            Assert(engine.FindPath(empty with { AlternateStartNodes = [6] }, 8).SequenceEqual([8]));
            var plan = engine.Allocate(empty with { AlternateStartNodes = [6] }, 8, 26297);
            Assert(plan.AllocatedNodes.SequenceEqual([8]), "alternate-rooted allocation " + string.Join(",", plan.AllocatedNodes));
            engine.Validate(plan); // connected through the alternate root, no TreeDisconnected
            // Refund keeps node 8 (still rooted from the alternate start) instead of pruning it.
            Assert(engine.RefundSet(plan, 1).Length == 0, "alternate root must not cut the cluster");
        }));
        await test("Tree: alternate start nodes are validated and cannot be silently dropped", () => Check(() =>
        {
            Rule("TreeInvalidSaved", () => engine.Validate(empty with { AlternateStartNodes = [65001] }));
            try { (empty with { AlternateStartNodes = [6, 6] }).ValidateStructure(); throw new Exception("duplicate alternates accepted"); }
            catch (BuildFormatException) { }
            Assert(engine.Roots(empty with { AlternateStartNodes = [6, 1] }).SequenceEqual([6, 1]), "engine roots dedup");
        }));
        await test("Tree: a saved plan that fails its own rules is brought back without inventing anything", () => Check(() =>
        {
            // The shape an imported "jewel carrier" build was saved in: an anoint-only node (11) stored as if
            // it were an ordinary passive, and a choice map left over from nodes the plan no longer has (99).
            // Validate refuses it, so every walk on it throws — which is what made a hover tooltip and a click
            // fail on that build.
            var messy = new PassiveTreePlan { DatasetId = "fixture", ClassIndex = 6, AllocatedNodes = [2, 11], AttributeSelections = new() { [99] = 26297 } };
            Rule("TreeInvalidSaved", () => engine.Validate(messy));
            var repaired = engine.Repair(messy, out int granted, out int dropped, out int choices);
            Assert(granted == 1 && dropped == 0 && choices == 0, $"expected 1 granted, 0 dropped, 0 choices; got {granted}/{dropped}/{choices}");
            Assert(repaired.JewelAllocatedNodes.SequenceEqual([11]), "an anoint node is granted, not paid for");
            Assert(repaired.AttributeSelections.Count == 0, "a choice for a node the plan does not have is meaningless");
            Assert(engine.Spent(repaired) == 1, "a granted node costs no point");
            engine.Validate(repaired); // and the repaired plan passes every rule the original failed
            Assert(ReferenceEquals(engine.Repair(repaired, out _, out _, out _), repaired), "an already valid plan is returned untouched");
        }));
        await test("Tree: repair keeps a socket its jewel allocates and defaults a choice the tree cannot offer", () => Check(() =>
        {
            // Node 3 is an attribute node whose stored choice is not one the pinned tree offers any more.
            var choice = new PassiveTreePlan { DatasetId = "fixture", ClassIndex = 6, AllocatedNodes = [2, 3], AttributeSelections = new() { [3] = 999 } };
            var fixedChoice = engine.Repair(choice, out int g1, out int d1, out int c1);
            Assert(g1 == 0 && d1 == 0 && c1 == 1, $"expected 0 granted, 0 dropped, 1 choice; got {g1}/{d1}/{c1}");
            Assert(fixedChoice.AttributeSelections.GetValueOrDefault(3) == PassiveTreeEngine.DefaultAttribute, "the engine's default choice is applied");
            engine.Validate(fixedChoice);
            // Socket 22 holds a jewel but nothing connects it: the jewel in it is what allocates it, so it is
            // granted instead of being given up with the rest of the build (which is what the pinned engine
            // does with the sockets of an imported jewel carrier).
            var orphanSocket = new PassiveTreePlan { DatasetId = "fixture", ClassIndex = 6, AllocatedNodes = [22], Jewels = new() { [22] = Guid.NewGuid() } };
            Rule("TreeDisconnected", () => engine.Validate(orphanSocket));
            var fixedSocket = engine.Repair(orphanSocket, out int g2, out int d2, out int c2);
            Assert(g2 == 1 && d2 == 0 && c2 == 0, $"expected 1 granted, 0 dropped, 0 choices; got {g2}/{d2}/{c2}");
            Assert(fixedSocket.JewelAllocatedNodes.SequenceEqual([22]), "the socket's own jewel grants it");
            Assert(fixedSocket.Jewels.Count == 1, "and the jewel in it stays where it is");
            engine.Validate(fixedSocket);
        }));
        await test("Tree: what cannot be traversed or granted is given up; a foreign plan is left alone", () => Check(() =>
        {
            // Node 7 carries an unsupported constraint: it is neither traversable nor grantable, so the only
            // honest repair is to give it up — and to say so.
            var messy = new PassiveTreePlan { DatasetId = "fixture", ClassIndex = 6, AllocatedNodes = [2, 7] };
            Rule("TreeInvalidSaved", () => engine.Validate(messy));
            var repaired = engine.Repair(messy, out int granted, out int dropped, out int choices);
            Assert(granted == 0 && dropped == 1 && choices == 0, $"expected 0 granted, 1 dropped, 0 choices; got {granted}/{dropped}/{choices}");
            Assert(repaired.AllocatedNodes.SequenceEqual([2]), "the unsupported node is the one given up");
            engine.Validate(repaired);
            // A plan from another data set cannot be judged, let alone repaired: it comes back untouched and
            // reports nothing, so the caller keeps reporting the invalid state instead.
            var foreign = new PassiveTreePlan { DatasetId = "other-tree", ClassIndex = 6, AllocatedNodes = [2] };
            var untouched = engine.Repair(foreign, out int g2, out int d2, out int c2);
            Assert(ReferenceEquals(untouched, foreign) && g2 == 0 && d2 == 0 && c2 == 0, "a foreign plan must not be rewritten");
            Assert(!engine.IsValid(foreign), "and it stays invalid");
        }));
        await test("Tree: a radius jewel reaches nodes with no edge (From Nothing, Intuitive Leap)", () => Check(() =>
        {
            // Node 23 is an orphan inside the SOCKET's radius and node 21 is an orphan inside the radius of
            // the keystone "Resonance" (node 20). Both are legal only through a radius jewel: Intuitive
            // Leap's "Passives in radius can be allocated without being connected to your tree" reads the
            // socket's radius, From Nothing's "… in radius of Resonance …" reads the KEYSTONE's radius and
            // never requires that keystone to be taken — PoB2's own PoB2 PassiveSpec.lua:1361-1392 behaviour,
            // and the reason a Twister build reaches its cluster without allocating Resonance.
            PassiveTreePlan socketed = empty, taken = empty, reached = empty, refunded = empty;
            void Ok(string what, Action action) { try { action(); } catch (TreeRuleException e) { throw new Exception(what + ": unexpected " + e.Code); } }
            Ok("allocate the socket node", () => socketed = engine.Allocate(empty, 22, 26297));
            var jewelId = Guid.NewGuid();
            var withJewel = socketed with { Jewels = new Dictionary<int, Guid> { [22] = jewelId } };
            Rule("TreeNoPath", () => engine.FindPath(withJewel, 23));
            var leap = withJewel with { RadiusJewels = new Dictionary<int, RadiusAllocationRule> { [22] = new(1, "") } };
            Ok("socket radius path", () => { if (!engine.FindPath(leap, 23).SequenceEqual([23])) throw new Exception("socket radius path: wrong path"); });
            Ok("allocate through the socket radius", () => taken = engine.Allocate(leap, 23, 26297));
            Ok("validate the socket-radius plan", () => engine.Validate(taken));
            Assert(engine.Spent(taken) - engine.Spent(socketed) == 1, "a radius node costs its own point");
            // From Nothing: the radius belongs to the named keystone, which the plan never allocates.
            var fromNothing = withJewel with { RadiusJewels = new Dictionary<int, RadiusAllocationRule> { [22] = new(1, "Resonance") } };
            Ok("keystone radius path", () => { if (!engine.FindPath(fromNothing, 21).SequenceEqual([21])) throw new Exception("keystone radius path: wrong path"); });
            Ok("allocate through the keystone radius", () => reached = engine.Allocate(fromNothing, 21, 26297));
            Ok("validate the keystone-radius plan", () => engine.Validate(reached));
            Assert(!reached.AllocatedNodes.Contains(20), "the named keystone itself is not allocated");
            // A radius that simply does not cover the node reaches nothing, and a rule naming a keystone the
            // tree does not have is a malformed saved state rather than a silent no-op.
            Rule("TreeNoPath", () => engine.FindPath(fromNothing, 23));
            Rule("TreeInvalidSaved", () => engine.Validate(
                fromNothing with { RadiusJewels = new Dictionary<int, RadiusAllocationRule> { [22] = new(1, "No Such Keystone") } }));
            // A rule without an allocated socket carrying a jewel is a malformed plan, not a silent no-op.
            Rule("TreeInvalidSaved", () => engine.Validate(empty with { RadiusJewels = new Dictionary<int, RadiusAllocationRule> { [22] = new(1, "") } }));
            Rule("TreeInvalidSaved", () => engine.Validate(socketed with { RadiusJewels = new Dictionary<int, RadiusAllocationRule> { [22] = new(1, "") } }));
            // Losing the jewel takes back exactly what it reached — the game refunds those points the same way.
            var closed = engine.KeepReachable(reached with { RadiusJewels = new Dictionary<int, RadiusAllocationRule>() });
            Assert(!closed.AllocatedNodes.Contains(21), "a node nothing reaches any more is dropped");
            Ok("validate the closed plan", () => engine.Validate(closed));
            Ok("refund the socket", () => refunded = engine.Refund(reached, 22));
            Ok("validate the refunded plan", () => engine.Validate(refunded));
            Assert(!refunded.AllocatedNodes.Contains(21) && refunded.RadiusJewels.Count == 0,
                "refunding the socket closes its radius cluster and drops its rule");
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
        await test("Tree: a click toggles allocation — allocate, refund, never the class start or a jewel-granted node", () => Check(() =>
        {
            // PoB2's tree (Classes/PassiveTreeView.lua:411-430) allocates on a left click and refunds on the
            // next one, so the editor needs no separate "select the path" step; the decision table lives in
            // the core (TreeClickModel), where it is covered without a UI.
            var plan = new PassiveTreePlan { DatasetId = fixture.DatasetId, ClassIndex = 6, AllocatedNodes = [2, 3] };
            Assert(TreeClickModel.Resolve(plan, fixture, 3) == TreeClickAction.Refund, "an allocated node is refundable");
            Assert(TreeClickModel.Resolve(plan, fixture, 4) == TreeClickAction.Allocate, "a free node is allocatable");
            Assert(TreeClickModel.Resolve(plan, null, 4) == TreeClickAction.Ignore, "without a catalog nothing happens");
            Assert(TreeClickModel.Resolve(plan, fixture, 999) == TreeClickAction.Ignore, "an unknown id is ignored");
            Assert(TreeClickModel.Resolve(new PassiveTreePlan { DatasetId = "fixture" }, fixture, 1) == TreeClickAction.Ignore,
                "the class start costs no points and is never a target");
            var granted = plan with { JewelAllocatedNodes = [3] };
            Assert(TreeClickModel.Resolve(granted, fixture, 3) == TreeClickAction.Ignore,
                "a node a jewel pays for is owned by the jewel, not by the plan");
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
        await test("Tree: verbatim allocation never invents intermediates", () => Check(() =>
        {
            // Fixture: start 1 — 2 — 3 — 4; node 2 is adjacent to the start, node 4 needs 3 first.
            Assert(engine.AllocateVerbatim(empty, 2, 26297).AllocatedNodes.SequenceEqual([2]));
            Rule("TreeNoPath", () => engine.AllocateVerbatim(empty, 4, 26297));
            var two = engine.AllocateVerbatim(empty, 2, 26297);
            Rule("TreeNoPath", () => engine.AllocateVerbatim(two, 4, 26297)); // 4 is two steps away
            var three = engine.Allocate(two, 3, 26297);
            Assert(engine.AllocateVerbatim(three, 4, 26297).AllocatedNodes.SequenceEqual([2, 3, 4]));
            Assert(engine.AllocateVerbatim(empty, 1, 26297).AllocatedNodes.Length == 0, "class start is implicit");
        }));

        await test("Foreign dataset plans remain preservable but cannot be allocated", async () =>
        {
            var foreign = new PassiveTreePlan { DatasetId = "future-dataset", AllocatedNodes = [65500], AttributeSelections = new() { [65500] = 444 } };
            var path = Path.Combine(tempRoot, "foreign.poebuild"); await BuildRepository.WriteDocumentAsync(path, BuildDocument.Create("Foreign") with { Tree = foreign });
            var read = await BuildRepository.ReadDocumentAsync(path); var editor = new BuildEditor(read); editor.Notes = "metadata still editable";
            Assert(editor.ToDocument().Tree!.AllocatedNodes.SequenceEqual([65500]) && editor.ToDocument().Tree!.AttributeSelections[65500] == 444);
            Rule("TreeDatasetMismatch", () => engine.Allocate(editor.TreeSnapshot!, 2, 26297));
        });

        await test("Tree art: PoB2's own frame sprites are mapped per node type and state", () => Task.Run(() =>
        {
            // PoB2 picks a frame sprite from its tree data (TreeData/0_5/tree.json → nodeOverlay) and draws
            // it under the skill icon, so the sprite is what tells an allocated node from a reachable one.
            // The app ships those atlas slices as PNGs, converted once from PoB2's BC7 DDS arrays (the
            // converter is documented in docs/VALIDATION.md), which means the mapping and the shipped art
            // have to agree — a wrong slice or a missing file would otherwise only show up as odd pixels.
            Assert(TreeFrameArt.Sprite(false, false, false, false, false, NodeFrameState.Unallocated) == TreeFrameArt.Normal);
            Assert(TreeFrameArt.Sprite(false, false, false, false, false, NodeFrameState.CanAllocate) == TreeFrameArt.NormalCanAllocate);
            Assert(TreeFrameArt.Sprite(false, false, false, false, false, NodeFrameState.Allocated) == TreeFrameArt.NormalAllocated);
            Assert(TreeFrameArt.Sprite(false, false, true, false, false, NodeFrameState.Allocated) == TreeFrameArt.NotableAllocated);
            Assert(TreeFrameArt.Sprite(false, true, false, false, false, NodeFrameState.CanAllocate) == TreeFrameArt.KeystoneCanAllocate);
            Assert(TreeFrameArt.Sprite(false, false, false, true, false, NodeFrameState.Unallocated) == TreeFrameArt.JewelUnallocated);
            Assert(TreeFrameArt.Sprite(true, false, false, false, false, NodeFrameState.Allocated) == TreeFrameArt.AscendancyAllocated);
            Assert(TreeFrameArt.Sprite(false, false, false, false, true, NodeFrameState.Allocated) is null,
                "the class start node is drawn as the class crest, not as a node frame");

            string folder = Path.Combine(AppContext.BaseDirectory, "Data", "Tree", "Art");
            foreach (var (sprite, expected) in new (string, (int, int))[]
            {
                (TreeFrameArt.Normal, (104, 104)),
                (TreeFrameArt.NormalAllocated, (104, 104)),
                (TreeFrameArt.NotableAllocated, (152, 156)),
                (TreeFrameArt.JewelCanAllocate, (152, 156)),
                (TreeFrameArt.KeystoneUnallocated, (220, 224)),
                (TreeFrameArt.AscendancyUnallocated, (208, 208)),
            })
            {
                string file = Path.Combine(folder, sprite + ".png");
                Assert(File.Exists(file), "missing frame art: " + file);
                var bytes = File.ReadAllBytes(file);
                int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                Assert((width, height) == expected, sprite + " shipped as " + width + "x" + height + " (not matched)");
                Assert((width, height) == TreeFrameArt.Size(sprite), sprite + " must keep its atlas slice size");
            }
        }));

        await test("Tree art: a node's icon sits inside its frame, never over it", () => Task.Run(() =>
        {
            // PoB2 draws the icon first and the frame over it (PassiveTreeView.lua:1064, :1126) at its own
            // icon share of the frame (PassiveTree.lua:777-835: 37/54 for a normal node, 54/80 for a notable,
            // 82/120 for a keystone, 76/76 for a socket). Drawing the full-width icon last is what put a square
            // patch over every node's round frame, so the numbers are pinned here.
            Assert(Math.Abs(TreeFrameArt.IconShare(TreeFrameArt.Normal) - 37.0 / 54.0) < 1e-9, "normal node icon share");
            Assert(Math.Abs(TreeFrameArt.IconShare(TreeFrameArt.NotableAllocated) - 54.0 / 80.0) < 1e-9, "notable icon share");
            Assert(Math.Abs(TreeFrameArt.IconShare(TreeFrameArt.KeystoneCanAllocate) - 82.0 / 120.0) < 1e-9, "keystone icon share");
            Assert(TreeFrameArt.IconShare(TreeFrameArt.JewelUnallocated) == 1, "a socket's icon is its own frame");
            Assert(TreeFrameArt.IconShare(TreeFrameArt.AscendancyAllocated) > 0, "an ascendancy node has a share too");
            Assert(TreeFrameArt.IconShare(null) > 0, "a data set without frame art still gets an icon size");
            foreach (string sprite in new[] { TreeFrameArt.Normal, TreeFrameArt.NotableAllocated, TreeFrameArt.KeystoneAllocated, TreeFrameArt.AscendancyAllocated })
                Assert(TreeFrameArt.IconShare(sprite) is > 0 and <= 1, sprite + " must keep the icon inside the frame");
        }));

        await test("Tree art: PoB2's connector sprites are placed on their orbit by an affine transform", () => Task.Run(() =>
        {
            // PoB2 builds a connector's sprite name as connectionArt .. (Orbit<N> | LineConnector) .. state
            // (Classes/PassiveTree.lua:723-731), and tree.json → assets names the file. Orbit N uses sprite
            // index 10 - N, so the catalogue's own numbering is what the mapping has to reproduce.
            Assert(TreeConnectionArt.OrbitFile(9, ConnectionState.Active) == "Character_orbit_intermediateactive1.png");
            Assert(TreeConnectionArt.OrbitFile(1, ConnectionState.Normal) == "Character_orbit_normal9.png");
            Assert(TreeConnectionArt.OrbitFile(7, ConnectionState.Intermediate) == "Character_orbit_intermediate7.png",
                "orbit 7 is the irregular one: its sprite carries the same number");
            Assert(TreeConnectionArt.OrbitFile(3, ConnectionState.Normal) == "Character_orbit_normal6.png",
                "orbit 3 uses sprite 6, because that is the art whose radius matches orbit 3");
            Assert(TreeConnectionArt.OrbitFile(0, ConnectionState.Normal) is null, "orbit 0 has no arc art");
            Assert(TreeConnectionArt.LineFile(ConnectionState.Intermediate) == "Character_orbit_intermediate0.png");
            Assert(TreeConnectionArt.OrbitForRadius(81.7) == 1 && TreeConnectionArt.OrbitForRadius(250.6) == 7
                && TreeConnectionArt.OrbitForRadius(1318.4) == 9 && TreeConnectionArt.OrbitForRadius(657.1) == 5,
                "the orbit is found by matching the node distance to PoB2's radius table");

            // One sprite covers a quarter circle, so a wider span is drawn as several pieces.
            Assert(TreeConnectionArt.SplitArc(0, 0.5).Count == 1, "a narrow span needs one sprite");
            Assert(TreeConnectionArt.SplitArc(0, Math.PI / 2).Count == 1, "exactly 90 degrees still fits one sprite");
            var wide = TreeConnectionArt.SplitArc(1, 3 * Math.PI / 2);
            Assert(wide.Count == 3 && wide.All(p => Math.Abs(p.Sweep) <= Math.PI / 2 + 1e-9), "a 270 degree span is split three ways");
            Assert(Math.Abs(wide.Sum(p => p.Sweep) - 3 * Math.PI / 2) < 1e-9, "the pieces must cover the whole span");
            Assert(Math.Abs(wide[1].Start - (1 + Math.PI / 2)) < 1e-9, "the pieces must be contiguous");

            // The measured art: the arc's centre is the sprite's bottom-right pixel, the arc radius is the
            // catalogue's orbit radius, and the arc opens towards the sprite's top-left corner (-135 degrees).
            // Placing it must therefore put the art's arc points on the orbit circle at the requested angles.
            const int artWidth = 91, artHeight = 90, orbitRadius = 82;
            double artRadius = TreeConnectionArt.ArcRadius("Character_orbit_normal9.png");
            var placement = TreeConnectionArt.OrbitPlacement("Character_orbit_normal9.png", orbitRadius, 0,
                1000, 500, artWidth, artHeight);
            foreach (int offsetDegrees in new[] { -45, 0, 45 })
            {
                double artAngle = (-135 + offsetDegrees) * Math.PI / 180;
                var (x, y) = placement.Apply(artWidth - 1 + artRadius * Math.Cos(artAngle),
                    artHeight - 1 + artRadius * Math.Sin(artAngle));
                double expectedAngle = offsetDegrees * Math.PI / 180;
                Assert(Math.Abs(x - (1000 + orbitRadius * Math.Cos(expectedAngle))) < 0.01 &&
                    Math.Abs(y - (500 + orbitRadius * Math.Sin(expectedAngle))) < 0.01,
                    $"arc point {offsetDegrees} degrees lands at {x:0.##},{y:0.##} instead of on the orbit");
            }

            // The line sprite is a strip along the segment: a rotation about the segment's first endpoint
            // must keep that endpoint and carry the strip's edge across the connection.
            var line = TreeConnectionArt.LinePlacement(0, 0, Math.PI / 2);
            var (cx, cy) = line.Apply(0, -16.5);
            Assert(Math.Abs(cx - 16.5) < 0.01 && Math.Abs(cy) < 0.01, "the strip's edge must land beside the start point");

            // Every sprite the mapping can ask for must ship, at the size it was measured at — the width
            // and height pairs below come from the files themselves, so a wrong or corrupt one shows up
            // here instead of as a hole in the tree.
            var expectedSizes = new Dictionary<int, (int, int)>
            {
                [0] = (1435, 29), [1] = (1333, 1333), [2] = (1090, 1091), [3] = (853, 853), [4] = (671, 671),
                [5] = (501, 502), [6] = (346, 346), [7] = (263, 263), [8] = (176, 176), [9] = (91, 90),
            };
            string folder = Path.Combine(AppContext.BaseDirectory, "Data", "Tree", "Art", "orbit");
            int checkedFiles = 0;
            foreach (var state in new[] { ConnectionState.Normal, ConnectionState.Intermediate, ConnectionState.Active })
                for (int orbit = 1; orbit <= 9; orbit++)
                {
                    string sprite = TreeConnectionArt.OrbitFile(orbit, state)!;
                    string path = Path.Combine(folder, sprite);
                    Assert(File.Exists(path), "missing connector art: " + path);
                    var bytes = File.ReadAllBytes(path);
                    int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                    int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                    Assert((width, height) == expectedSizes[sprite[^5] - '0'], sprite + " shipped as " + width + "x" + height);
                    // The art radius has to match the orbit it is drawn on, or the arc would miss the nodes.
                    double ratio = TreeConnectionArt.OrbitRadii[orbit] / TreeConnectionArt.ArcRadius(sprite);
                    Assert(ratio is > 0.98 and < 1.03, sprite + " radius ratio " + ratio);
                    checkedFiles++;
                }
            Assert(checkedFiles == 27 && File.Exists(Path.Combine(folder, TreeConnectionArt.LineFile(ConnectionState.Normal))),
                "the line connector sprite must ship as well");
        }));

        await test("Tree art: a mastery is drawn as its effect pattern, and every pattern ships", () => Task.Run(() =>
        {
            // PoB2 draws a node's effect art first: a mastery *is* that art (its nodes are the export's
            // "OnlyImage" records and take the 380 half-size of PassiveTree.lua:809-810, with no frame and
            // no icon), while a notable or keystone keeps it under the frame and ghosts it at 15 % until
            // it is allocated (Classes/PassiveTreeView.lua:1026-1040, GetNodeTargetSize:799/:813). Those
            // patterns are the faint star burst a cluster shows and the golden flare on an allocated
            // node. The export names them on the 368 masteries; the pinned reference tree names them on
            // 632 nodes, and notable-effects.json supplies the other 264 (notables and keystones).
            Assert(TreeEffectArt.Radius(true, true) == TreeEffectArt.MasteryRadius, "a mastery is its pattern");
            Assert(TreeEffectArt.Radius(false, true) == TreeEffectArt.NotableRadius, "any node with effect art draws the 380 backdrop of :799/:813");
            Assert(TreeEffectArt.Radius(false, false) == 0, "a node without effect art draws none");
            Assert(TreeEffectArt.MasteryRadius * 2 == 760, "PoB2's sizes are half-sizes: the art spans twice the number");
            Assert(TreeEffectArt.IdleOpacity == 0.15, "an unallocated effect keeps PoB2's 15 % ghost");
            Assert(TreeEffectArt.Sprite("Art/2DArt/UIImages/InGame/PassiveMastery/MasteryBackgroundGraphic/MasteryFirePattern.png") == "MasteryFirePattern");
            Assert(TreeEffectArt.Sprite("MasteryAttackPattern") == "MasteryAttackPattern" && TreeEffectArt.Sprite("") is null);

            var catalog = real ?? throw new Exception("the pinned tree must have loaded");
            string folder = Path.Combine(AppContext.BaseDirectory, "Data", "Tree", "Art", "effect");
            var sprites = new HashSet<string>();
            int affected = 0, masteries = 0, stray = 0;
            foreach (var node in catalog.Nodes.Values)
            {
                string? sprite = TreeEffectArt.Sprite(node.EffectArt);
                if (sprite is null) continue;
                affected++;
                if (node.IsMastery) masteries++;
                // An ordinary node may also carry effect art (Mark Effect, Herald Damage, Evasion): it is
                // not an error, it is the same backdrop under its frame — count it, never gate it.
                else if (!node.IsNotable && !node.IsKeystone) stray++;
                sprites.Add(sprite);
                Assert(File.Exists(Path.Combine(folder, sprite + ".png")), "missing effect art: " + sprite);
            }
            Assert(affected == 632, "nodes carrying effect art: " + affected);
            Assert(masteries == 368, "masteries carrying effect art: " + masteries);
            Assert(stray == 3, "ordinary nodes carrying effect art (not mastery/notable/keystone): " + stray);
            Assert(sprites.Count == 60, "distinct shipped patterns: " + sprites.Count);
            Assert(Directory.GetFiles(folder, "*.png").Length == 60, "no unused pattern is shipped");
        }));
        await test("Tree art: class and ascendancy backdrops are shipped and placed like PoB2", () => Task.Run(() =>
        {
            // PoB2 draws each class's 1500x1500 art at the tree centre, every ascendancy's art around the
            // ring, and then BGTree plus a BGTreeActive glow rotated towards the class start node
            // (Classes/PassiveTreeView.lua:588-640). The table mirrors tree.json's own classes[] entries.
            Assert(TreeClassArtTable.Class("Mercenary") is { Sprite: "ClassesMercenary", X: 0, Y: 0 });
            Assert(TreeClassArtTable.Class("Gemling Legionnaire") is null, "an ascendancy is not a base class");
            var gemling = TreeClassArtTable.Ascendancy("Gemling Legionnaire");
            Assert(gemling is { Sprite: "ClassesGemling Legionnaire" } && Math.Abs(gemling.X + 3210.11) < 1 &&
                Math.Abs(gemling.Y - 15201.52) < 1, "the build's own ascendancy keeps its tree position");
            Assert(TreeClassArtTable.Ring.Count == 23 &&
                TreeClassArtTable.Ring.Select(a => a.Name).Distinct().Count() == 23,
                "every ascendancy backdrop is listed exactly once");
            // The glow turns towards the start node: a start node straight below the centre is 180°.
            Assert(Math.Abs(TreeClassArtTable.GlowRotationDegrees(0, 1000, 0, 0) - 180) < 1e-6,
                "a start node below the centre turns the glow downwards");
            Assert(Math.Abs(TreeClassArtTable.GlowRotationDegrees(1000, 0, 0, 0) - 90) < 1e-6,
                "a start node to the right turns it to the right");

            // Every sprite the table can ask for must ship, at the size it was reduced to (the sizes come
            // from the conversion step — build/extract-tree-art.ps1 — so a stale or wrong file shows up here).
            // Class and ascendancy backdrops are the full 1500x1500 BC7 slice; the ring art is the 4000
            // slice halved to 2000, which is exactly the size PoB2 draws it at.
            var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [TreeClassArtTable.RingSprite] = 2000,
                [TreeClassArtTable.GlowSprite] = 2000,
                ["ClassesMercenary"] = 1500,
                ["ClassesGemling Legionnaire"] = 1500,
                ["ClassesDeadeye"] = 1500,
            };
            string folder = Path.Combine(AppContext.BaseDirectory, "Data", "Tree", "Art", "class");
            foreach (var sprite in expected.Keys)
            {
                string path = Path.Combine(folder, sprite + ".png");
                Assert(File.Exists(path), "missing class art: " + path);
                var bytes = File.ReadAllBytes(path);
                int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
                int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
                Assert(width == expected[sprite] && height == expected[sprite],
                    sprite + " shipped as " + width + "x" + height);
            }
            Assert(TreeClassArtTable.Ring.All(a => File.Exists(Path.Combine(folder, a.Sprite + ".png"))),
                "every ascendancy circle must be shipped");
        }));
    }
}
