using System.Text.Json;
using System.Text.Json.Nodes;
using PoeBuilder.App.Services;
using PoeBuilder.App.ViewModels;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Storage;
using PoeBuilder.Core.Tree;

internal static class AscendancyTests
{
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static void Rule(string code, Action action)
    { try { action(); } catch (TreeRuleException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
    public static async Task Run(Func<string, Func<Task>, Task> test, string folder)
    {
        var data = Path.Combine(AppContext.BaseDirectory, "Data", "Tree"); var catalog = TreeCatalog.LoadPinned(Path.Combine(data, "data.json"));
        Task Check(Action a) { a(); return Task.CompletedTask; }
        await test("Ascendancies: 23 named definitions belong to their correct class", () => Check(() =>
        {
            Assert(catalog.Ascendancies.Count == 23, "actual " + catalog.Ascendancies.Count);
            foreach (var asc in catalog.Ascendancies)
            {
                Assert(catalog.Classes.Any(c => c.Index == asc.ClassIndex));
                Assert(asc.Graph.Classes.Single().Index == asc.ClassIndex && asc.Graph.Nodes.Values.Count(n => n.IsStart) == 1);
                Assert(asc.Graph.Nodes.Values.All(n => n.AscendancyId == asc.SourceGraphId));
                Assert(File.Exists(Path.Combine(data, "Portraits", asc.Id + ".jpg")), asc.Id);
            }
            foreach (var cls in catalog.Classes) Assert(File.Exists(Path.Combine(data, "Portraits", cls.Name + ".jpg")));
        }));
        await test("Ascendancies: isolated graphs never include main bridges or other classes", () => Check(() =>
        {
            foreach (var asc in catalog.Ascendancies)
            {
                Assert(!asc.Graph.Nodes.Keys.Intersect(catalog.Nodes.Keys).Any());
                Assert(asc.Graph.Edges.All(e => asc.Graph.Nodes.ContainsKey(e.From) && asc.Graph.Nodes.ContainsKey(e.To)));
                Assert(asc.Graph.Neighbors.All(p => p.Value.All(asc.Graph.Nodes.ContainsKey)));
            }
        }));
        await test("Ascendancies: every named graph has a working route and refund", () => Check(() =>
        {
            foreach (var asc in catalog.Ascendancies)
            {
                var engine = new PassiveTreeEngine(asc.Graph); var plan = asc.ToGraphPlan(new() { Id = asc.Id, PointLimit = 0 });
                engine.Validate(plan); int worked = 0;
                foreach (var n in asc.Graph.Nodes.Values.Where(n => n.IsSupported && !n.IsStart))
                {
                    try
                    {
                        var next = engine.Allocate(plan, n.Id, 26297); engine.Validate(next);
                        Assert(next.AllocatedNodes.Contains(n.Id)); var removed = engine.Refund(next, n.Id); engine.Validate(removed);
                        Assert(!removed.AllocatedNodes.Contains(n.Id)); worked++;
                    }
                    catch (TreeRuleException e) when (e.Code == "TreeNoPath") { }
                }
                Assert(worked > 0, asc.Id + " has no supported routes");
            }
        }));
        await test("Ascendancies: manual pools are separate and free nodes cost zero", () => Check(() =>
        {
            var asc = catalog.Ascendancies.Single(a => a.Id == "Witch2"); var engine = new PassiveTreeEngine(asc.Graph);
            var graphPlan = asc.ToGraphPlan(new() { Id = asc.Id, PointLimit = 1 });
            var free = asc.Graph.Nodes.Values.Single(n => n.Name == "Sanguimancy"); Assert(free.PointCost == 0 && free.IsSupported);
            var allocated = engine.Allocate(graphPlan, free.Id, 26297); Assert(engine.Spent(allocated) == 0 && allocated.AllocatedNodes.Contains(free.Id));
            var main = AscendancyRules.Select(catalog, new() { ClassIndex = 1, PointLimit = 1 }, asc.Id);
            main = AscendancyRules.Update(catalog, main, allocated); new PassiveTreeEngine(catalog).Validate(main);
            Assert(main.PointLimit == 1 && main.AllocatedNodes.Length == 0 && main.Ascendancy!.PointLimit == 1);
        }));
        await test("Ascendancies: weighted route chooses least points, including zero-cost vertices", () => Check(() =>
        {
            PassiveNode N(int id, int cost = 1) => new(id, id.ToString(), id.ToString(), "", [], id, 0, 1, false, false, false, false, false, false, false, false, id == 1 ? [6] : [], cost);
            var nodes = new[] { N(1), N(2), N(3), N(4, 0), N(5, 0), N(6) }.ToDictionary(n => n.Id);
            var graph = new TreeCatalog("cost-test", nodes, [new(6, "Fixture", 1, new Dictionary<int, int>())],
                new[] { (1, 2), (2, 3), (3, 6), (1, 4), (4, 5), (5, 6) }.Select(e => new TreeEdge(e.Item1, e.Item2, null, null)).ToArray(), catalog.Variants);
            var engine = new PassiveTreeEngine(graph); var plan = new PassiveTreePlan { DatasetId = "cost-test", PointLimit = 1 };
            Assert(engine.FindPath(plan, 6).SequenceEqual([4, 5, 6])); Assert(engine.Spent(engine.Allocate(plan, 6, 26297)) == 1);
        }));
        await test("Ascendancies: wrong class, dataset and foreign node plans are rejected", () => Check(() =>
        {
            var main = new PassiveTreePlan { ClassIndex = 6 };
            Rule("TreeInvalidAscendancy", () => AscendancyRules.Select(catalog, main, "Witch2"));
            Rule("TreeInvalidAscendancy", () => new PassiveTreeEngine(catalog).Validate(main with { Ascendancy = new() { Id = "unknown" } }));
            var asc = catalog.Ascendancies.Single(a => a.Id == "Warrior1");
            var plan = AscendancyRules.Select(catalog, main, asc.Id);
            Rule("TreeDatasetMismatch", () => AscendancyRules.Update(catalog, plan, new()));
            Rule("TreeInvalidSaved", () => AscendancyRules.Validate(catalog, plan with { Ascendancy = new() { Id = asc.Id, AllocatedNodes = [47175] } }));
        }));
        await test("Ascendancies: Abyssal Lich reuses Lich graph with its own descriptions", () => Check(() =>
        {
            var variant = catalog.Ascendancies.Single(a => a.Id == "Witch3b"); var standard = catalog.Ascendancies.Single(a => a.Id == "Witch3");
            Assert(variant.SourceGraphId == "Witch3" && variant.Graph.Nodes.Keys.Order().SequenceEqual(standard.Graph.Nodes.Keys.Order()));
            var plan = variant.ToGraphPlan(new() { Id = variant.Id });
            var map = variant.Graph.Classes.Single().Overrides; Assert(map.Count == 13);
            foreach (var entry in map) Assert(variant.Graph.Describe(entry.Key, plan) == catalog.Variants[entry.Value]);
        }));
        await test("Ascendancies: change and undo preserve main allocations and deep-copy child nodes", () => Check(() =>
        {
            var engine = new PassiveTreeEngine(catalog); var main = new PassiveTreePlan { ClassIndex = 6 };
            int neighbor = catalog.Neighbors[47175].First(id => engine.CanTraverse(id, main)); main = engine.Allocate(main, neighbor, 26297);
            var chosen = AscendancyRules.Select(catalog, main, "Warrior1"); var asc = AscendancyRules.Definition(catalog, chosen);
            var graph = asc.ToGraphPlan(chosen.Ascendancy!); var ag = new PassiveTreeEngine(asc.Graph);
            int target = asc.Graph.Neighbors[ag.Start(graph)].First(id => ag.CanTraverse(id, graph));
            chosen = AscendancyRules.Update(catalog, chosen, ag.Allocate(graph, target, 26297));
            var history = new TreeHistory(); history.Record(chosen);
            var changed = AscendancyRules.Select(catalog, chosen, "Warrior2"); Assert(changed.AllocatedNodes.SequenceEqual(main.AllocatedNodes) && changed.Ascendancy!.AllocatedNodes.Length == 0);
            var old = history.Undo(changed); Assert(old.Ascendancy!.Id == "Warrior1" && old.Ascendancy.AllocatedNodes.Contains(target));
            chosen.Ascendancy!.AllocatedNodes[0] = 65535; Assert(old.Ascendancy.AllocatedNodes[0] != 65535);
            Assert(AscendancyRules.Select(catalog, old, null).Ascendancy is null);
        }));
        await test("Schema 2 migrates to 4 without losing tree or touching original bytes", async () =>
        {
            var repo = new BuildRepository(Path.Combine(folder, "schema2")); Directory.CreateDirectory(repo.RootDirectory);
            var plan = new PassiveTreePlan { ClassIndex = 6, AllocatedNodes = [16732] };
            var doc = BuildDocument.Create("Version 0.2") with { Tree = plan }; var json = JsonSerializer.SerializeToNode(doc, BuildRepository.JsonOptions)!;
            json["schemaVersion"] = 2; json["tree"]!.AsObject().Remove("ascendancy"); string bytes = json.ToJsonString(); string path = repo.PathFor(doc.Id);
            await File.WriteAllTextAsync(path, bytes); var read = await BuildRepository.ReadDocumentAsync(path);
            Assert(read.SchemaVersion == 4 && read.Tree!.AllocatedNodes.SequenceEqual([16732]) && read.Tree.Ascendancy is null && await File.ReadAllTextAsync(path) == bytes);
            await repo.SaveAsync(read); Assert(await File.ReadAllTextAsync(path + ".bak") == bytes);
        });
        await test("Schema 4 ascendancy roundtrip, duplicate, import, export and dirty tracking", async () =>
        {
            var plan = AscendancyRules.Select(catalog, new() { ClassIndex = 6 }, "Warrior1"); var asc = AscendancyRules.Definition(catalog, plan);
            var engine = new PassiveTreeEngine(asc.Graph); var graph = asc.ToGraphPlan(plan.Ascendancy!);
            int target = asc.Graph.Neighbors[engine.Start(graph)].First(id => engine.CanTraverse(id, graph));
            plan = AscendancyRules.Update(catalog, plan, engine.Allocate(graph, target, 26297));
            var editor = new BuildEditor(BuildDocument.Create("Asc test")); editor.SetTree(plan); Assert(editor.IsDirty);
            var repo = new BuildRepository(Path.Combine(folder, "asc-io")); var doc = await repo.SaveAsync(editor.ToDocument());
            var read = await BuildRepository.ReadDocumentAsync(repo.PathFor(doc.Id));
            Assert(read.Tree!.Ascendancy!.AllocatedNodes.Contains(target));
            var imported = await repo.ImportAsNewAsync(repo.PathFor(doc.Id)); var copy = await repo.DuplicateAsync(read, "Copy");
            Assert(copy.Id != read.Id && imported.Id != read.Id && imported.Tree!.Ascendancy!.Id == "Warrior1");
            var export = Path.Combine(folder, "asc-export.poebuild"); await BuildRepository.WriteDocumentAsync(export, copy);
            Assert((await BuildRepository.ReadDocumentAsync(export)).Tree!.Ascendancy!.AllocatedNodes.Contains(target));
        });
        await test("Ascendancies: malformed child and schema-2 extension are not silently discarded", async () =>
        {
            var doc = BuildDocument.Create("Invalid") with { Tree = new() { Ascendancy = new() { Id = "Warrior1" } } }; var path = Path.Combine(folder, "bad-asc.poebuild");
            foreach (string mode in new[] { "schema", "null", "duplicate", "unknown" })
            {
                var json = JsonSerializer.SerializeToNode(doc, BuildRepository.JsonOptions)!;
                if (mode == "schema") json["schemaVersion"] = 2;
                if (mode == "null") json["tree"]!["ascendancy"]!["allocatedNodes"] = null;
                if (mode == "duplicate") json["tree"]!["ascendancy"]!["allocatedNodes"] = new JsonArray(1, 1);
                if (mode == "unknown") json["tree"]!["ascendancy"]!["future"] = true;
                await File.WriteAllTextAsync(path, json.ToJsonString());
                try { await BuildRepository.ReadDocumentAsync(path); throw new Exception("Accepted " + mode); } catch (BuildFormatException) { }
            }
        });
        await test("UI regression: named class and attribute fallbacks never expose record internals", () => Check(() =>
        {
            foreach (var cls in catalog.Classes) Assert(cls.ToString() == cls.Name && !cls.ToString().Contains("Index"));
            foreach (var a in catalog.AttributeChoices) Assert(a.ToString() == a.Name && !a.ToString().Contains("PassiveVariant"));
            foreach (var a in catalog.Ascendancies) Assert(a.ToString() == a.Name);
        }));
        await test("UI regression: 16x9 viewport follows the window and respects narrow widths", () => Check(() =>
        {
            double normal = ViewportSizing.Width(850, 940), maximized = ViewportSizing.Width(1300, 1080);
            Assert(maximized > normal && normal == 836 && Math.Abs(maximized - 700 * 16.0 / 9.0) < 1e-9);
            Assert(Math.Abs(ViewportSizing.Height(normal) / normal - 9.0 / 16.0) < 1e-9);
            Assert(ViewportSizing.Height(1600) == 900);
            Assert(ViewportSizing.Width(350, 1080) == 336);
            Assert(ViewportSizing.Width(double.NaN, 1000) == 1);
        }));
    }
}
