using PoeBuilder.Core.Models;

namespace PoeBuilder.Core.Calculation;

/// <summary>Game-facing boundary. No upstream PoB runtime or Lua is required.</summary>
public sealed record GameDataManifest(string GameVersion, string Language, string Source, string ContentHash);
public enum CalculationAvailability { NoGameData, NotImplemented, Available }
public sealed record CalculationResult(CalculationAvailability Availability, IReadOnlyDictionary<string, decimal> Metrics);

public interface ICalculationEngine
{
    CalculationResult Calculate(BuildDocument build, GameDataManifest? data);
}

/// <summary>Explicit unavailable result rather than invented DPS or zero-valued game statistics.</summary>
public sealed class UnavailableCalculationEngine : ICalculationEngine
{
    public CalculationResult Calculate(BuildDocument build, GameDataManifest? data) =>
        new(data is null ? CalculationAvailability.NoGameData : CalculationAvailability.NotImplemented,
            new Dictionary<string, decimal>());
}
