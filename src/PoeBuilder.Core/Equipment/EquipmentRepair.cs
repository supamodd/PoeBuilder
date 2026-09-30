using PoeBuilder.Core.Models;

namespace PoeBuilder.Core.Equipment;

/// <summary>Read-side repair of an equipment plan. A native file written by an older build, by another
/// tool or by a build that was imported before a rule was tightened can carry an item that today's
/// structure rule rejects — and refusing the WHOLE file locked a user out of their own build with the
/// dialog "Invalid equipment item structure." while they were trying to add a new build. Repair clamps
/// whatever has a sane bound, keeps what is representable, drops only what is not, and names every
/// change in the build's notes: nothing is altered silently, and the file on disk is untouched until an
/// explicit save.</summary>
public static class EquipmentRepair
{
    /// <summary>The same bound <see cref="GearItem.ValidateStructure"/> applies. Kept here so the repair
    /// and the rule can never drift apart.</summary>
    public const int MaximumMods = 12;

    public static BuildDocument Normalize(BuildDocument build)
    {
        if (build.Equipment is not { } equipment || equipment.Items.Length == 0) return build;
        var notes = new List<string>();
        var kept = new List<GearItem>();
        foreach (var item in equipment.Items)
        {
            if (item is null) { notes.Add("пустой предмет удалён"); continue; }
            var repaired = Repair(item, notes);
            if (repaired is null) continue;
            kept.Add(repaired);
        }
        // A plan whose item list changed must not keep slots pointing at removed items.
        var ids = kept.Select(i => i.Id).ToHashSet();
        var slots = equipment.Slots.Where(s => ids.Contains(s.Value)).ToDictionary(s => s.Key, s => s.Value);
        if (slots.Count != equipment.Slots.Count) notes.Add("слоты без предмета отброшены: " + (equipment.Slots.Count - slots.Count));
        if (kept.Count == equipment.Items.Length && slots.Count == equipment.Slots.Count && notes.Count == 0) return build;
        var plan = equipment with { Items = [.. kept], Slots = slots };
        string note = "Снаряжение восстановлено при чтении: " + string.Join("; ", notes);
        return build with { Equipment = plan, Notes = AppendNote(build.Notes, note) };
    }

    private static GearItem? Repair(GearItem item, List<string> notes)
    {
        string label = string.IsNullOrWhiteSpace(item.Name) ? "(без имени)" : item.Name;
        var mods = Sanitize(item.Mods, label, "модификатор", notes);
        var corrupted = Sanitize(item.CorruptedMods, label, "испорченный модификатор", notes);
        var augments = item.Augments.Distinct(StringComparer.Ordinal).Take(6).ToArray();
        if (augments.Length != item.Augments.Length) notes.Add(label + ": вставки усечены до уникальных (<=6)");
        var repaired = item with
        {
            ItemLevel = Math.Clamp(item.ItemLevel, 1, 100),
            Quality = Math.Clamp(item.Quality, 0, 60),
            SocketCapacity = Math.Clamp(item.SocketCapacity, 0, 6),
            Name = item.Name is { Length: > 120 } longName ? longName[..120] : item.Name ?? "",
            Notes = item.Notes is { Length: > 10000 } longNotes ? longNotes[..10000] : item.Notes ?? "",
            Mods = mods,
            CorruptedMods = corrupted,
            Augments = augments
        };
        if (repaired.SocketCapacity < augments.Length)
        {
            repaired = repaired with { Augments = [.. augments.Take(repaired.SocketCapacity)] };
            notes.Add(label + ": вставки усечены по числу гнёзд (" + repaired.SocketCapacity + ")");
        }
        try { repaired.ValidateStructure(); return repaired; }
        catch (BuildFormatException invalid)
        {
            notes.Add(label + " удалён из плана (" + invalid.Message + ")");
            return null;
        }
    }

    /// <summary>Drops entries the structure rule cannot represent: a null roll, a missing id, a roll
    /// whose value list is longer than the model's bound, more than the bound allows, and duplicates (the
    /// plan expects one roll per modifier id). Everything else is kept exactly as written.</summary>
    private static ModRoll[] Sanitize(ModRoll[] rolls, string label, string what, List<string> notes)
    {
        var kept = new List<ModRoll>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool overflow = false;
        foreach (var roll in rolls)
        {
            if (roll is null || string.IsNullOrWhiteSpace(roll.Id) || roll.Id.Length > 300 ||
                roll.Values is null || roll.Values.Length > 16)
            {
                notes.Add(label + ": " + what + " без идентификатора отброшен");
                continue;
            }
            if (!seen.Add(roll.Id))
            {
                notes.Add(label + ": повтор " + what + " " + roll.Id + " отброшен");
                continue;
            }
            if (kept.Count >= MaximumMods)
            {
                if (!overflow) notes.Add(label + ": " + what + "ов больше " + MaximumMods + ", лишние отброшены");
                overflow = true;
                continue;
            }
            kept.Add(roll);
        }
        return [.. kept];
    }

    private static string AppendNote(string? notes, string note)
    {
        if (string.IsNullOrWhiteSpace(notes)) return note;
        string combined = notes.TrimEnd() + "\n" + note;
        return combined.Length > 100000 ? combined[..100000] : combined;
    }
}