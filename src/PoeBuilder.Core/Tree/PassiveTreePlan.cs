using PoeBuilder.Core.Models;

namespace PoeBuilder.Core.Tree;

/// <summary>Graph IDs, not replacement-skill IDs. No automatic level/quest/weapon budget is implied.</summary>
public sealed record PassiveTreePlan
{
    public string DatasetId { get; init; } = TreeCatalog.PinnedDatasetId;
    public int ClassIndex { get; init; } = 6;
    public int PointLimit { get; init; } // 0 = no manual limit
    public int[] AllocatedNodes { get; init; } = [];
    public Dictionary<int, int> AttributeSelections { get; init; } = [];
    /// <summary>Nodes granted by socketed jewels ("Allocates X"): spent without path cost and exempt
    /// from the connectivity rule, exactly like the game treats them.</summary>
    public int[] JewelAllocatedNodes { get; init; } = [];
    /// <summary>Socketed jewels: tree jewel-socket node id → equipment item id.</summary>
    public Dictionary<int, Guid> Jewels { get; init; } = [];
    public AscendancyPlan? Ascendancy { get; init; }
    public PassiveTreePlan Copy() => this with { AllocatedNodes = [.. AllocatedNodes], AttributeSelections = new(AttributeSelections), JewelAllocatedNodes = [.. JewelAllocatedNodes], Jewels = new(Jewels), Ascendancy = Ascendancy?.Copy() };
    public void ValidateStructure()
    {
        Ascendancy?.ValidateStructure();
        if (string.IsNullOrWhiteSpace(DatasetId) || DatasetId.Length > 160 || ClassIndex is < 0 or > 31 || PointLimit is < 0 or > 10000 ||
            AllocatedNodes is null || AllocatedNodes.Length > 10000 || AllocatedNodes.Any(id => id is < 0 or > 65535) || AllocatedNodes.Distinct().Count() != AllocatedNodes.Length ||
            JewelAllocatedNodes is null || JewelAllocatedNodes.Length > 64 || JewelAllocatedNodes.Any(id => id is < 0 or > 65535) || JewelAllocatedNodes.Distinct().Count() != JewelAllocatedNodes.Length ||
            Jewels is null || Jewels.Count > 32 || Jewels.Keys.Any(id => id is < 0 or > 65535) ||
            AttributeSelections is null || AttributeSelections.Count > 10000 || AttributeSelections.Any(p => p.Key is < 0 or > 65535 || p.Value is < 0 or > 65535))
            throw new BuildFormatException("Invalid passive-tree plan structure.");
    }
}
public sealed class TreeRuleException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Independent, undirected main-tree allocator. Special mechanics are rejected, never approximated.</summary>
public sealed class PassiveTreeEngine(TreeCatalog catalog)
{
    public TreeCatalog Catalog { get; } = catalog;
    public int Start(PassiveTreePlan plan) => Catalog.Classes.FirstOrDefault(c => c.Index == plan.ClassIndex)?.StartNodeId ?? throw new TreeRuleException("TreeInvalidClass");
    public int Cost(IEnumerable<int> nodes) => nodes.Sum(id => Catalog.Nodes[id].PointCost);
    public int Spent(PassiveTreePlan plan) => Cost(plan.AllocatedNodes.Except(plan.JewelAllocatedNodes));
    public bool CanTraverse(int id, PassiveTreePlan plan) => Catalog.Nodes.TryGetValue(id, out var n) && n.IsSupported && (!n.IsStart || id == Start(plan));
    public void Validate(PassiveTreePlan plan)
    {
        plan.ValidateStructure();
        if (plan.DatasetId != Catalog.DatasetId) throw new TreeRuleException("TreeDatasetMismatch");
        if (plan.Ascendancy is not null) AscendancyRules.Validate(Catalog, plan);
        int start = Start(plan);
        var free = plan.JewelAllocatedNodes.ToHashSet();
        // Jewel-granted nodes must exist on the tree and stay ordinary passables; they are exempt
        // from connectivity and cost no points (the jewel pays, not the character).
        foreach (int id in free)
            if (!Catalog.Nodes.TryGetValue(id, out var fn) || !fn.IsSupported || fn.IsStart || fn.IsAscendancy)
                throw new TreeRuleException("TreeInvalidSaved");
        if (free.Any(id => !plan.AllocatedNodes.Contains(id))) throw new TreeRuleException("TreeInvalidSaved");
        foreach (var (socket, _) in plan.Jewels)
            if (!Catalog.Nodes.TryGetValue(socket, out var sn) || !sn.IsJewel || !plan.AllocatedNodes.Contains(socket))
                throw new TreeRuleException("TreeInvalidSaved");
        var allocated = plan.AllocatedNodes.ToHashSet();
        if (allocated.Contains(start) || allocated.Any(id => !CanTraverse(id, plan))) throw new TreeRuleException("TreeInvalidSaved");
        if (plan.PointLimit > 0 && Spent(plan) > plan.PointLimit) throw new TreeRuleException("TreeOverBudget");
        
        static HashSet<int> WithoutFree(HashSet<int> set, HashSet<int> free) { var c = new HashSet<int>(set); c.ExceptWith(free); return c; }
        foreach (int id in allocated.Where(id => Catalog.Nodes[id].IsAttribute))
            if (!plan.AttributeSelections.TryGetValue(id, out int choice) || !ValidAttribute(choice)) throw new TreeRuleException("TreeInvalidAttribute");
        if (plan.AttributeSelections.Any(p => !allocated.Contains(p.Key) || !Catalog.Nodes[p.Key].IsAttribute || !ValidAttribute(p.Value))) throw new TreeRuleException("TreeInvalidAttribute");
        allocated.Add(start);
        // Jewel-granted notables are legitimately disconnected: exclude them from the reachability law.
        var connected = WithoutFree(allocated, free);
        if (Reachable(start, connected).Count != connected.Count) throw new TreeRuleException("TreeDisconnected");
    }
    private bool ValidAttribute(int id) => id is 26297 or 14927 or 57022 && Catalog.Variants.ContainsKey(id);
    public int[] FindPath(PassiveTreePlan plan, int target)
    {
        Validate(plan);
        if (!CanTraverse(target, plan)) throw new TreeRuleException("TreeUnsupported");
        var owned = plan.AllocatedNodes.Append(Start(plan)).ToHashSet();
        if (owned.Contains(target)) return [];
        var queue = new PriorityQueue<int, (int Cost, int Id)>();
        var previous = owned.ToDictionary(id => id, _ => -1);
        var distance = owned.ToDictionary(id => id, _ => 0);
        var settled = new HashSet<int>();
        foreach (int id in owned.Order()) queue.Enqueue(id, (0, id));
        while (queue.TryDequeue(out int node, out _))
        {
            if (!settled.Add(node)) continue;
            if (node == target)
            {
                var path = new List<int>(); int step = target;
                while (previous[step] != -1) { path.Add(step); step = previous[step]; }
                path.Reverse(); return path.ToArray();
            }
            foreach (int next in Catalog.Neighbors[node])
            {
                if (settled.Contains(next) || !CanTraverse(next, plan)) continue;
                int cost = distance[node] + Catalog.Nodes[next].PointCost;
                if (distance.TryGetValue(next, out int old) && old <= cost) continue;
                distance[next] = cost; previous[next] = node; queue.Enqueue(next, (cost, next));
            }
        }
        throw new TreeRuleException("TreeNoPath");
    }
    public PassiveTreePlan Allocate(PassiveTreePlan plan, int target, int defaultAttribute)
    {
        if (!ValidAttribute(defaultAttribute)) throw new TreeRuleException("TreeInvalidAttribute");
        var path = FindPath(plan, target);
        if (Catalog.Nodes[target].MultipleChoiceParent is int parent && parent != 0)
        {
            var selected = plan.AllocatedNodes.FirstOrDefault(id => Catalog.Nodes.TryGetValue(id, out var node) && node.MultipleChoiceParent == parent && id != target);
            if (selected != 0) throw new TreeRuleException("TreeMultipleChoice");
        }
        if (plan.PointLimit > 0 && Spent(plan) + Cost(path) > plan.PointLimit) throw new TreeRuleException("TreeOverBudget");
        var choices = new Dictionary<int, int>(plan.AttributeSelections);
        foreach (int id in path.Where(id => Catalog.Nodes[id].IsAttribute)) choices[id] = defaultAttribute;
        var result = plan with { AllocatedNodes = plan.AllocatedNodes.Concat(path).Order().ToArray(), AttributeSelections = choices };
        Validate(result); return result;
    }
    /// <summary>Includes the selected node and every branch that loses connection to this class start.</summary>
    public int[] RefundSet(PassiveTreePlan plan, int target)
    {
        Validate(plan);
        if (!plan.AllocatedNodes.Contains(target)) return [];
        var allowed = plan.AllocatedNodes.Append(Start(plan)).ToHashSet(); allowed.Remove(target);
        var connected = Reachable(Start(plan), allowed);
        return plan.AllocatedNodes.Where(id => !connected.Contains(id)).Order().ToArray();
    }
    public PassiveTreePlan Refund(PassiveTreePlan plan, int target)
    {
        var removed = RefundSet(plan, target).ToHashSet();
        var next = plan with { AllocatedNodes = plan.AllocatedNodes.Where(id => !removed.Contains(id)).ToArray(), AttributeSelections = plan.AttributeSelections.Where(p => !removed.Contains(p.Key)).ToDictionary() };
        Validate(next); return next;
    }
    public PassiveTreePlan SetAttribute(PassiveTreePlan plan, int node, int choice)
    {
        Validate(plan);
        if (!plan.AllocatedNodes.Contains(node) || !Catalog.Nodes[node].IsAttribute || !ValidAttribute(choice)) throw new TreeRuleException("TreeInvalidAttribute");
        var choices = new Dictionary<int, int>(plan.AttributeSelections) { [node] = choice };
        return plan.Copy() with { AttributeSelections = choices };
    }
    private HashSet<int> Reachable(int start, HashSet<int> allowed)
    {
        var result = new HashSet<int> { start }; var queue = new Queue<int>(); queue.Enqueue(start);
        while (queue.TryDequeue(out int current))
            foreach (int next in Catalog.Neighbors[current]) if (allowed.Contains(next) && result.Add(next)) queue.Enqueue(next);
        return result;
    }
}

/// <summary>Bounded, deep-copy undo history, isolated per open build.</summary>
public sealed class TreeHistory
{
    private readonly List<PassiveTreePlan> _undo = [], _redo = [];
    public bool CanUndo => _undo.Count != 0;
    public bool CanRedo => _redo.Count != 0;
    public void Record(PassiveTreePlan current) { _undo.Add(current.Copy()); if (_undo.Count > 100) _undo.RemoveAt(0); _redo.Clear(); }
    public PassiveTreePlan Undo(PassiveTreePlan current) => Move(_undo, _redo, current);
    public PassiveTreePlan Redo(PassiveTreePlan current) => Move(_redo, _undo, current);
    public void Clear() { _undo.Clear(); _redo.Clear(); }
    private static PassiveTreePlan Move(List<PassiveTreePlan> from, List<PassiveTreePlan> to, PassiveTreePlan current)
    {
        if (from.Count == 0) return current.Copy();
        to.Add(current.Copy()); var next = from[^1]; from.RemoveAt(from.Count - 1); return next.Copy();
    }
}
