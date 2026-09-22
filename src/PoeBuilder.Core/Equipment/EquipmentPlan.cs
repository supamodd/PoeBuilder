using PoeBuilder.Core.Models;

namespace PoeBuilder.Core.Equipment;

public sealed record ModRoll
{
    public string Id { get; init; } = "";
    public decimal[] Values { get; init; } = [];
    public ModRoll Copy() => this with { Values = [.. Values] };
}
public sealed record GearItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string BaseId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Rarity { get; init; } = "rare";
    public int ItemLevel { get; init; } = 80;
    public int Quality { get; init; }
    public int SocketCapacity { get; init; } // User-specified capacity, not a base's calculated maximum.
    public ModRoll[] Mods { get; init; } = [];
    public bool Corrupted { get; init; }
    public ModRoll[] CorruptedMods { get; init; } = [];
    public string[] Augments { get; init; } = [];
    public string Notes { get; init; } = "";
    public GearItem Copy() => this with { Mods = Mods.Select(m => m.Copy()).ToArray(), CorruptedMods = CorruptedMods.Select(m => m.Copy()).ToArray(), Augments = [.. Augments] };
    public void ValidateStructure()
    {
        // Unique items may carry no catalog base (imported uniques/jewels keep their own name and text).
        bool uniqueWithoutBase = Rarity == "unique" && string.IsNullOrWhiteSpace(BaseId);
        if (Id == Guid.Empty || BaseId.Length > 300 || (!string.IsNullOrWhiteSpace(BaseId) && !(Rarity is "normal" or "magic" or "rare" or "unique")) ||
            Name is null || Name.Length > 120 ||
            Rarity is not ("normal" or "magic" or "rare" or "unique") || (uniqueWithoutBase && string.IsNullOrWhiteSpace(Name)) ||
            ItemLevel is < 1 or > 100 || Quality is < 0 or > 20 || SocketCapacity is < 0 or > 6 ||
            Mods is null || Mods.Length > 6 || Mods.Any(m => m is null || string.IsNullOrWhiteSpace(m.Id) || m.Id.Length > 300 || m.Values is null || m.Values.Length > 16 || m.Values.Any(v => v is < -1000000000 or > 1000000000)) ||
            CorruptedMods is null || CorruptedMods.Any(m => m is null || string.IsNullOrWhiteSpace(m.Id) || m.Id.Length > 300 || m.Values is null || m.Values.Length > 16) ||
            Augments is null || Augments.Length > SocketCapacity || Augments.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > 300) || Notes is null || Notes.Length > 10000)
            throw new BuildFormatException("Invalid equipment item structure.");
        if (Mods.Select(m => m.Id).Distinct().Count() != Mods.Length) throw new BuildFormatException("Duplicate item modifier.");
        if (CorruptedMods.Select(m => m.Id).Distinct().Count() != CorruptedMods.Length) throw new BuildFormatException("Duplicate corrupted modifier.");
        if (Augments.Select(a => a).Distinct().Count() != Augments.Length) throw new BuildFormatException("Duplicate item augment.");
    }
}
public sealed record EquipmentPlan
{
    public string DatasetId { get; init; } = GameCatalog.Dataset;
    public GearItem[] Items { get; init; } = [];
    public Dictionary<string, Guid> Slots { get; init; } = [];
    public int WeaponSet { get; init; } = 1;
    public EquipmentPlan Copy() => this with { Items = Items.Select(i => i.Copy()).ToArray(), Slots = new(Slots) };
    public void ValidateStructure()
    {
        if (string.IsNullOrWhiteSpace(DatasetId) || DatasetId.Length > 160 || WeaponSet is not (1 or 2) || Items is null || Items.Length > 250 || Items.Any(i => i is null) ||
            Slots is null || Slots.Count > EquipmentRules.SlotIds.Length || Slots.Keys.Any(s => !EquipmentRules.SlotIds.Contains(s))) throw new BuildFormatException("Invalid equipment plan.");
        foreach (var item in Items) item.ValidateStructure();
        if (Items.Select(i => i.Id).Distinct().Count() != Items.Length || Slots.Values.Any(id => !Items.Any(i => i.Id == id)) || Slots.Values.Distinct().Count() != Slots.Count)
            throw new BuildFormatException("Invalid equipment item references.");
    }
}
public static class EquipmentRules
{
    public static readonly string[] SlotIds = ["Helmet", "Body", "Gloves", "Boots", "Belt", "Amulet", "Ring1", "Ring2", "Main1", "Off1", "Main2", "Off2", "LifeFlask", "ManaFlask", "Charm1", "Charm2", "Charm3"];
    public static bool Fits(string slot, ItemBase b) => slot switch
    {
        "Helmet" => b.ItemClass == "Helmet", "Body" => b.ItemClass == "Body Armour", "Gloves" => b.ItemClass == "Gloves", "Boots" => b.ItemClass == "Boots",
        "Belt" => b.ItemClass == "Belt", "Amulet" => b.ItemClass == "Amulet", "Ring1" or "Ring2" => b.ItemClass == "Ring",
        "LifeFlask" => b.ItemClass == "LifeFlask", "ManaFlask" => b.ItemClass == "ManaFlask", "Charm1" or "Charm2" or "Charm3" => b.ItemClass == "UtilityFlask",
        "Main1" or "Main2" => b.Tags.Contains("weapon"),
        "Off1" or "Off2" => b.ItemClass is "Shield" or "Buckler" or "Focus" or "Quiver" || b.Tags.Contains("one_hand_weapon"),
        _ => false
    };
    public static bool FitsUnique(string slot, UniqueItem item) => slot switch
    {
        "Helmet" => item.ItemClass == "Helmet", "Body" => item.ItemClass == "Body Armour", "Gloves" => item.ItemClass == "Gloves", "Boots" => item.ItemClass == "Boots",
        "Belt" => item.ItemClass == "Belt", "Amulet" => item.ItemClass == "Amulet", "Ring1" or "Ring2" => item.ItemClass == "Ring",
        "Main1" or "Main2" => item.ItemClass.Contains("Weapon", StringComparison.OrdinalIgnoreCase),
        "Off1" or "Off2" => item.ItemClass is "Shield" or "Buckler" or "Focus" or "Quiver" || item.ItemClass.Contains("Weapon", StringComparison.OrdinalIgnoreCase),
        "LifeFlask" => item.ItemClass == "LifeFlask", "ManaFlask" => item.ItemClass == "ManaFlask",
        "Charm1" or "Charm2" or "Charm3" => item.ItemClass == "UtilityFlask", _ => false
    };
    public static void ValidateItem(GameCatalog catalog, GearItem item)
    {
        item.ValidateStructure();
        if (item.Rarity == "unique")
        {
            // Uniques are outside the affix system: the pinned catalog has no unique modifiers, so an
            // imported unique keeps its mods as item text (Notes) and contributes nothing to v1 math.
            if (string.IsNullOrWhiteSpace(item.BaseId))
            {
                if (item.Mods.Length > 0 || item.CorruptedMods.Length > 0) throw new PlanningException("PlanAffixCap");
                return;
            }
            if (!catalog.Bases.TryGetValue(item.BaseId, out var ub)) throw new PlanningException("PlanUnknownBase");
            if (item.ItemLevel < ub.DropLevel) throw new PlanningException("PlanItemLevel");
            if (item.Mods.Length > 0) throw new PlanningException("PlanAffixCap");
        }
        if (item.BaseId.Length == 0)
        {
            // PoE2 jewels are absent from the pinned base list: a baseless magic/rare item whose
            // affixes all come from the jewel pool is kept as a jewel inventory item. Rolls keep
            // their observed values even outside pinned ranges (import fidelity over range law).
            var jewelIds = new HashSet<string>(catalog.JewelMods.Select(m => m.Id));
            if (item.Mods.Length > 0 && item.Mods.All(r => jewelIds.Contains(r.Id))) return;
            throw new PlanningException("PlanUnknownBase");
        }
        if (!catalog.Bases.TryGetValue(item.BaseId, out var b)) throw new PlanningException("PlanUnknownBase");
        if (item.ItemLevel < b.DropLevel) throw new PlanningException("PlanItemLevel");
        var allowed = catalog.ModsFor(b, item.ItemLevel).Select(m => m.Id).ToHashSet(); var groups = new HashSet<string>();
        int prefixes = 0, suffixes = 0;
        foreach (var roll in item.Mods)
        {
            if (!allowed.Contains(roll.Id)) throw new PlanningException("PlanModInvalid");
            var m = catalog.Mods[roll.Id];
            if (m.Groups.Any(g => groups.Contains(g))) throw new PlanningException("PlanModGroup");
            groups.UnionWith(m.Groups);
            if (m.Kind == "prefix") prefixes++; else if (m.Kind == "suffix") suffixes++; else throw new PlanningException("PlanModInvalid");
            if (roll.Values.Length != m.Stats.Length || roll.Values.Where((v, i) => v < m.Stats[i].Min || v > m.Stats[i].Max || decimal.Truncate(v) != v).Any()) throw new PlanningException("PlanModValues");
        }
        int cap = item.Rarity == "normal" ? 0 : item.Rarity == "magic" ? 1 : 3;
        if (prefixes > cap || suffixes > cap) throw new PlanningException("PlanAffixCap");
        // Corruption: at most one corrupted implicit, from the base's own corrupted pool.
        if (item.CorruptedMods.Length > 1) throw new PlanningException("PlanCorruptLimit");
        if (item.CorruptedMods.Length > 0 && !item.Corrupted) throw new PlanningException("PlanCorruptLimit");
        var corruptedAllowed = catalog.CorruptedFor(b).Select(m => m.Id).ToHashSet();
        foreach (var roll in item.CorruptedMods)
        {
            if (!corruptedAllowed.Contains(roll.Id)) throw new PlanningException("PlanCorruptInvalid");
            var m = catalog.Mods[roll.Id];
            if (roll.Values.Length != m.Stats.Length || roll.Values.Where((v, i) => v < m.Stats[i].Min || v > m.Stats[i].Max || decimal.Truncate(v) != v).Any()) throw new PlanningException("PlanModValues");
        }
        foreach (var id in item.Augments)
        {
            if (!catalog.Augments.TryGetValue(id, out var aug) || catalog.AugmentEffect(b, aug).Length == 0) throw new PlanningException("PlanAugmentInvalid");
            // Bare "1" is covered by per-item uniqueness above. Named shared-family caps
            // (e.g. "1 Ancient Augment", "1 Aldur's Legacy") span several different augments
            // with unverified scope, so they fail closed instead of pretending to validate.
            if (aug.Limit.Length > 0 && aug.Limit != "1") throw new PlanningException("PlanAugmentLimit");
        }
    }
    public static void Validate(GameCatalog catalog, EquipmentPlan plan)
    {
        plan.ValidateStructure(); if (plan.DatasetId != GameCatalog.Dataset) throw new PlanningException("PlanDataset");
        foreach (var item in plan.Items) ValidateItem(catalog, item);
        foreach (var (slot, id) in plan.Slots)
            if (!Fits(slot, catalog.Bases[plan.Items.Single(i => i.Id == id).BaseId])) throw new PlanningException("PlanSlot");
        for (int set = 1; set <= 2; set++)
        {
            ItemBase? Base(string slot) => plan.Slots.TryGetValue(slot, out var id) ? catalog.Bases[plan.Items.Single(i => i.Id == id).BaseId] : null;
            var main = Base("Main" + set); var off = Base("Off" + set);
            if (off?.ItemClass == "Quiver" && main?.ItemClass != "Bow") throw new PlanningException("PlanQuiver");
            if (main?.Tags.Contains("two_hand_weapon") == true && off is not null && !(main.ItemClass == "Bow" && off.ItemClass == "Quiver")) throw new PlanningException("PlanTwoHand");
        }
    }
    public static EquipmentPlan Put(GameCatalog catalog, EquipmentPlan plan, GearItem item, string? slot)
    {
        ValidateItem(catalog, item);
        var next = plan.Copy() with { Items = plan.Items.Where(i => i.Id != item.Id).Select(i => i.Copy()).Append(item.Copy()).ToArray() };
        if (slot is not null)
        {
            if (!SlotIds.Contains(slot)) throw new PlanningException("PlanSlot");
            foreach (var old in next.Slots.Where(p => p.Value == item.Id).Select(p => p.Key).ToArray()) next.Slots.Remove(old);
            next.Slots[slot] = item.Id; // Displaced item remains in the inventory.
        }
        Validate(catalog, next); return next;
    }
    public static EquipmentPlan Unequip(GameCatalog catalog, EquipmentPlan plan, string slot)
    {
        var next = plan.Copy(); next.Slots.Remove(slot); Validate(catalog, next); return next;
    }
    public static EquipmentPlan Delete(GameCatalog catalog, EquipmentPlan plan, Guid id)
    {
        var next = plan.Copy() with { Items = plan.Items.Where(i => i.Id != id).Select(i => i.Copy()).ToArray(), Slots = plan.Slots.Where(p => p.Value != id).ToDictionary() };
        Validate(catalog, next); return next;
    }
}

public sealed class PlanHistory<T>(Func<T, T> copy)
{
    private readonly List<T> _undo = [], _redo = [];
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public void Record(T value) { _undo.Add(copy(value)); if (_undo.Count > 50) _undo.RemoveAt(0); _redo.Clear(); }
    public T Undo(T current) => Move(_undo, _redo, current);
    public T Redo(T current) => Move(_redo, _undo, current);
    public void Clear() { _undo.Clear(); _redo.Clear(); }
    private T Move(List<T> from, List<T> to, T current)
    { if (from.Count == 0) return copy(current); to.Add(copy(current)); var v = from[^1]; from.RemoveAt(from.Count - 1); return copy(v); }
}
