using PoeBuilder.Core.Localization;

namespace PoeBuilder.App.Services;

/// <summary>One display language's view of the game data. Everything that shows a game name or filters a
/// list of them goes through here, so a Russian interface shows Russian and an English one shows English
/// from the same call site.
///
/// Two rules hold everywhere:
///  * A name with no translation falls back to the real English name. Nothing is invented.
///  * Search always matches BOTH languages. Filtering on the displayed text alone would make a Russian
///    build impossible to find with the English term a player copied from the game client, and the other
///    way round.
/// The underlying ids, stats and saved documents are untouched: this only affects what is displayed.</summary>
public sealed class GameLocale(GameStrings? strings, Localization ui)
{
    public GameStrings? Strings { get; } = strings;
    public Localization Ui { get; } = ui;

    /// <summary>True when the interface is Russian AND we have real Russian text for this data. A Russian
    /// UI with an empty file must show English, not blanks.</summary>
    public bool Russian => Ui.Language == "ru" && Strings is { IsEmpty: false };

    public Provenance Source => Strings?.Source ?? new Provenance();

    /// <summary>The name to display for a stored English name.</summary>
    public string Name(string? english)
    {
        if (string.IsNullOrWhiteSpace(english)) return "";
        return Russian ? Strings!.Name(english, true) : english;
    }

    /// <summary>Does this entity match the query? English text always matches; the Russian name is
    /// searched as well when the interface is Russian.</summary>
    public bool Matches(string? english, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (!string.IsNullOrWhiteSpace(english) && english.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (Russian)
        {
            var ru = Strings!.Name(english, true);
            return !string.IsNullOrEmpty(ru) && ru.Contains(query, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>The Russian text of a modifier, or the English text when this modifier has no
    /// translation. Never null, so a caller can always paint the result.</summary>
    public string Mod(string? englishTemplate) => (Russian ? Strings!.Mod(englishTemplate) : null) ?? englishTemplate ?? "";

    /// <summary>Does this modifier match the query, in either language?</summary>
    public bool ModMatches(string? englishTemplate, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (!string.IsNullOrEmpty(englishTemplate) && englishTemplate.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var ru = Russian ? Strings!.Mod(englishTemplate) : null;
        return ru is not null && ru.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Matches on a pair of texts (a mod's name and its rendered line, say) in either language.</summary>
    public bool Matches(params string?[] texts)
    {
        if (texts.Any(t => !string.IsNullOrWhiteSpace(t) && t.Contains(Search, StringComparison.OrdinalIgnoreCase))) return true;
        if (!Russian) return false;
        return texts.Any(t => !string.IsNullOrWhiteSpace(t) && Name(t).Contains(Search, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The active search text; set by the search boxes so Matches can be called without it.</summary>
    public string Search { get; set; } = "";
}