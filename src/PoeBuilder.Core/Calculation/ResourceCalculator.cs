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
