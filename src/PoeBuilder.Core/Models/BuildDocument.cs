using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Skills;
using PoeBuilder.Core.Tree;

namespace PoeBuilder.Core.Models;

/// <summary>Independent native format. Not a PoB XML/share-code document.</summary>
public sealed record BuildDocument
{
    public const string FormatName = "PoeBuilder.Native.Build";
    public string Format { get; init; } = FormatName;
    public int SchemaVersion { get; init; } = 6;
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public string CharacterClass { get; init; } = "";
    public int Level { get; init; } = 1;
    public string GameVersion { get; init; } = "0.5.5c";
    /// <summary>"starter" = campaign, resistances start at 0; "endgame" = after the campaign, elemental resists start at -40.</summary>
    public string ProgressStage { get; init; } = "starter";
    public string Notes { get; init; } = "";
    public string? NotesRtf { get; init; }
    public EquipmentPlan? Equipment { get; init; }
    public SkillPlan? Skills { get; init; }
    public PassiveTreePlan? Tree { get; init; }
    /// <summary>Optional resolved resource reservation totals. This is not an active-skill
    /// graph; it is persisted only when an importer or caller has explicitly resolved sources.</summary>
    public ResourceReservationPlan? Reservation { get; init; }
    /// <summary>Optional Life-state condition. PoB2 derives Low Life from the unreserved Life
    /// percentage (data.misc.LowPoolThreshold = 35%) and Full Life from nothing being reserved; our
    /// own reservation model cannot resolve skill reservations yet, so an importer that finds the
    /// value already resolved in the source (PoB2 writes LifeUnreservedPercent into its share code)
    /// stores it here. Null means "derive from our own reservation plan".</summary>
    public bool? LowLife { get; init; }
    /// <summary>PoB2 config conditions resolved from an imported build's &lt;Config&gt; inputs.
    /// PoB2 saves only the inputs the build changed, so a false flag means "at its default". The
    /// importer maps PoB2's own variable names (conditionMoving, conditionCritRecently, …) onto these
    /// typed flags, so every value traces back to the source and nothing is guessed.</summary>
    public BuildConditions Conditions { get; init; } = new();
    /// <summary>Optional explicit incoming spell scenario. It is persisted separately from the
    /// default-monster estimate because the pinned catalog has no spell-hit scenario.</summary>
    public DefenceScenarioPlan? Defence { get; init; }
    /// <summary>Optional quest-reward lines resolved from an imported build's config (PoB2's
    /// "Quest Rewards" section, PathOfBuilding-PoE2-master src/Data/QuestRewards.lua). Each entry is
    /// one reward line exactly as PoB2 stores it; the calculator parses them with
    /// <see cref="Calculation.QuestRewardParser"/>. Null/empty means "no quest rewards resolved",
    /// which is also what a build created by hand has.</summary>
    public string[]? QuestRewards { get; init; }
    public List<BuildProgressionStage> Stages { get; init; } = [];
    public Guid ActiveStageId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }

    public static BuildDocument Create(string name, string gameVersion = "0.5.5c") => new()
    {
        Id = Guid.NewGuid(), Name = name, GameVersion = gameVersion,
        CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow
    };
}

/// <summary>A self-contained progression snapshot. Later stages can evolve without mutating earlier ones.</summary>
public sealed record BuildProgressionStage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public bool IsInitialized { get; init; }
    public string CharacterClass { get; init; } = "";
    public int Level { get; init; } = 1;
    public string ProgressStage { get; init; } = "starter";
    public EquipmentPlan? Equipment { get; init; }
    public SkillPlan? Skills { get; init; }
    public PassiveTreePlan? Tree { get; init; }
    public ResourceReservationPlan? Reservation { get; init; }
    public bool? LowLife { get; init; }
    public BuildConditions Conditions { get; init; } = new();
    public DefenceScenarioPlan? Defence { get; init; }
    public string[]? QuestRewards { get; init; }

    public static BuildProgressionStage FromBuild(BuildDocument build, string name) => new()
    {
        Name = name, CharacterClass = build.CharacterClass, Level = build.Level, ProgressStage = build.ProgressStage,
        Equipment = build.Equipment?.Copy(), Skills = build.Skills?.Copy(), Tree = build.Tree?.Copy(),
        Reservation = build.Reservation, LowLife = build.LowLife, Conditions = build.Conditions,
        Defence = build.Defence, QuestRewards = build.QuestRewards is null ? null : [.. build.QuestRewards]
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

/// <summary>Typed PoB2 config conditions (see <see cref="BuildDocument.Conditions"/>).</summary>
public sealed record BuildConditions
{
    // Player-state conditions.
    public bool Moving { get; init; }
    public bool CritRecently { get; init; }
    public bool BeenHitRecently { get; init; }
    /// <summary>PoB2's <c>conditionSurrounded</c> — "at least 5 Enemies within 3 metres" (ConfigOptions.lua:1114).</summary>
    public bool Surrounded { get; init; }
    /// <summary>PoB2's <c>conditionStunnedRecently</c> (ConfigOptions.lua:1259).</summary>
    public bool StunnedRecently { get; init; }
    /// <summary>PoB2's <c>conditionAtCloseRange</c> (ConfigOptions.lua:1645).</summary>
    public bool AtCloseRange { get; init; }
    /// <summary>PoB2's <c>enemyDistance</c> in units (10 units = 1 metre). When a build does not set it, PoB2 uses
    /// the option's own placeholder — <c>defaultPlaceholderState = 20</c>, two metres (ConfigOptions.lua:1621,
    /// ConfigTab.lua:712-715) — which is exactly the distance its "against enemies within 2m" family needs.</summary>
    public decimal EnemyDistance { get; init; } = 20m;
    // Enemy-state conditions (they drive enemy-side mechanics, e.g. exposure and ailments).
    public bool EnemyChilled { get; init; }
    public bool EnemyIgnited { get; init; }
    public bool EnemyBleeding { get; init; }
    public bool EnemyShocked { get; init; }
    public bool EnemyFireExposure { get; init; }
    public bool EnemyColdExposure { get; init; }
    public bool EnemyLightningExposure { get; init; }
    // Skill mechanics switched on by the config.
    /// <summary>PoB2's <c>flameWallAddedDamage</c> ("Projectile Travelled through?") and <c>flameWallInfused</c>
    /// ("Lightning Infused?") checkboxes of the Flame Wall config section (ConfigOptions.lua:379-383).</summary>
    public bool FlameWallAddedDamage { get; init; }
    public bool FlameWallInfused { get; init; }
    /// <summary>The charge counts the build actually has. PoB2 resolves them from its config
    /// (<c>useFrenzyCharges</c>/<c>usePowerCharges</c>/<c>useEnduranceCharges</c> → the maximum) and exports
    /// the result as <c>&lt;PlayerStat stat="FrenzyCharges" value="3"/&gt;</c>, which is the source read here;
    /// they drive PoB2's <c>StatThreshold</c> and <c>Multiplier</c> tags ("with Frenzy Charges", "per charge").</summary>
    public int FrenzyCharges { get; init; }
    public int PowerCharges { get; init; }
    public int EnduranceCharges { get; init; }
    public int TotalCharges => FrenzyCharges + PowerCharges + EnduranceCharges;
    /// <summary>The Rage the build runs at (PoB2's "Rage:" count input, <c>multiplierRage</c>). PoB2 only shows
    /// that input while the build can gain Rage, so a non-zero value proves rage is live — which is exactly the
    /// gate its own resolver uses (<c>CalcPerform.lua:777</c>: the <c>CanGainRage</c> flag or a positive rage
    /// regeneration).</summary>
    public int RageStacks { get; init; }
    /// <summary>"Elemental Conflux Element" list (ConfigOptions.lua:389): 1 = Average (the default), 2 =
    /// Lightning, 3 = Cold, 4 = Fire. The value only scales how the conflux's "N% more damage" is divided
    /// between the three elements.</summary>
    public int ConfluxElement { get; init; } = 1;
    /// <summary>Trinity's "Total Resonance Count" (ConfigOptions.lua:673), clamped to 0..300 by PoB2 itself.</summary>
    public int ResonanceCount { get; init; }
    public bool ArcLightningInfused { get; init; }
    /// <summary>Enemy values for PoB2's "effective" mode (its Calcs panel and its exported TotalDPS price
    /// the damage the enemy actually takes). Null means PoB2's own default: 50% elemental resistance,
    /// 0% chaos, the level's monster armour (see the calculator).</summary>
    public decimal? EnemyFireResist { get; init; }
    public decimal? EnemyColdResist { get; init; }
    public decimal? EnemyLightningResist { get; init; }
    public decimal? EnemyChaosResist { get; init; }
    public decimal? EnemyArmour { get; init; }
    public decimal? EnemyLevel { get; init; }
    public decimal? EnemyPhysicalDamageReduction { get; init; }
}

/// <summary>Explicit player-facing spell-hit scenario inputs. The calculator derives the
/// successful-hit mitigation and player defensive sources; the caller supplies the missing
/// enemy spell hit size and any non-default hit/block assumptions.</summary>
public sealed record DefenceScenarioPlan
{
    public decimal? SpellRawHit { get; init; }
    public string SpellDamageType { get; init; } = "Fire";
    public decimal SpellHitChancePercent { get; init; } = 100m;
    public decimal SpellBlockedHitDamagePercent { get; init; }
    public decimal SpellResistanceReductionPercent { get; init; }
    public decimal SpellResistancePenetrationPercent { get; init; }

    public void ValidateStructure()
    {
        if (SpellRawHit is < 0 || SpellHitChancePercent is < 0 or > 100 ||
            SpellBlockedHitDamagePercent is < 0 or > 100 ||
            SpellResistanceReductionPercent < 0 || SpellResistancePenetrationPercent < 0 ||
            SpellDamageType is not ("Physical" or "Fire" or "Cold" or "Lightning" or "Chaos"))
            throw new BuildFormatException("Invalid defence scenario plan.");
    }
}

public sealed class BuildFormatException(string message) : Exception(message);

public static class BuildValidation
{
    public static void Validate(BuildDocument build)
    {
        if (build.Format != BuildDocument.FormatName || build.SchemaVersion != 6)
            throw new BuildFormatException("Unsupported native build format or schema version.");
        if (build.ProgressStage is not ("starter" or "endgame")) throw new BuildFormatException("Invalid progress stage.");
        build.Tree?.ValidateStructure(); build.Equipment?.ValidateStructure(); build.Skills?.ValidateStructure(); build.Reservation?.ValidateStructure(); build.Defence?.ValidateStructure();
        if (build.Stages is null || build.Stages.Count > 32 ||
            build.Stages.Any(s => s is null || s.Id == Guid.Empty || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 40 ||
                s.Level is < 1 or > 100 || s.ProgressStage is not ("starter" or "endgame") || s.CharacterClass is null || s.CharacterClass.Length > 80 ||
                s.QuestRewards is { Length: > 64 } || s.QuestRewards?.Any(q => q is null || q.Length > 300) == true))
            throw new BuildFormatException("Invalid progression stages.");
        if (build.Stages.Select(s => s.Id).Distinct().Count() != build.Stages.Count ||
            (build.Stages.Count > 0 && !build.Stages.Any(s => s.Id == build.ActiveStageId)))
            throw new BuildFormatException("Progression stage identifiers are invalid.");
        foreach (var stage in build.Stages)
        {
            stage.Tree?.ValidateStructure(); stage.Equipment?.ValidateStructure(); stage.Skills?.ValidateStructure();
            stage.Reservation?.ValidateStructure(); stage.Defence?.ValidateStructure();
        }
        if (build.QuestRewards is { Length: > 64 } ||
            build.QuestRewards?.Any(q => q is null || q.Length > 300) == true)
            throw new BuildFormatException("Invalid quest reward list.");
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
        if (build.NotesRtf is { Length: > 1_000_000 })
            throw new BuildFormatException("Formatted notes are too large.");
        if (build.CreatedUtc == default || build.UpdatedUtc == default)
            throw new BuildFormatException("Creation/update timestamp is missing.");
    }
}
