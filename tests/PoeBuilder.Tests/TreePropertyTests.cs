using System.Buffers.Binary;
using System.Text.Json;
using PoeBuilder.Core.Tree;

internal static class TreePropertyTests
{
    private static void Assert(bool condition, string detail = "Assertion failed") { if (!condition) throw new Exception(detail); }
    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Data", "Tree");
        var catalog = TreeCatalog.LoadPinned(Path.Combine(folder, "data.json"));
        var engine = new PassiveTreeEngine(catalog);
        await test("GGG: atlas coordinates fit the actual PNG dimensions", async () =>
        {
            var png = await File.ReadAllBytesAsync(Path.Combine(folder, "skills.png"));
            Assert(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
            int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "skills.json")));
            var frames = json.RootElement.GetProperty("frames");
            foreach (var p in frames.EnumerateObject())
            {
                var f = p.Value.GetProperty("frame"); int x = f.GetProperty("x").GetInt32(), y = f.GetProperty("y").GetInt32(), w = f.GetProperty("w").GetInt32(), h = f.GetProperty("h").GetInt32();
                Assert(x >= 0 && y >= 0 && w > 0 && h > 0 && x + w <= width && y + h <= height, p.Name);
            }
            foreach (var cls in catalog.Classes)
            {
                var plan = new PassiveTreePlan { ClassIndex = cls.Index };
                foreach (var n in catalog.Nodes.Values.Where(n => n.IsSupported && !n.IsJewel))
                {
                    string key = (n.IsKeystone ? "keystoneActive:" : n.IsNotable ? "notableActive:" : "normalActive:") + catalog.Describe(n.Id, plan).Icon;
                    Assert(frames.TryGetProperty(key, out _), cls.Name + " / " + n.Id + " / " + key);
                }
            }
        });
        await test("GGG: allocation costs agree with independent single-source BFS distances", () =>
        {
            foreach (var cls in catalog.Classes)
            {
                var plan = new PassiveTreePlan { ClassIndex = cls.Index }; var distance = new Dictionary<int, int> { [cls.StartNodeId] = 0 };
                var queue = new Queue<int>(); queue.Enqueue(cls.StartNodeId);
                while (queue.TryDequeue(out int current))
                    foreach (int next in catalog.Neighbors[current])
                        if (engine.CanTraverse(next, plan) && !distance.ContainsKey(next)) { distance[next] = distance[current] + 1; queue.Enqueue(next); }
                Assert(distance.Count > 3000, cls.Name + " reachable: " + distance.Count);
                // Spread deterministic samples over the entire graph, including far outer nodes.
                foreach (var pair in distance.OrderBy(p => p.Key).Where((_, i) => i % 113 == 0))
                {
                    var path = engine.FindPath(plan, pair.Key); Assert(path.Length == pair.Value, $"{cls.Name} #{pair.Key}");
                    var built = engine.Allocate(plan, pair.Key, 26297); engine.Validate(built);
                    Assert(built.AllocatedNodes.Length == pair.Value);
                }
            }
            return Task.CompletedTask;
        });
        await test("GGG: deterministic mixed allocate/refund/attribute operations keep valid plans", () =>
        {
            var random = new Random(20260920); var nodes = catalog.Nodes.Values.Where(n => n.IsSupported && !n.IsStart).Select(n => n.Id).ToArray();
            foreach (var cls in catalog.Classes)
            {
                var plan = new PassiveTreePlan { ClassIndex = cls.Index };
                for (int i = 0; i < 36; i++)
                {
                    if (i % 3 == 2 && plan.AllocatedNodes.Length > 0) plan = engine.Refund(plan, plan.AllocatedNodes[random.Next(plan.AllocatedNodes.Length)]);
                    else
                    {
                        try { plan = engine.Allocate(plan, nodes[random.Next(nodes.Length)], i % 2 == 0 ? 14927 : 57022); }
                        catch (TreeRuleException e) when (e.Code == "TreeNoPath") { }
                    }
                    if (plan.AttributeSelections.Count > 0) plan = engine.SetAttribute(plan, plan.AttributeSelections.Keys.First(), 26297);
                    engine.Validate(plan);
                }
            }
            return Task.CompletedTask;
        });
    }
}
