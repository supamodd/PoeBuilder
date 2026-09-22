namespace PoeBuilder.Core.Calculation;

public static class BlockCalculator
{
    /// <summary>Applies lucky/unlucky to a final block chance and then clamps it to the
    /// supplied maximum. This is a probability helper; block recovery is separate.</summary>
    public static decimal Chance(decimal chancePercent, decimal maximumPercent = DefenceCalculator.BlockChanceCap,
        bool lucky = false, bool unlucky = false)
        => Math.Clamp(EvasionCalculator.ApplyLuck(chancePercent, lucky, unlucky), 0, maximumPercent);

    /// <summary>Returns whether a block recovery animation has completed.</summary>
    public static bool RecoveryReady(decimal secondsSinceBlock, decimal recoverySeconds)
        => secondsSinceBlock >= 0 && recoverySeconds >= 0 && secondsSinceBlock >= recoverySeconds;

    /// <summary>Returns damage received by a blocked hit. A normal block prevents all hit
    /// damage; explicit partial-block effects can supply a percentage.</summary>
    public static decimal BlockedDamage(decimal incomingDamage, decimal blockedDamagePercent = 0)
        => Math.Max(0, incomingDamage) * Math.Clamp(blockedDamagePercent, 0, 100) / 100m;
}
