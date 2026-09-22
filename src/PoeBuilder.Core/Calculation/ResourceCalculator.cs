namespace PoeBuilder.Core.Calculation;

/// <summary>Resolved reservation context supplied by a caller that has enumerated active
/// reserving skills. BuildDocument does not currently preserve that skill-state graph, so the
/// calculator must not infer reservations from incomplete gem links.</summary>
public sealed record ResourceReservationContext(
    decimal LifeReservedFlat = 0m,
    decimal LifeReservedPercent = 0m,
    decimal ManaReservedFlat = 0m,
    decimal ManaReservedPercent = 0m,
    decimal SpiritReservedFlat = 0m,
    decimal SpiritReservedPercent = 0m);

/// <summary>Bounded continuous resource recovery helpers.</summary>
public static class ResourceRecovery
{
    /// <summary>Converts a life-regeneration rate expressed per minute to a bounded positive
    /// per-second rate. This is continuous regeneration only; it does not model recovery sources,
    /// leech, recoup, recovery locks or damage-event state.</summary>
    public static decimal LifeRegenerationPerSecond(decimal basePerMinute, decimal increasedPercent = 0m)
        => basePerMinute <= 0 ? 0m : Math.Max(0m, basePerMinute / 60m * (1m + increasedPercent / 100m));

    /// <summary>Applies continuous recovery over a finite window and caps at the maximum pool.
    /// Invalid timing or pool inputs return null rather than inventing a combat result.</summary>
    public static decimal? AfterRecoveryWindow(decimal maximum, decimal current,
        decimal seconds, decimal recoveryPerSecond)
    {
        if (maximum < 0 || current < 0 || seconds < 0)
            return null;
        decimal boundedCurrent = Math.Clamp(current, 0m, maximum);
        if (maximum == 0 || recoveryPerSecond <= 0)
            return boundedCurrent;
        return Math.Min(maximum, boundedCurrent + seconds * recoveryPerSecond);
    }
}

/// <summary>One resource's reservation result. Flat reservation is applied first, followed by
/// the percentage reservation rounded up to a whole resource unit, matching the current PoB2
/// reservation contract. The result is bounded by the resource maximum.</summary>
public sealed record ResourceReservation(
    decimal Maximum,
    decimal Reserved,
    decimal Unreserved,
    decimal ReservedPercent)
{
    public static ResourceReservation? Calculate(decimal maximum, decimal reservedFlat = 0m,
        decimal reservedPercent = 0m)
    {
        if (maximum < 0 || reservedFlat < 0 || reservedPercent < 0)
            return null;

        decimal reserved = Math.Min(maximum,
            reservedFlat + Math.Ceiling(maximum * reservedPercent / 100m));
        decimal percent = maximum > 0 ? reserved / maximum * 100m : 0m;
        return new(maximum, reserved, maximum - reserved, percent);
    }
}
