using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoeBuilder.Core.Tree;

public sealed record PassiveNode(int Id, string StableId, string Name, string Icon, string[] Stats,
    double X, double Y, int Group, bool IsNotable, bool IsKeystone, bool IsJewel, bool IsAttribute,
    bool IsMastery, bool IsAscendancy, bool HasUnsupportedConstraint, bool IsAnointOnly, int[] ClassStarts, int PointCost = 1, string AscendancyId = "", bool IsAscendancyStart = false, int MultipleChoiceParent = 0)
{
    public bool IsStart => ClassStarts.Length != 0;
    public bool IsSupported => !IsAscendancy && !IsMastery && !HasUnsupportedConstraint && !IsAnointOnly && !string.IsNullOrWhiteSpace(StableId);
}
public sealed record PassiveVariant(int Id, string Name, string Icon, string[] Stats) { public override string ToString() => Name; }
public sealed record TreeClass(int Index, string Name, int StartNodeId, IReadOnlyDictionary<int, int> Overrides, int BaseStrength = 0, int BaseDexterity = 0, int BaseIntelligence = 0) { public override string ToString() => Name; }
public sealed record TreeEdge(int From, int To, double? CenterX, double? CenterY);

public sealed class TreeCatalog
{
    public const string PinnedDatasetId = "ggg-poe2-0.5.5-bd87e651";
    public const string PinnedHash = "b52be9c4f17e4114064255ef1b8c58292e9db0e395d95af235a8d3fef0d44642";
    public string DatasetId { get; }
    public string Version => "0.5.5";
    public IReadOnlyDictionary<int, PassiveNode> Nodes { get; }
    public IReadOnlyDictionary<int, PassiveVariant> Variants { get; }
    public IReadOnlyList<TreeClass> Classes { get; }
    public IReadOnlyList<TreeEdge> Edges { get; }
    public IReadOnlyDictionary<int, int[]> Neighbors { get; }
    public int ExportNodeCount { get; }
    public IReadOnlyList<AscendancyDefinition> Ascendancies { get; private set; } = [];
    public string? PortraitKey { get; private set; }
    public bool IsAscendancyGraph => PortraitKey is not null;
    public IEnumerable<PassiveVariant> AttributeChoices => new[] { 26297, 14927, 57022 }.Where(Variants.ContainsKey).Select(id => Variants[id]);

    public TreeCatalog(string datasetId, IReadOnlyDictionary<int, PassiveNode> nodes, IReadOnlyList<TreeClass> classes,
        IReadOnlyList<TreeEdge> edges, IReadOnlyDictionary<int, PassiveVariant> variants, int exportNodeCount = 0)
    {
        DatasetId = datasetId; Nodes = nodes; Classes = classes; Edges = edges; Variants = variants; ExportNodeCount = exportNodeCount;
        var map = nodes.Keys.ToDictionary(id => id, _ => new HashSet<int>());
        foreach (var edge in edges)
        {
            if (!map.ContainsKey(edge.From) || !map.ContainsKey(edge.To) || edge.From == edge.To) continue;
            map[edge.From].Add(edge.To); map[edge.To].Add(edge.From);
        }
        Neighbors = map.ToDictionary(p => p.Key, p => p.Value.Order().ToArray());
    }
    public PassiveVariant Describe(int nodeId, PassiveTreePlan plan)
    {
        var node = Nodes[nodeId];
        if (node.IsAttribute && plan.AttributeSelections.TryGetValue(nodeId, out int attribute) && Variants.TryGetValue(attribute, out var selected)) return selected;
        var cls = Classes.FirstOrDefault(c => c.Index == plan.ClassIndex);
        if (cls is not null && cls.Overrides.TryGetValue(nodeId, out int replacement) && Variants.TryGetValue(replacement, out var variant)) return variant;
        if (node.IsStart)
        {
            var name = cls?.StartNodeId == nodeId ? cls.Name : string.Join(" / ", Classes.Where(c => c.StartNodeId == nodeId).Select(c => c.Name));
            return new(nodeId, string.IsNullOrEmpty(name) ? node.Name : name, node.Icon, node.Stats);
        }
        return new(nodeId, node.Name, node.Icon, node.Stats);
    }
    public static string PlainText(string text)
    {
        text = Regex.Replace(text, @"\[([^\]|]+)\|([^\]]+)\]", "$2");
        text = Regex.Replace(text, @"\[([^\]]+)\]", "$1");
        return Regex.Replace(text, @"</?\w+>", "");
    }
    public static TreeCatalog LoadPinned(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > 16 * 1024 * 1024) throw new InvalidDataException("Tree export exceeds size limit.");
        var bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(PinnedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Official tree snapshot checksum mismatch. The existing build has not been modified.");
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        var groups = new Dictionary<int, (double X, double Y)>();
        foreach (var p in root.GetProperty("groups").EnumerateObject())
            groups[int.Parse(p.Name)] = (p.Value.GetProperty("x").GetDouble(), p.Value.GetProperty("y").GetDouble());
        var nodes = new Dictionary<int, PassiveNode>(); int total = 0;
        foreach (var p in root.GetProperty("nodes").EnumerateObject())
        {
            total++;
            var n = p.Value;
            if (!int.TryParse(p.Name, out int id) || !n.TryGetProperty("x", out var x) || !n.TryGetProperty("y", out var y)) continue;
            bool ascendancy = !string.IsNullOrEmpty(Text(n, "ascendancyId"));
            // This release deliberately handles the MAIN tree only.
            if (string.IsNullOrWhiteSpace(Text(n, "name"))) continue;
            bool constraints = n.TryGetProperty("unlockConstraint", out _) || (!ascendancy && Flag(n, "isFree")) ||
                n.TryGetProperty("grantedPassivePoints", out _) || n.TryGetProperty("passivePointsGranted", out _) || n.TryGetProperty("weaponPassivePointsGranted", out _);
            int choiceParent = n.TryGetProperty("multipleChoiceParent", out var parent) && parent.ValueKind == JsonValueKind.Number ? parent.GetInt32() : 0;
            var starts = n.TryGetProperty("classStartIndex", out var indices) ? indices.EnumerateArray().Select(i => i.GetInt32()).ToArray() : [];
            nodes.Add(id, new(id, Text(n, "id"), PlainText(Text(n, "name")), Text(n, "icon"), Strings(n, "stats"), x.GetDouble(), y.GetDouble(), n.GetProperty("group").GetInt32(),
                Flag(n, "isNotable"), Flag(n, "isKeystone"), Flag(n, "isJewelSocket"), Flag(n, "isGenericAttribute"), Flag(n, "isMastery"), ascendancy, constraints, Flag(n, "isBlighted"), starts, ascendancy && Flag(n, "isFree") ? 0 : 1, Text(n, "ascendancyId"), Flag(n, "isAscendancyStart"), choiceParent));
        }
        var variants = new Dictionary<int, PassiveVariant>();
        foreach (var p in root.GetProperty("skillOverrides").EnumerateObject())
            variants.Add(int.Parse(p.Name), new(int.Parse(p.Name), PlainText(Text(p.Value, "name")), Text(p.Value, "icon"), Strings(p.Value, "stats")));
        var classes = new List<TreeClass>(); int index = 0;
        foreach (var cls in root.GetProperty("classes").EnumerateArray())
        {
            var start = nodes.Values.FirstOrDefault(n => n.ClassStarts.Contains(index));
            bool available = cls.GetProperty("ascendancies").EnumerateArray().Any(a => !string.IsNullOrWhiteSpace(Text(a, "name")));
            var overrides = new Dictionary<int, int>();
            if (cls.TryGetProperty("overridePairs", out var pairs) && pairs.ValueKind == JsonValueKind.Object)
                foreach (var pair in pairs.EnumerateObject()) overrides.Add(int.Parse(pair.Name), pair.Value.GetInt32());
            if (start is not null && available) classes.Add(new(index, Text(cls, "name"), start.Id, overrides,
                Num(cls, "base_str"), Num(cls, "base_dex"), Num(cls, "base_int")));
            index++;
        }
        var edges = new List<TreeEdge>();
        foreach (var edge in root.GetProperty("edges").EnumerateArray())
        {
            if (!int.TryParse(edge.GetProperty("from").ToString(), out int from) || !int.TryParse(edge.GetProperty("to").ToString(), out int to) || !nodes.ContainsKey(from) || !nodes.ContainsKey(to)) continue;
            double? cx = null, cy = null;
            if (edge.TryGetProperty("orbitX", out var orbitX) && edge.TryGetProperty("orbitY", out var orbitY)) { cx = orbitX.GetDouble(); cy = orbitY.GetDouble(); }
            else if (edge.TryGetProperty("orbit", out _) && nodes[from].Group == nodes[to].Group && groups.TryGetValue(nodes[from].Group, out var center)) { cx = center.X; cy = center.Y; }
            edges.Add(new(from, to, cx, cy));
        }
        var mainNodes = nodes.Where(p => !p.Value.IsAscendancy).ToDictionary();
        var result = new TreeCatalog(PinnedDatasetId, mainNodes, classes, edges.Where(e => mainNodes.ContainsKey(e.From) && mainNodes.ContainsKey(e.To)).ToArray(), variants, total);
        var ascendancies = new List<AscendancyDefinition>(); index = 0;
        foreach (var cls in root.GetProperty("classes").EnumerateArray())
        {
            foreach (var asc in cls.GetProperty("ascendancies").EnumerateArray())
            {
                string name = Text(asc, "name"), id = Text(asc, "id");
                if (name.Length == 0 || !classes.Any(c => c.Index == index)) continue;
                var overrides = new Dictionary<int, int>();
                if (asc.TryGetProperty("overridePairs", out var pairs) && pairs.ValueKind == JsonValueKind.Object)
                    foreach (var pair in pairs.EnumerateObject()) overrides.Add(int.Parse(pair.Name), pair.Value.GetInt32());
                string graphId = id;
                // Variant ascendancies (e.g. Abyssal Lich) reuse the graph identified by their override keys.
                if (!nodes.Values.Any(n => n.AscendancyId == graphId) && overrides.Count > 0)
                {
                    var candidates = overrides.Keys.Where(nodes.ContainsKey).Select(key => nodes[key].AscendancyId).Where(key => key.Length > 0).Distinct().ToArray();
                    if (candidates.Length == 1) graphId = candidates[0];
                }
                var members = nodes.Values.Where(n => n.AscendancyId == graphId).ToArray();
                var start = members.SingleOrDefault(n => n.IsAscendancyStart);
                if (start is null) continue;
                // An isolated domain graph: the main-tree bridge and other ascendancies never enter pathfinding.
                var local = members.ToDictionary(n => n.Id, n => n with { IsAscendancy = false, ClassStarts = n.IsAscendancyStart ? [index] : [] });
                var graph = new TreeCatalog(PinnedDatasetId + ":asc:" + id, local, [new(index, name, start.Id, overrides)],
                    edges.Where(e => local.ContainsKey(e.From) && local.ContainsKey(e.To)).ToArray(), variants, members.Length) { PortraitKey = id };
                ascendancies.Add(new(id, name, index, graphId, graph));
            }
            index++;
        }
        result.Ascendancies = ascendancies;
        return result;
    }
    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    private static int Num(JsonElement item, string key) => item.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
    private static bool Flag(JsonElement item, string key) => item.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
    private static string[] Strings(JsonElement item, string key) => item.TryGetProperty(key, out var p) ? p.EnumerateArray().Select(v => PlainText(v.GetString() ?? "")).ToArray() : [];
}
