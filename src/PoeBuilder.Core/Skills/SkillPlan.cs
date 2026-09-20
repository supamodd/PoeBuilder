using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;

namespace PoeBuilder.Core.Skills;

public sealed record GemSelection
{
    public string GemId { get; init; } = "";
    public int Level { get; init; } = 1;
    public int Quality { get; init; }
    public void ValidateStructure()
    {
        if (string.IsNullOrWhiteSpace(GemId) || GemId.Length > 300 || Level is < 1 or > 40 || Quality is < 0 or > 20) throw new BuildFormatException("Invalid gem selection.");
    }
}
public sealed record SkillGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public int WeaponSet { get; init; } // 0 = both, 1/2 = one set; a plan label, not automatic compatibility.
    public GemSelection Active { get; init; } = new();
    public GemSelection[] Supports { get; init; } = [];
    public string Notes { get; init; } = "";
    public SkillGroup Copy() => this with { Active = Active with { }, Supports = Supports.Select(s => s with { }).ToArray() };
    public void ValidateStructure()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 120 || WeaponSet is < 0 or > 2 || Active is null || Supports is null || Supports.Length > 5 || Supports.Any(s => s is null) ||
            Notes is null || Notes.Length > 10000) throw new BuildFormatException("Invalid skill group.");
        Active.ValidateStructure(); foreach (var support in Supports) support.ValidateStructure();
        if (Supports.Select(s => s.GemId).Distinct().Count() != Supports.Length) throw new BuildFormatException("Duplicate support in one group.");
    }
}
public sealed record SkillPlan
{
    public string DatasetId { get; init; } = GameCatalog.Dataset;
    public SkillGroup[] Groups { get; init; } = [];
    public SkillPlan Copy() => this with { Groups = Groups.Select(g => g.Copy()).ToArray() };
    public void ValidateStructure()
    {
        if (string.IsNullOrWhiteSpace(DatasetId) || DatasetId.Length > 160 || Groups is null || Groups.Length > 40 || Groups.Any(g => g is null)) throw new BuildFormatException("Invalid skill plan.");
        foreach (var group in Groups) group.ValidateStructure();
        if (Groups.Select(g => g.Id).Distinct().Count() != Groups.Length) throw new BuildFormatException("Duplicate skill group identifier.");
    }
}
public static class SkillRules
{
    public static void ValidateGroup(GameCatalog catalog, SkillGroup group)
    {
        group.ValidateStructure();
        Check(group.Active, false); foreach (var support in group.Supports) Check(support, true);
        void Check(GemSelection selected, bool support)
        {
            if (!catalog.Gems.TryGetValue(selected.GemId, out var gem) || (gem.Kind == "support") != support) throw new PlanningException("PlanGemKind");
            if (!gem.Levels.Contains(selected.Level)) throw new PlanningException("PlanGemLevel");
        }
        // Recommended supports are a discoverability aid, NOT proof of compatibility.
    }
    public static void Validate(GameCatalog catalog, SkillPlan plan)
    {
        plan.ValidateStructure(); if (plan.DatasetId != GameCatalog.Dataset) throw new PlanningException("PlanDataset");
        foreach (var group in plan.Groups) ValidateGroup(catalog, group);
    }
    public static SkillPlan Put(GameCatalog catalog, SkillPlan plan, SkillGroup group)
    {
        ValidateGroup(catalog, group);
        var next = plan.Copy() with { Groups = plan.Groups.Where(g => g.Id != group.Id).Select(g => g.Copy()).Append(group.Copy()).ToArray() };
        Validate(catalog, next); return next;
    }
}
