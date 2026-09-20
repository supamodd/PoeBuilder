using PoeBuilder.Core.Models;

namespace PoeBuilder.Core.Tree;

public sealed record AscendancyPlan
{
    public string Id { get; init; } = "";
    public int[] AllocatedNodes { get; init; } = [];
    // Manual planning limit, not inferred from completed trials. Kept separate from the main pool.
    public int PointLimit { get; init; } = 8;
    public AscendancyPlan Copy() => this with { AllocatedNodes = [.. AllocatedNodes] };
    public void ValidateStructure()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 80 || PointLimit is < 0 or > 10000 || AllocatedNodes is null ||
            AllocatedNodes.Length > 1000 || AllocatedNodes.Any(id => id is < 0 or > 65535) || AllocatedNodes.Distinct().Count() != AllocatedNodes.Length)
            throw new BuildFormatException("Invalid ascendancy plan structure.");
    }
}
public sealed record AscendancyDefinition(string Id, string Name, int ClassIndex, string SourceGraphId, TreeCatalog Graph)
{
    public override string ToString() => Name;
    public PassiveTreePlan ToGraphPlan(AscendancyPlan plan) => new()
    {
        DatasetId = Graph.DatasetId, ClassIndex = ClassIndex, PointLimit = plan.PointLimit, AllocatedNodes = [.. plan.AllocatedNodes]
    };
}
public static class AscendancyRules
{
    public static AscendancyDefinition Definition(TreeCatalog catalog, PassiveTreePlan plan) =>
        catalog.Ascendancies.FirstOrDefault(a => a.Id == plan.Ascendancy?.Id && a.ClassIndex == plan.ClassIndex) ?? throw new TreeRuleException("TreeInvalidAscendancy");
    public static void Validate(TreeCatalog catalog, PassiveTreePlan plan)
    {
        if (plan.Ascendancy is null) return;
        var definition = Definition(catalog, plan);
        new PassiveTreeEngine(definition.Graph).Validate(definition.ToGraphPlan(plan.Ascendancy));
    }
    public static PassiveTreePlan Select(TreeCatalog catalog, PassiveTreePlan plan, string? id)
    {
        if (plan.DatasetId != catalog.DatasetId) throw new TreeRuleException("TreeDatasetMismatch");
        if (id is not null && !catalog.Ascendancies.Any(a => a.Id == id && a.ClassIndex == plan.ClassIndex)) throw new TreeRuleException("TreeInvalidAscendancy");
        // Explicit replacement: preserve main allocations and reset only ascendancy nodes.
        return plan.Copy() with { Ascendancy = id is null ? null : new() { Id = id } };
    }
    public static PassiveTreePlan Update(TreeCatalog catalog, PassiveTreePlan plan, PassiveTreePlan graphPlan)
    {
        var definition = Definition(catalog, plan);
        new PassiveTreeEngine(definition.Graph).Validate(graphPlan);
        if (graphPlan.Ascendancy is not null) throw new TreeRuleException("TreeInvalidAscendancy");
        return plan.Copy() with { Ascendancy = new() { Id = definition.Id, PointLimit = graphPlan.PointLimit, AllocatedNodes = [.. graphPlan.AllocatedNodes] } };
    }
}
