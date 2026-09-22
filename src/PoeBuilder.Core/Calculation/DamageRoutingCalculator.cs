namespace PoeBuilder.Core.Calculation;

/// <summary>Damage amounts after hit-time type routing, before mitigation.</summary>
public sealed record DamagePacket(decimal Physical, decimal Fire, decimal Cold, decimal Lightning, decimal Chaos)
{
    public decimal Total => Physical + Fire + Cold + Lightning + Chaos;

    public decimal Get(string type) => type switch
    {
        "physical" => Physical,
        "fire" => Fire,
        "cold" => Cold,
        "lightning" => Lightning,
        "chaos" => Chaos,
        _ => 0
    };

    public DamagePacket Add(string type, decimal amount) => type switch
    {
        "physical" => this with { Physical = Physical + amount },
        "fire" => this with { Fire = Fire + amount },
        "cold" => this with { Cold = Cold + amount },
        "lightning" => this with { Lightning = Lightning + amount },
        "chaos" => this with { Chaos = Chaos + amount },
        _ => this
    };
}

public static class DamageRoutingCalculator
{
    private static readonly string[] Types = ["physical", "fire", "cold", "lightning", "chaos"];

    /// <summary>Moves each source type through its taken-as routes. Route percentages are
    /// evaluated from the current amount and capped at 100% per source; generated damage can
    /// continue through a later route, while cycles terminate at the repeated type.</summary>
    public static DamagePacket ApplyTakenAs(
        DamagePacket damage,
        IReadOnlyDictionary<(string Source, string Destination), decimal> routes)
    {
        var result = new DamagePacket(0, 0, 0, 0, 0);
        foreach (string source in Types)
            result = Add(result, Route(source, Math.Max(0, damage.Get(source)), [source]));
        return result;

        DamagePacket Route(string source, decimal amount, HashSet<string> path)
        {
            var routed = new DamagePacket(0, 0, 0, 0, 0);
            decimal remaining = amount;
            foreach (var route in routes.Where(x => x.Key.Source == source)
                         .OrderBy(x => x.Key.Destination, StringComparer.Ordinal))
            {
                decimal transfer = Math.Min(remaining, Math.Max(0, amount * route.Value / 100m));
                if (transfer <= 0) continue;
                var nextPath = new HashSet<string>(path, StringComparer.Ordinal) { route.Key.Destination };
                routed = Add(routed, path.Contains(route.Key.Destination)
                    ? new DamagePacket(0, 0, 0, 0, 0).Add(route.Key.Destination, transfer)
                    : Route(route.Key.Destination, transfer, nextPath));
                remaining -= transfer;
            }
            return routed.Add(source, remaining);
        }

        static DamagePacket Add(DamagePacket left, DamagePacket right) => new(
            left.Physical + right.Physical, left.Fire + right.Fire, left.Cold + right.Cold,
            left.Lightning + right.Lightning, left.Chaos + right.Chaos);
    }
}
