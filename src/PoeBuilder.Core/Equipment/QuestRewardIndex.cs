using System.Security.Cryptography;
using System.Text.Json;

namespace PoeBuilder.Core.Equipment;

/// <summary>One quest reward exactly as PoB2 stores it
/// (PathOfBuilding-PoE2-master/src/Data/QuestRewards.lua). <see cref="Stat"/> is the reward line
/// ("+10% to Cold Resistance"); a choice quest lists its selectable lines in <see cref="Options"/> and
/// has an empty Stat until the player picks one. <see cref="UseConfig"/> is PoB2's own flag for
/// "apply this from the build config", which is how a levelled character ends up with all of them.</summary>
public sealed record QuestReward(int Act, string Description, string Area, string Info, string Stat,
    string[] Options, int QuestPoints, int AreaLevel, bool UseConfig)
{
    /// <summary>The line this entry contributes when the player has not chosen: its single Stat, or the
    /// first choice of a choice quest (PoB2 itself defaults those to "None", so a caller that only wants
    /// the automatic rewards should read <see cref="QuestRewardIndex.ConfigLines"/> instead).</summary>
    public string DefaultLine => Stat.Length > 0 ? Stat : Options.FirstOrDefault() ?? "";
}

/// <summary>Loaded <c>questrewards.json</c>: PoB2's whole quest-reward table. A missing file is an empty
/// index (a build can run without it); a checksum mismatch always fails closed.</summary>
public sealed class QuestRewardIndex
{
    public const string Sha256 = "31bbd9a2e1ebc04811b8d49ac1fa8f6d77a798c722744766e154c29284204bc0";
    public static readonly QuestRewardIndex Empty = new([]);

    private readonly IReadOnlyList<QuestReward> _rewards;
    private QuestRewardIndex(IReadOnlyList<QuestReward> rewards) => _rewards = rewards;

    public IReadOnlyList<QuestReward> Rewards => _rewards;
    public int Count => _rewards.Count;

    /// <summary>The acts the table covers, ascending — the tab groups by them like PoB2 does.</summary>
    public IReadOnlyList<int> Acts => [.. _rewards.Select(r => r.Act).Distinct().Order()];

    /// <summary>The reward lines a levelled character has: every entry PoB2 switches on through the
    /// config, in the data's own order. A choice quest contributes nothing until the player picks a line
    /// (PoB2 defaults those to "None"), so only entries with a single Stat are returned.</summary>
    public IReadOnlyList<string> ConfigLines()
    {
        var lines = new List<string>();
        foreach (var reward in _rewards)
        {
            if (!reward.UseConfig || reward.Stat.Length == 0) continue;
            lines.Add(reward.Stat);
        }
        return lines;
    }

    /// <summary>PoB2's option strings carry the game's own line breaks and the leading tab of a wrapped
    /// line ("30% increased Charm Charges Gained\n\t+1 Charm Slot"), a share code stores a flattened
    /// variant of the same reward (XML turns the newline and the tab into two spaces), and the importer
    /// writes it trimmed per line. Matching therefore compares the whitespace-collapsed form, so a choice
    /// quest's option still matches the reward the build was imported with.</summary>
    public static string NormaliseLine(string line) =>
        System.Text.RegularExpressions.Regex.Replace(line, @"\s+", " ").Trim();

    public static QuestRewardIndex Load(string path)
    {
        if (!File.Exists(path)) return Empty;
        var bytes = File.ReadAllBytes(path);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Quest reward data checksum mismatch.");
        using var json = JsonDocument.Parse(bytes);
        var rewards = new List<QuestReward>();
        if (json.RootElement.TryGetProperty("rewards", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var entry in list.EnumerateArray())
            {
                string area = Text(entry, "Area");
                string info = Text(entry, "Info");
                if (area.Length == 0 && info.Length == 0) continue;
                rewards.Add(new(Number(entry, "Act"), Text(entry, "Description"), area, info, Text(entry, "Stat"),
                    Strings(entry, "Options"), Number(entry, "questPoints"), Number(entry, "AreaLevel"),
                    Flag(entry, "useConfig")));
            }
        return new(rewards);
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string[] Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return [.. v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? "").Where(x => x.Length > 0)];
    }
}
