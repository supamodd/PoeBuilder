using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Skills;
using PoeBuilder.Core.Tree;

namespace PoeBuilder.Core.Models;

/// <summary>Independent native format. Not a PoB XML/share-code document.</summary>
public sealed record BuildDocument
{
    public const string FormatName = "PoeBuilder.Native.Build";
    public string Format { get; init; } = FormatName;
    public int SchemaVersion { get; init; } = 4;
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string CharacterClass { get; init; } = "";
    public int Level { get; init; } = 1;
    public string GameVersion { get; init; } = "0.5.5c";
    /// <summary>"starter" = campaign, resistances start at 0; "endgame" = after the campaign, elemental resists start at -40.</summary>
    public string ProgressStage { get; init; } = "starter";
    public string Notes { get; init; } = "";
    public EquipmentPlan? Equipment { get; init; }
    public SkillPlan? Skills { get; init; }
    public PassiveTreePlan? Tree { get; init; }
    /// <summary>Optional resolved resource reservation totals. This is not an active-skill
    /// graph; it is persisted only when an importer or caller has explicitly resolved sources.</summary>
    public ResourceReservationPlan? Reservation { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }

    public static BuildDocument Create(string name, string gameVersion = "0.5.5c") => new()
    {
        Id = Guid.NewGuid(), Name = name, GameVersion = gameVersion,
        CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow
    };
}

/// <summary>Resolved reservation totals that can be persisted without pretending that the
/// native skill model contains PoB's active reservation graph.</summary>
public sealed record ResourceReservationPlan
{
    public decimal LifeReservedFlat { get; init; }
    public decimal LifeReservedPercent { get; init; }
    public decimal ManaReservedFlat { get; init; }
    public decimal ManaReservedPercent { get; init; }
    public decimal SpiritReservedFlat { get; init; }
    public decimal SpiritReservedPercent { get; init; }

    public void ValidateStructure()
    {
        if (LifeReservedFlat < 0 || LifeReservedPercent < 0 ||
            ManaReservedFlat < 0 || ManaReservedPercent < 0 ||
            SpiritReservedFlat < 0 || SpiritReservedPercent < 0)
            throw new BuildFormatException("Resource reservation values cannot be negative.");
    }
}

public sealed class BuildFormatException(string message) : Exception(message);

public static class BuildValidation
{
    public static void Validate(BuildDocument build)
    {
        if (build.Format != BuildDocument.FormatName || build.SchemaVersion != 4)
            throw new BuildFormatException("Unsupported native build format or schema version.");
        if (build.ProgressStage is not ("starter" or "endgame")) throw new BuildFormatException("Invalid progress stage.");
        build.Tree?.ValidateStructure(); build.Equipment?.ValidateStructure(); build.Skills?.ValidateStructure(); build.Reservation?.ValidateStructure();
        if (build.Id == Guid.Empty) throw new BuildFormatException("Build identifier is missing.");
        if (string.IsNullOrWhiteSpace(build.Name) || build.Name.Length > 80)
            throw new BuildFormatException("Build name must contain 1–80 characters.");
        if (build.Level is < 1 or > 100) throw new BuildFormatException("Level must be between 1 and 100.");
        if (build.CharacterClass is null || build.CharacterClass.Length > 80)
            throw new BuildFormatException("Class label is invalid.");
        if (build.GameVersion is null || build.GameVersion.Length > 32)
            throw new BuildFormatException("Game version label is invalid.");
        if (build.Notes is null || build.Notes.Length > 100_000)
            throw new BuildFormatException("Notes are too long (maximum 100,000 characters).");
        if (build.CreatedUtc == default || build.UpdatedUtc == default)
            throw new BuildFormatException("Creation/update timestamp is missing.");
    }
}
