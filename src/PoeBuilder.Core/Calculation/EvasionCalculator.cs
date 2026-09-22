namespace PoeBuilder.Core.Calculation;

public sealed record EvasionRollResult(bool Hit, decimal EntropyBefore, decimal EntropyAfter, decimal HitChancePercent);

public static class EvasionCalculator
{
    /// <summary>Resolves one attack using PoE's entropy model. Entropy is in [0,100); a hit
    /// occurs when accumulated hit chance reaches 100, then 100 is removed.</summary>
    public static EvasionRollResult ResolveAttack(decimal entropy, decimal hitChancePercent)
    {
        decimal before = Math.Clamp(entropy, 0, 99.999999m);
        decimal chance = Math.Clamp(hitChancePercent, 0, 100);
        decimal accumulated = before + chance;
        bool hit = accumulated >= 100;
        decimal after = hit ? accumulated - 100 : accumulated;
        return new(hit, before, after, chance);
    }

    /// <summary>Returns the mean result of two independent rolls used by lucky/unlucky
    /// mechanics. The input and output are percentages.</summary>
    public static decimal ApplyLuck(decimal chancePercent, bool lucky = false, bool unlucky = false)
    {
        decimal chance = Math.Clamp(chancePercent, 0, 100) / 100m;
        if (lucky == unlucky) return chance * 100m;
        decimal result = lucky ? 1 - (1 - chance) * (1 - chance) : chance * chance;
        return result * 100m;
    }
}
