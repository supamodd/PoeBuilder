using PoeBuilder.Core.Calculation;
using PoeBuilder.Core.Models;

namespace PoeBuilder.Core.Tree;

/// <summary>
/// A jewel line that changes ALLOCATION instead of stats: "Passives in radius of Resonance can be
/// Allocated without being connected to your tree" (From Nothing, a Diamond) and "Passives in radius can
/// be allocated without being connected to your tree" (Intuitive Leap). PoB2 stores both on the item
/// (Modules/ModParser.lua:5507-5511 → <c>JewelData.fromNothingKeystone</c> /
/// <c>JewelData.intuitiveLeapLike</c>) and then reaches the nodes of that radius from the jewel's own
/// socket (Classes/PassiveSpec.lua:1361-1392); for From Nothing the radius belongs to the named KEYSTONE,
/// not to the socket, so one jewel covers every allocated keystone of that name.
/// </summary>
/// <param name="RadiusIndex">PoB2's 1-based <c>jewelRadiusIndex</c> band; 0 is never a rule.</param>
/// <param name="KeystoneName">The keystone whose radius applies; empty means the socket's own radius.</param>
public sealed record RadiusAllocationRule(int RadiusIndex, string KeystoneName)
{
    /// <summary>True for the From Nothing shape: the radius is measured from the named keystone.</summary>
    public bool FromKeystone => KeystoneName.Length > 0;
}

/// <summary>Graph IDs, not replacement-skill IDs. No automatic level/quest/weapon budget is implied.</summary>
public sealed record PassiveTreePlan
{
    public const int FixedPointLimit = 125;
    public string DatasetId { get; init; } = TreeCatalog.PinnedDatasetId;
    public int ClassIndex { get; init; } = 6;
    public int PointLimit { get; init; } = FixedPointLimit;
    public int[] AllocatedNodes { get; init; } = [];
    public Dictionary<int, int> AttributeSelections { get; init; } = [];
    /// <summary>Nodes granted by socketed jewels ("Allocates X"): spent without path cost and exempt
    /// from the connectivity rule, exactly like the game treats them.</summary>
    public int[] JewelAllocatedNodes { get; init; } = [];
    /// <summary>Socketed jewels: tree jewel-socket node id → equipment item id.</summary>
    public Dictionary<int, Guid> Jewels { get; init; } = [];
    /// <summary>Extra class-start nodes opened by unique jewels ("Can Allocate Passive Skills from the
    /// {Class}'s starting point", PoB2 jewelData.alternateClassStart). Nodes in such a region stay
    /// reachable from that start exactly like the normal class start, at zero path cost for the
    /// start node itself.</summary>
    public int[] AlternateStartNodes { get; init; } = [];
    /// <summary>Weapon-set allocations: node id → 1 or 2. PoB2 stores them as child elements of the spec
    /// (<c>&lt;WeaponSet1 nodes="…"/&gt;</c>, Classes/PassiveSpec.lua:272-277) and gives every such node an
    /// allocation mode, so its stats count only while that weapon set is active. Nodes not listed here
    /// belong to both sets.</summary>
    public Dictionary<int, int> WeaponSetNodes { get; init; } = [];
    /// <summary>Socketed radius jewels that change ALLOCATION ("From Nothing": "Passives in radius of
    /// Resonance can be Allocated without being connected to your tree"; Intuitive Leap: "Passives in
    /// radius can be allocated without being connected to your tree"): socket node id → rule. PoB2 reads
    /// the line off the jewel and THEN reaches the nodes of that radius from the socket or from the named
    /// keystone (Modules/ModParser.lua:5507-5511, Classes/PassiveSpec.lua:1361-1392), so such a node needs
    /// no edge to the tree — which is why a build that uses one must not be rerouted through passives it
    /// never took.</summary>
    public Dictionary<int, RadiusAllocationRule> RadiusJewels { get; init; } = [];
    public AscendancyPlan? Ascendancy { get; init; }
    public PassiveTreePlan Copy() => this with
    {
        AllocatedNodes = [.. AllocatedNodes],
        AttributeSelections = new(AttributeSelections),
        JewelAllocatedNodes = [.. JewelAllocatedNodes],
        Jewels = new(Jewels),
        RadiusJewels = new(RadiusJewels),
        WeaponSetNodes = new(WeaponSetNodes),
        AlternateStartNodes = [.. AlternateStartNodes],
        Ascendancy = Ascendancy?.Copy()
    };
    public void ValidateStructure()
    {
        Ascendancy?.ValidateStructure();
        if (string.IsNullOrWhiteSpace(DatasetId) || DatasetId.Length > 160 || ClassIndex is < 0 or > 31 || PointLimit is < 0 or > 10000 ||
            AllocatedNodes is null || AllocatedNodes.Length > 10000 || AllocatedNodes.Any(id => id is < 0 or > 65535) || AllocatedNodes.Distinct().Count() != AllocatedNodes.Length ||
            JewelAllocatedNodes is null || JewelAllocatedNodes.Length > 64 || JewelAllocatedNodes.Any(id => id is < 0 or > 65535) || JewelAllocatedNodes.Distinct().Count() != JewelAllocatedNodes.Length ||
            AlternateStartNodes is null || AlternateStartNodes.Length > 32 || AlternateStartNodes.Any(id => id is < 0 or > 65535) || AlternateStartNodes.Distinct().Count() != AlternateStartNodes.Length ||
            Jewels is null || Jewels.Count > 32 || Jewels.Keys.Any(id => id is < 0 or > 65535) ||
            RadiusJewels is null || RadiusJewels.Count > 32 ||
            RadiusJewels.Any(p => p.Key is < 0 or > 65535 || p.Value is null || p.Value.RadiusIndex is < 1 or > 12 ||
                p.Value.KeystoneName is null || p.Value.KeystoneName.Length > 80) ||
            WeaponSetNodes is null || WeaponSetNodes.Count > 200 || WeaponSetNodes.Any(p => p.Key is < 0 or > 65535 || p.Value is not (1 or 2)) ||
            WeaponSetNodes.Keys.Any(id => !AllocatedNodes.Contains(id)) ||
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
    /// <summary>The attribute a generic "+5 to any Attribute" node takes when nothing else says otherwise —
    /// the engine's own default, which is what a saved state without a choice falls back to.</summary>
    public const int DefaultAttribute = 26297;
    public int Start(PassiveTreePlan plan) => Catalog.Classes.FirstOrDefault(c => c.Index == plan.ClassIndex)?.StartNodeId ?? throw new TreeRuleException("TreeInvalidClass");
    /// <summary>The class start plus any alternate class-start nodes opened by unique jewels. These
    /// are the roots for connectivity: every allocated main-tree node must be reachable from one of them.</summary>
    public int[] Roots(PassiveTreePlan plan) => plan.AlternateStartNodes.Length == 0
        ? [Start(plan)]
        : plan.AlternateStartNodes.Append(Start(plan)).Distinct().ToArray();
    /// <summary>
    /// The centres and bands of a plan's radius-allocation rules, with keystone names resolved against the
    /// catalog. PoB2 measures the From Nothing radius from the NAMED KEYSTONE and never checks whether that
    /// keystone is allocated — <c>tree.keystoneMap[keyName].nodesInRadius</c> is the tree's own table
    /// (Classes/PassiveSpec.lua:1378-1388), which is why a Twister build reaches the cluster around
    /// "Resonance" without ever taking Resonance itself. The SOCKET does have to be allocated and hold the
    /// jewel, because that is what carries the line.
    /// </summary>
    public IReadOnlyList<(int Centre, int RadiusIndex)> RadiusCentres(PassiveTreePlan plan)
    {
        var centres = new List<(int, int)>();
        foreach (var (socket, rule) in plan.RadiusJewels)
        {
            if (!plan.AllocatedNodes.Contains(socket) || rule.RadiusIndex is < 1 or > 12) continue;
            if (!rule.FromKeystone) { centres.Add((socket, rule.RadiusIndex)); continue; }
            int keystone = KeystoneId(rule.KeystoneName);
            if (keystone != 0) centres.Add((keystone, rule.RadiusIndex));
        }
        return centres;
    }
    /// <summary>The tree node id of a keystone by name (0 when the tree has no such keystone), i.e. PoB2's
    /// <c>tree.keystoneMap[name]</c> lookup.</summary>
    public static int KeystoneId(TreeCatalog catalog, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        string wanted = name.Trim();
        var keystone = catalog.Nodes.Values.FirstOrDefault(n => n.IsKeystone && n.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        return keystone?.Id ?? 0;
    }
    public int KeystoneId(string name) => KeystoneId(Catalog, name);
    /// <summary>
    /// Every node a radius rule set can reach, i.e. the nodes PoB2 lets you allocate with no edge to the
    /// tree (<c>nodesInRadius[radiusIndex]</c> of the keystone or of the socket). The pool is the set a
    /// radius may pick from; the plan's own traversable nodes when the plan is already known.
    /// </summary>
    public HashSet<int> RadiusAt(IEnumerable<(int Centre, int RadiusIndex)> centres, IEnumerable<int>? pool = null)
    {
        var candidates = pool ?? Catalog.Nodes.Values.Where(n => n.IsSupported).Select(n => n.Id);
        var list = candidates as IReadOnlyCollection<int> ?? candidates.ToArray();
        var result = new HashSet<int>();
        foreach (var (centre, index) in centres)
            foreach (int id in JewelRadius.NodesInRadius(Catalog, centre, index, list)) result.Add(id);
        return result;
    }
    public HashSet<int> RadiusAllocatable(PassiveTreePlan plan) =>
        RadiusAt(RadiusCentres(plan), Catalog.Nodes.Values.Where(n => CanTraverse(n.Id, plan)).Select(n => n.Id).ToArray());
    public int Cost(IEnumerable<int> nodes) => nodes.Sum(id => Catalog.Nodes[id].PointCost);
    public int Spent(PassiveTreePlan plan) => Cost(plan.AllocatedNodes.Except(plan.JewelAllocatedNodes));
    public bool CanTraverse(int id, PassiveTreePlan plan) => Catalog.Nodes.TryGetValue(id, out var n) && n.IsSupported
        && (n.UnlockNodeIds is null || n.UnlockNodeIds.All(plan.AllocatedNodes.Contains))
        && (!n.IsStart || id == Start(plan) || plan.AlternateStartNodes.Contains(id));
    public void Validate(PassiveTreePlan plan)
    {
        plan.ValidateStructure();
        if (plan.DatasetId != Catalog.DatasetId) throw new TreeRuleException("TreeDatasetMismatch");
        if (plan.Ascendancy is not null) AscendancyRules.Validate(Catalog, plan);
        int start = Start(plan);
        var roots = Roots(plan);
        var free = plan.JewelAllocatedNodes.ToHashSet();
        // Jewel-granted nodes must exist on the tree and stay ordinary passables; they are exempt
        // from connectivity and cost no points (the jewel pays, not the character).
        foreach (int id in free)
            if (!Catalog.Nodes.TryGetValue(id, out var fn) || !fn.CanBeGranted)
                throw new TreeRuleException("TreeInvalidSaved");
        if (free.Any(id => !plan.AllocatedNodes.Contains(id))) throw new TreeRuleException("TreeInvalidSaved");
        foreach (var (socket, _) in plan.Jewels)
            if (!Catalog.Nodes.TryGetValue(socket, out var sn) || !sn.IsJewel || !plan.AllocatedNodes.Contains(socket))
                throw new TreeRuleException("TreeInvalidSaved");
        // A radius-allocation rule only means something for a socket that is really allocated and really
        // carries a jewel. The keystone it names does NOT have to be allocated: PoB2 reads the radius from
        // the tree's own keystone table, which is how a build reaches the cluster around a keystone it
        // never took.
        foreach (var (socket, rule) in plan.RadiusJewels)
            if (!Catalog.Nodes.TryGetValue(socket, out var rn) || !rn.IsJewel || !plan.AllocatedNodes.Contains(socket) ||
                !plan.Jewels.ContainsKey(socket) || (rule.FromKeystone && KeystoneId(rule.KeystoneName) == 0))
                throw new TreeRuleException("TreeInvalidSaved");
        // Alternate roots must be real start nodes opened by a unique jewel; anything else is a
        // malformed saved state, not a wall we quietly ignore.
        foreach (int id in plan.AlternateStartNodes)
            if (!Catalog.Nodes.TryGetValue(id, out var alt) || !alt.IsStart)
                throw new TreeRuleException("TreeInvalidSaved");
        var allocated = plan.AllocatedNodes.ToHashSet();
        if (allocated.Contains(start) || allocated.Any(id => !free.Contains(id) && !CanTraverse(id, plan))) throw new TreeRuleException("TreeInvalidSaved");
        if (plan.PointLimit > 0 && Spent(plan) > plan.PointLimit) throw new TreeRuleException("TreeOverBudget");
        
        static HashSet<int> WithoutFree(HashSet<int> set, HashSet<int> free) { var c = new HashSet<int>(set); c.ExceptWith(free); return c; }
        foreach (int id in allocated.Where(id => Catalog.Nodes[id].IsAttribute))
            if (!plan.AttributeSelections.TryGetValue(id, out int choice) || !ValidAttribute(choice)) throw new TreeRuleException("TreeInvalidAttribute");
        if (plan.AttributeSelections.Any(p => !allocated.Contains(p.Key) || !Catalog.Nodes[p.Key].IsAttribute || !ValidAttribute(p.Value))) throw new TreeRuleException("TreeInvalidAttribute");
        foreach (int root in roots) allocated.Add(root);
        // Jewel-granted notables are legitimately disconnected: exclude them from the reachability law.
        var connected = WithoutFree(allocated, free);
        // Radius jewels extend the roots: a node inside the radius of an allocated keystone (From Nothing)
        // or of the socket itself (Intuitive Leap) is legal with no edge at all, and everything BEYOND it is
        // reachable through it — PoB2 makes such nodes depend on the socket node instead of on an edge
        // (Classes/PassiveSpec.lua:1814-1862), which is exactly what these extra roots reproduce.
        var radius = RadiusAllocatable(plan);
        connected.ExceptWith(radius);
        var extendedRoots = roots.Concat(allocated.Where(radius.Contains)).Distinct().ToArray();
        if (Reachable(extendedRoots, connected).Count != connected.Count) throw new TreeRuleException("TreeDisconnected");
    }
    private bool ValidAttribute(int id) => id is 26297 or 14927 or 57022 && Catalog.Variants.ContainsKey(id);
    public int[] FindPath(PassiveTreePlan plan, int target)
    {
        Validate(plan);
        if (!CanTraverse(target, plan)) throw new TreeRuleException("TreeUnsupported");
        var owned = plan.AllocatedNodes.Concat(Roots(plan)).ToHashSet();
        if (owned.Contains(target)) return [];
        // A node reached by a radius jewel costs the node itself and needs no edge: PoB2 spends exactly one
        // point on it (the jewel paid for the reach, not for the node).
        if (RadiusAllocatable(plan).Contains(target)) return [target];
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

    /// <summary>
    /// Verbatim (build-import) allocation. Unlike <see cref="Allocate"/> it never invents intermediate
    /// nodes: the target must already be adjacent (directly connected by an edge) to the allocated set,
    /// so its shortest path is exactly the target itself. Foreign build lists ship every allocated node,
    /// so exact adjacency reproduces the source allocation with no rerouted paths. The class start is
    /// implicit and treated as a no-op. Throws TreeNoPath when the node is not adjacent.
    /// </summary>
    public PassiveTreePlan AllocateVerbatim(PassiveTreePlan plan, int target, int defaultAttribute)
    {
        if (!ValidAttribute(defaultAttribute)) throw new TreeRuleException("TreeInvalidAttribute");
        if (target == Start(plan)) return plan;
        if (!CanTraverse(target, plan)) throw new TreeRuleException("TreeUnsupported");
        if (plan.AllocatedNodes.Contains(target)) return plan;
        var path = FindPath(plan, target);
        if (path.Length != 1 || path[0] != target) throw new TreeRuleException("TreeNoPath");
        if (Catalog.Nodes[target].MultipleChoiceParent is int parent && parent != 0)
        {
            var selected = plan.AllocatedNodes.FirstOrDefault(id => Catalog.Nodes.TryGetValue(id, out var node) && node.MultipleChoiceParent == parent && id != target);
            if (selected != 0) throw new TreeRuleException("TreeMultipleChoice");
        }
        if (plan.PointLimit > 0 && Spent(plan) + Cost(path) > plan.PointLimit) throw new TreeRuleException("TreeOverBudget");
        var choices = new Dictionary<int, int>(plan.AttributeSelections);
        if (Catalog.Nodes[target].IsAttribute) choices[target] = defaultAttribute;
        var result = plan with { AllocatedNodes = plan.AllocatedNodes.Append(target).Order().ToArray(), AttributeSelections = choices };
        Validate(result); return result;
    }
    /// <summary>Includes the selected node and every branch that loses connection to this class start.</summary>
    public int[] RefundSet(PassiveTreePlan plan, int target)
    {
        Validate(plan);
        if (!plan.AllocatedNodes.Contains(target)) return [];
        var allowed = plan.AllocatedNodes.Concat(Roots(plan)).ToHashSet(); allowed.Remove(target);
        var connected = Reachable(Roots(plan), allowed);
        return plan.AllocatedNodes.Where(id => !connected.Contains(id)).Order().ToArray();
    }
    public PassiveTreePlan Refund(PassiveTreePlan plan, int target)
    {
        var removed = RefundSet(plan, target).ToHashSet();
        // Every node-keyed map has to drop what the refund removed, or the saved state would violate its own
        // invariants (weapon-set modes, jewel sockets, radius rules and jewel-granted nodes all name
        // allocated nodes). A rule that loses its SOCKET stops applying (the keystone it names does not have
        // to be allocated at all), so whatever it reached has to go with it — otherwise the remaining plan
        // would be silently disconnected.
        var next = Prune(plan, removed);
        next = KeepReachable(next);
        Validate(next); return next;
    }
    /// <summary>
    /// A plan with the given nodes (and everything that names them) removed. Used by refunds and by every
    /// operation that can stop a radius rule from applying.
    /// </summary>
    private PassiveTreePlan Prune(PassiveTreePlan plan, IReadOnlySet<int> removed) => plan with
    {
        AllocatedNodes = plan.AllocatedNodes.Where(id => !removed.Contains(id)).ToArray(),
        AttributeSelections = plan.AttributeSelections.Where(p => !removed.Contains(p.Key)).ToDictionary(),
        WeaponSetNodes = plan.WeaponSetNodes.Where(p => !removed.Contains(p.Key)).ToDictionary(),
        Jewels = plan.Jewels.Where(p => !removed.Contains(p.Key)).ToDictionary(),
        RadiusJewels = plan.RadiusJewels.Where(p => !removed.Contains(p.Key)).ToDictionary(),
        JewelAllocatedNodes = plan.JewelAllocatedNodes.Where(id => !removed.Contains(id)).ToArray()
    };
    /// <summary>
    /// Drops the allocated nodes that nothing reaches any more. A socket that loses its radius jewel (or a
    /// refunded keystone that a From Nothing jewel pointed at) takes with it exactly what the rule used to
    /// reach — the game refunds those points the same way, and keeping them would leave a plan that fails its
    /// own connectivity law.
    /// </summary>
    public PassiveTreePlan KeepReachable(PassiveTreePlan plan) => Disconnected(plan) is { Count: > 0 } gone ? Prune(plan, gone) : plan;
    /// <summary>The allocated nodes that no root and no radius rule reaches any more — the plan's own
    /// connectivity law, used to close a refund after a rule stopped applying.</summary>
    private HashSet<int> Disconnected(PassiveTreePlan plan)
    {
        var allocated = plan.AllocatedNodes.ToHashSet();
        var radius = RadiusAllocatable(plan);
        var check = allocated.Where(id => !plan.JewelAllocatedNodes.Contains(id) && !radius.Contains(id)).ToHashSet();
        var roots = Roots(plan).Concat(allocated.Where(radius.Contains)).ToArray();
        // Reachable() only walks nodes that are IN its allowed set, so the roots must be part of it (this is
        // exactly what Validate does before it calls the same walk).
        foreach (int root in roots) check.Add(root);
        var reached = Reachable(roots, check);
        return check.Where(id => !reached.Contains(id)).ToHashSet();
    }
    /// <summary>
    /// Brings a saved state that fails <see cref="Validate"/> back to one the engine accepts, without
    /// inventing anything. Three things can be wrong in a state that an older version of this program or a
    /// hand-edited document left behind:
    /// <list type="number">
    /// <item>a node the graph cannot traverse, but an ITEM can grant (an anoint such as the Delirium
    /// "Paragon" node, a jewel's "Allocates X" socket) — it moves into the granted set, where it costs no
    /// point and needs no path, exactly where the importer and the game put it;</item>
    /// <item>a jewel socket that HOLDS a jewel while nothing connects it — the jewel in it is what allocates
    /// it, so it goes into the granted set too instead of taking the build's other nodes with it;</item>
    /// <item>an attribute choice belonging to no allocated node (or to a choice the pinned tree cannot
    /// offer) — it is dropped, because it has no effect anyway, and an allocated attribute node whose choice
    /// went missing falls back to the engine's own default, which the editor can change.</item>
    /// </list>
    /// What is neither traversable nor grantable is given up. The plan itself is returned when it was
    /// already valid, so a caller can tell "nothing happened" by reference; <paramref name="granted"/>,
    /// <paramref name="dropped"/> and <paramref name="choices"/> say what was moved, given up and defaulted,
    /// for the caller to report rather than change a build silently. A state that cannot be brought back at
    /// all — another data set, a class the pinned tree does not have — is returned untouched.
    /// </summary>
    public PassiveTreePlan Repair(PassiveTreePlan plan, out int granted, out int dropped, out int choices)
    {
        granted = dropped = choices = 0;
        try
        {
            if (IsValid(plan)) return plan;
            var free = plan.JewelAllocatedNodes.Intersect(plan.AllocatedNodes).ToHashSet();
            var gone = new HashSet<int>();
            foreach (int id in plan.AllocatedNodes)
            {
                if (free.Contains(id) || CanTraverse(id, plan)) continue;
                if (Catalog.Nodes.TryGetValue(id, out var node) && node.CanBeGranted) { free.Add(id); granted++; }
                else gone.Add(id);
            }
            var owed = plan with { JewelAllocatedNodes = [.. free.Order()] };
            foreach (int id in Disconnected(owed).Order())
            {
                if (Catalog.Nodes.TryGetValue(id, out var node) && node.IsJewel && plan.Jewels.ContainsKey(id) && !gone.Contains(id))
                { free.Add(id); granted++; continue; }
                gone.Add(id);
            }
            var selections = new Dictionary<int, int>();
            foreach (int id in plan.AllocatedNodes)
            {
                if (!Catalog.Nodes.TryGetValue(id, out var node) || !node.IsAttribute) continue;
                if (plan.AttributeSelections.TryGetValue(id, out int choice) && ValidAttribute(choice)) selections[id] = choice;
                else if (ValidAttribute(DefaultAttribute)) { selections[id] = DefaultAttribute; choices++; }
                else gone.Add(id);
            }
            var next = Prune(
                (plan with { JewelAllocatedNodes = [.. free.Order()] }) with { AttributeSelections = selections }, gone);
            // Weapon-set modes name allocated nodes; a leftover entry would make the plan fail its own
            // structure check before any rule is looked at.
            next = next with { WeaponSetNodes = next.WeaponSetNodes.Where(p => next.AllocatedNodes.Contains(p.Key)).ToDictionary() };
            Validate(next);
            dropped = plan.AllocatedNodes.Length - next.AllocatedNodes.Length;
            return next;
        }
        catch (Exception e) when (e is TreeRuleException or BuildFormatException)
        {
            granted = dropped = choices = 0;
            return plan;
        }
    }
    /// <summary>True when the plan passes every rule the engine has, without throwing at the caller.</summary>
    public bool IsValid(PassiveTreePlan plan)
    {
        try { Validate(plan); return true; }
        catch (Exception e) when (e is TreeRuleException or BuildFormatException) { return false; }
    }
    public PassiveTreePlan SetAttribute(PassiveTreePlan plan, int node, int choice)
    {
        Validate(plan);
        if (!plan.AllocatedNodes.Contains(node) || !Catalog.Nodes[node].IsAttribute || !ValidAttribute(choice)) throw new TreeRuleException("TreeInvalidAttribute");
        var choices = new Dictionary<int, int>(plan.AttributeSelections) { [node] = choice };
        return plan.Copy() with { AttributeSelections = choices };
    }
    private HashSet<int> Reachable(int[] starts, HashSet<int> allowed)
    {
        var result = new HashSet<int>(); var queue = new Queue<int>();
        foreach (int start in starts)
            if (allowed.Contains(start) && result.Add(start)) queue.Enqueue(start);
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
