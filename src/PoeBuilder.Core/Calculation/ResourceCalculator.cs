namespace PoeBuilder.Core.Calculation;

/// <summary>Resolved reservation context supplied by a caller or a persisted
/// <see cref="PoeBuilder.Core.Models.ResourceReservationPlan"/>. It contains totals, not the
/// active-skill graph; the calculator must not infer reservations from incomplete gem links.</summary>
public sealed record ResourceReservationContext(
    decimal LifeReservedFlat = 0m,
    decimal LifeReservedPercent = 0m,
    decimal ManaReservedFlat = 0m,
    decimal ManaReservedPercent = 0m,
    decimal SpiritReservedFlat = 0m,
    decimal SpiritReservedPercent = 0m);

/// <summary>An explicit ES damage event. A zero ES-damage event still resets the recharge timer,
/// because the helper models interruption by a damage event rather than only ES loss.</summary>
public sealed record EnergyShieldDamageEvent(decimal TimeSeconds, decimal EnergyShieldDamage);

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

    /// <summary>Evaluates an explicit, chronological ES damage sequence. The sequence starts
    /// with a damage event at t=0; each later event interrupts recharge, applies its ES damage,
    /// and the final window runs until <paramref name="endTimeSeconds"/>. This deliberately
    /// excludes recovery modifiers, recoup, leech, reservation and other combat state.</summary>
    public static decimal? EnergyShieldAfterDamageSequence(decimal maximumEnergyShield,
        decimal currentEnergyShield, IReadOnlyList<EnergyShieldDamageEvent>? events,
        decimal endTimeSeconds, decimal rechargePerSecond, decimal rechargeDelaySeconds)
    {
        if (events is null || events.Count == 0 || maximumEnergyShield < 0 || currentEnergyShield < 0 ||
            endTimeSeconds < 0 || rechargePerSecond < 0 || rechargeDelaySeconds < 0 ||
            events[0].TimeSeconds != 0)
            return null;

        decimal current = Math.Clamp(currentEnergyShield, 0, maximumEnergyShield);
        decimal previousTime = 0;
        foreach (var damageEvent in events)
        {
            if (damageEvent.TimeSeconds < previousTime || damageEvent.TimeSeconds < 0 || damageEvent.EnergyShieldDamage < 0 ||
                damageEvent.TimeSeconds > endTimeSeconds)
                return null;

            decimal? recovered = DefenceCalculator.EnergyShieldAfterRechargeWindow(
                maximumEnergyShield, current, damageEvent.TimeSeconds - previousTime,
                rechargePerSecond, rechargeDelaySeconds);
            if (recovered is not decimal value)
                return null;
            current = Math.Max(0, value - damageEvent.EnergyShieldDamage);
            previousTime = damageEvent.TimeSeconds;
        }

        return DefenceCalculator.EnergyShieldAfterRechargeWindow(
            maximumEnergyShield, current, endTimeSeconds - previousTime,
            rechargePerSecond, rechargeDelaySeconds);
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
