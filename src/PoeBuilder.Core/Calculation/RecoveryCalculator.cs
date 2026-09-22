namespace PoeBuilder.Core.Calculation;

public sealed record RecoveryWindowResult(
    decimal LifeAfter,
    decimal EnergyShieldAfter,
    decimal RecoupApplied,
    decimal LeechApplied,
    decimal RegenerationApplied,
    decimal EsRechargeApplied);

public static class RecoveryCalculator
{
    /// <summary>Applies explicit post-hit recovery sources over a finite window. ES recharge
    /// respects its interruption delay; recoup, leech and regeneration are caller-supplied
    /// rates and are capped by their corresponding resource pools.</summary>
    public static RecoveryWindowResult AfterHit(
        decimal lifeMaximum,
        decimal lifeCurrent,
        decimal energyShieldMaximum,
        decimal energyShieldCurrent,
        decimal seconds,
        decimal recoupPerSecond = 0,
        decimal leechPerSecond = 0,
        decimal regenerationPerSecond = 0,
        decimal esRechargePerSecond = 0,
        decimal esRechargeDelaySeconds = DefenceCalculator.BaseEnergyShieldRechargeDelaySeconds)
    {
        decimal duration = Math.Max(0, seconds);
        decimal life = Math.Clamp(lifeCurrent, 0, Math.Max(0, lifeMaximum));
        decimal es = Math.Clamp(energyShieldCurrent, 0, Math.Max(0, energyShieldMaximum));
        decimal recoup = Math.Min(Math.Max(0, lifeMaximum) - life,
            Math.Max(0, recoupPerSecond) * duration);
        life += recoup;
        decimal leech = Math.Min(Math.Max(0, lifeMaximum) - life,
            Math.Max(0, leechPerSecond) * duration);
        life += leech;
        decimal regen = Math.Min(Math.Max(0, lifeMaximum) - life,
            Math.Max(0, regenerationPerSecond) * duration);
        life += regen;
        decimal rechargeDuration = Math.Max(0, duration - Math.Max(0, esRechargeDelaySeconds));
        decimal recharge = Math.Min(Math.Max(0, energyShieldMaximum) - es,
            Math.Max(0, esRechargePerSecond) * rechargeDuration);
        es += recharge;
        return new(life, es, recoup, leech, regen, recharge);
    }
}
