using System.Security.Cryptography;
using System.Text.Json;

namespace PoeBuilder.Core.Calculation;

/// <summary>Pinned reverse-translation of passive-tree stat lines into numeric stat ids
/// (tools/prepare_catalog.py). Lines that could not be resolved are never guessed —
/// the calculator reports them as unaccounted instead.</summary>
public sealed class GameStatMap
{
    public const string Sha256 = "c7b4c8413f8dafb40a9be57e0718ac6ab664d543cff1251c12368e5081127111";
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> Lines { get; }

    private GameStatMap(IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> lines) => Lines = lines;

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
