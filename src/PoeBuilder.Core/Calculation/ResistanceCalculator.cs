namespace PoeBuilder.Core.Calculation;

/// <summary>
/// Calculates a player's displayed resistance from the configured stage, raw resistance
/// sources and maximum-resistance modifiers.
///
/// This intentionally does not apply enemy resistance, penetration, exposure or reduction:
/// those belong to the offence/target pipeline and are not player resistance.
/// </summary>
public sealed record ResistanceResult(
    decimal Baseline,
    decimal Sources,
    decimal Maximum,
    decimal Effective);

public static class ResistanceCalculator
{
    /// <summary>
    /// Adds the stage baseline to all raw player sources and applies the upper resistance cap.
    /// Negative resistance is preserved; no undocumented lower clamp is introduced.
    /// </summary>
    public static ResistanceResult Calculate(
        decimal baseline,
        decimal sources,
        decimal maximumResistance,
        decimal resistanceCap = 75m)
    {
        decimal maximum = resistanceCap + maximumResistance;
        decimal effective = Math.Min(baseline + sources, maximum);
        return new ResistanceResult(baseline, sources, maximum, effective);
    }
}
