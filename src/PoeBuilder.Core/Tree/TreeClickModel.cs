namespace PoeBuilder.Core.Tree;

/// <summary>What a click on a tree node has to do. PoB2's own model, which is also the game's, is a
/// two-state toggle: a left click allocates an unallocated node together with the route that reaches it
/// and refunds an allocated one (<c>Classes/PassiveTreeView.lua:411-430</c> — <c>spec:AllocNode</c> /
/// <c>spec:DeallocNode</c>).</summary>
public enum TreeClickAction
{
    /// <summary>Nothing to allocate or refund (an id outside the tree, or a node the plan does not own).</summary>
    Ignore,
    Allocate,
    Refund
}

public static class TreeClickModel
{
    /// <summary>Resolves the click on one node against the current plan:
    /// the class start is never a target and never refundable (it is implicit and costs no points);
    /// a node a jewel pays for stays allocated — the jewel, not the plan, owns it;
    /// every other allocated node is refundable and every other node is allocatable.</summary>
    public static TreeClickAction Resolve(PassiveTreePlan plan, TreeCatalog? catalog, int nodeId)
    {
        if (catalog is null || !catalog.Nodes.TryGetValue(nodeId, out var node)) return TreeClickAction.Ignore;
        if (node.IsStart) return TreeClickAction.Ignore;
        if (!plan.AllocatedNodes.Contains(nodeId)) return TreeClickAction.Allocate;
        return plan.JewelAllocatedNodes.Contains(nodeId) ? TreeClickAction.Ignore : TreeClickAction.Refund;
    }
}
