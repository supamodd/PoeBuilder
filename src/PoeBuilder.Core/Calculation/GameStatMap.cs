using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PoeBuilder.Core.Calculation;

/// <summary>Pinned reverse-translation of passive-tree stat lines into numeric stat ids
/// (tools/prepare_catalog.py). Lines that could not be resolved are never guessed —
/// the calculator reports them as unaccounted instead.</summary>
public sealed class GameStatMap
{
    public const string Sha256 = "c7b4c8413f8dafb40a9be57e0718ac6ab664d543cff1251c12368e5081127111";
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> Lines { get; }

    private static readonly Regex NumberToken = new(@"[+-]?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private Dictionary<string, string>? _singleNumberIds;

    private GameStatMap(IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> lines) => Lines = lines;

    /// <summary>
    /// Numeric-tolerant lookup for a wording whose pinned key carries a concrete roll. Entries are keyed by
    /// the translated line ("15% increased Critical Damage Bonus for Attack Damage"), so a jewel that grants
    /// the same wording at another value ("12% …") would miss an exact lookup. Lines holding exactly ONE
    /// number and ONE stat id are indexed with that number removed, and the queried number is paired with the
    /// id. A multi-value line is deliberately NOT resolved here: the map does not promise an order for its
    /// values, so guessing which value belongs to which id would be an invention.
    /// </summary>
    public bool TryResolve(string line, out string id, out decimal value)
    {
        id = ""; value = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;
        var numbers = NumberToken.Matches(line);
        if (numbers.Count != 1) return false;
        var index = _singleNumberIds ??= BuildSingleNumberIndex();
        if (!index.TryGetValue(Normalize(line), out string? found) || found is null) return false;
        id = found;
        value = decimal.Parse(numbers[0].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        return true;
    }

    private Dictionary<string, string> BuildSingleNumberIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (text, stats) in Lines)
        {
            if (stats.Count != 1 || NumberToken.Matches(text).Count != 1) continue;
            index.TryAdd(Normalize(text), stats.Keys.First());
        }
        return index;
    }

    private static string Normalize(string line) => NumberToken.Replace(line, "#");

    public static GameStatMap Load(string path)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Stat map exceeds size limit.");
        var bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Stat map checksum mismatch.");
        var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, decimal>>>(bytes) ?? throw new InvalidDataException("Empty stat map.");
        return new(raw.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, decimal>)p.Value));
    }
}
