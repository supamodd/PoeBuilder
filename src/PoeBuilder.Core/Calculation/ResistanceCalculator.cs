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

/// <summary>Resistance stages for one damage hit. Reduction is applied before penetration;
/// neither modifier changes the character's displayed resistance.</summary>
public sealed record ResistanceHitResult(
    decimal DisplayedResistance,
    decimal AfterReduction,
    decimal AfterPenetration,
    decimal DamageMultiplier);

/// <summary>How PoB2's own panel shows a resistance: the capped value with the overcapped part printed
/// beside it ("75% (+14%)"), the uncapped total (stage baseline + raw sources), the cap and the colour tone
/// the sheet uses for the row.</summary>
public sealed record ResistanceDisplay(string Value, decimal Total, decimal OverCap, decimal Maximum, string Tone);

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

    /// <summary>Formats a resistance row the way PoB2's panel prints it: the effective value plus the
    /// overcapped part ("75% (+14%)"), with the uncapped total kept for the row detail. A build whose gear
    /// overshoots the cap must never look like a resistance of 149%.</summary>
    public static ResistanceDisplay Display(decimal effective, decimal sources, decimal penalty, decimal maximum)
    {
        decimal total = sources + penalty;
        decimal over = Math.Max(0, total - maximum);
        string tone = effective < 0 ? "danger" : effective >= maximum ? "good" : effective < 30 ? "warn" : "none";
        string value = F(effective) + "%" + (over > 0 ? " (+" + F(over) + "%)" : "");
        return new(value, total, over, maximum, tone);

        static string F(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>Applies hit-time resistance reduction and penetration in game order. Positive
    /// reduction lowers resistance before positive penetration lowers it further.</summary>
    public static ResistanceHitResult ForHit(
        ResistanceResult resistance,
        decimal reduction = 0,
        decimal penetration = 0)
    {
        decimal afterReduction = resistance.Effective - reduction;
        decimal afterPenetration = afterReduction - penetration;
        decimal damageMultiplier = Math.Max(0, 1m - afterPenetration / 100m);
        return new(resistance.Effective, afterReduction, afterPenetration, damageMultiplier);
    }
}
