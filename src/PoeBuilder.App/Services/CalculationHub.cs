using PoeBuilder.Core.Calculation;

namespace PoeBuilder.App.Services;

/// <summary>Shares the latest character calculation between pages without coupling view models.</summary>
public static class CalculationHub
{
    public static CharacterSummary? Latest { get; private set; }
    public static event Action? Changed;
    public static void Publish(CharacterSummary summary)
    {
        Latest = summary;
        Changed?.Invoke();
    }
}
