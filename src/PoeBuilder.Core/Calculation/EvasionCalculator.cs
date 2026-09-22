namespace PoeBuilder.Core.Calculation;

public sealed record EvasionRollResult(bool Hit, decimal EntropyBefore, decimal EntropyAfter, decimal HitChancePercent);
public sealed record EvasionSequenceResult(int Attacks, int Hits, decimal FinalEntropy, decimal HitRatePercent);

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

    public static EvasionSequenceResult ResolveSequence(decimal initialEntropy, decimal hitChancePercent, int attacks)
    {
        int count = Math.Max(0, attacks);
        decimal entropy = Math.Clamp(initialEntropy, 0, 99.999999m);
        int hits = 0;
        for (int i = 0; i < count; i++)
        {
            var result = ResolveAttack(entropy, hitChancePercent);
            if (result.Hit) hits++;
            entropy = result.EntropyAfter;
        }
        return new(count, hits, entropy, count == 0 ? 0 : hits * 100m / count);
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
