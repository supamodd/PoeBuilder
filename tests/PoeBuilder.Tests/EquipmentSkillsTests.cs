using System.Text.Json;
using System.Text.Json.Nodes;
using PoeBuilder.App.ViewModels;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Skills;
using PoeBuilder.Core.Storage;

internal static class EquipmentSkillsTests
{
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static void Plan(string code, Action action)
    { try { action(); } catch (PlanningException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
    private static void Bad(Action action)
    { try { action(); } catch (BuildFormatException) { return; } throw new Exception("Expected BuildFormatException"); }
    private static ModRoll MaxRoll(ItemMod mod) => new() { Id = mod.Id, Values = mod.Stats.Select(s => s.Max).ToArray() };

    public static async Task Run(Func<string, Func<Task>, Task> test, string folder)
    {
        var catalog = GameCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "catalog.json"));
        await test("Equipment: required unique identities and artwork paths are available", () => Task.Run(() =>
        {
            foreach (var name in new[] { "Hands of Wisdom and Action", "Morior Invictus", "Headhunter" })
                Assert(catalog.Uniques.ContainsKey(name), "missing unique " + name);
            Assert(catalog.Uniques["Headhunter"].Icon.EndsWith("Headhunter.dds", StringComparison.Ordinal), "headhunter artwork");
        }));
        Task Check(Action a) { a(); return Task.CompletedTask; }
        var body = catalog.Bases.Values.First(b => b.ItemClass == "Body Armour" && b.DropLevel == 1);
        var bodyMods = catalog.ModsFor(body, 80).ToArray();
        var prefix = bodyMods.First(m => m.Kind == "prefix");
        var suffix = bodyMods.First(m => m.Kind == "suffix" && !m.Groups.Intersect(prefix.Groups).Any());
        GearItem BodyItem(string rarity = "rare", int ilvl = 80) => new()
        {
            BaseId = body.Id, Name = "Test Plate", Rarity = rarity, ItemLevel = ilvl, Quality = 20,
            Mods = [MaxRoll(prefix), MaxRoll(suffix)]
        };

        await test("Game catalog: pinned snapshot exposes every supported class with pools", () => Check(() =>
        {
            Assert(catalog.Data.DatasetId == GameCatalog.Dataset && catalog.Data.SourceVersion == "4.5.5.2");
            Assert(catalog.Bases.Count == 1845 && catalog.Mods.Count == 1389 && catalog.Augments.Count == 300 && catalog.Gems.Count == 1115);
            foreach (string cls in new[] { "Body Armour", "Helmet", "Gloves", "Boots", "Belt", "Ring", "Amulet", "Shield", "Quiver",
                     "Bow", "Wand", "One Hand Sword", "Two Hand Sword", "LifeFlask", "ManaFlask", "UtilityFlask" })
                Assert(catalog.Bases.Values.Any(b => b.ItemClass == cls), cls);
            Assert(catalog.Bases.Values.All(b => b.ModPool.Length > 0 && catalog.Data.ModPools.ContainsKey(b.ModPool)));
            Assert(catalog.Data.CorruptedPools.Count == catalog.Data.ModPools.Count, "corrupted pools mirror class pools");
            Assert(catalog.Data.CorruptedPools.Values.Any(v => v.Length > 0), "corrupted pools are populated");
            Assert(catalog.Gems.Values.Any(g => g.Kind == "active") && catalog.Gems.Values.Any(g => g.Kind == "support") && catalog.Gems.Values.Any(g => g.Kind == "spirit"));
        }));
        await test("Equipment: rare body armour with pool mods validates and equips", () => Check(() =>
        {
            var item = BodyItem();
            EquipmentRules.ValidateItem(catalog, item);
            var plan = EquipmentRules.Put(catalog, new(), item, "Body");
            Assert(plan.Slots["Body"] == item.Id && plan.Items.Length == 1);
            EquipmentRules.Validate(catalog, plan);
        }));
        await test("Equipment: rarity caps and group conflicts fail closed", () => Check(() =>
        {
            var secondPrefix = bodyMods.First(m => m.Kind == "prefix" && m.Id != prefix.Id && !m.Groups.Intersect(prefix.Groups).Any());
            Plan("PlanAffixCap", () => EquipmentRules.ValidateItem(catalog, BodyItem("magic") with { Mods = [MaxRoll(prefix), MaxRoll(secondPrefix)] }));
            Plan("PlanAffixCap", () => EquipmentRules.ValidateItem(catalog, BodyItem("normal")));
            var pair = bodyMods.Where(m => m.Groups.Length > 0).GroupBy(m => m.Groups.First()).First(g => g.Count() > 1).Take(2).ToArray();
            Plan("PlanModGroup", () => EquipmentRules.ValidateItem(catalog, BodyItem() with { Mods = [MaxRoll(pair[0]), MaxRoll(pair[1])] }));
        }));
        await test("Equipment: foreign mods, bad values and low item level are rejected", () => Check(() =>
        {
            var bow = catalog.Bases.Values.First(b => b.ItemClass == "Bow");
            var foreign = catalog.ModsFor(bow, 80).Select(m => m.Id).First(id => !bodyMods.Any(m => m.Id == id));
            var foreignMod = catalog.Mods[foreign];
            Plan("PlanModInvalid", () => EquipmentRules.ValidateItem(catalog, BodyItem() with { Mods = [MaxRoll(foreignMod)] }));
            Plan("PlanModValues", () => EquipmentRules.ValidateItem(catalog, BodyItem() with { Mods = [new() { Id = prefix.Id, Values = [prefix.Stats[0].Max + 1] }] }));
            Plan("PlanModValues", () => EquipmentRules.ValidateItem(catalog, BodyItem() with { Mods = [new() { Id = prefix.Id, Values = [] }] }));
            var highBase = catalog.Bases.Values.First(b => b.ItemClass == "Body Armour" && b.DropLevel > 10);
            Plan("PlanItemLevel", () => EquipmentRules.ValidateItem(catalog, BodyItem("rare", 1) with { BaseId = highBase.Id, Mods = [] }));
            Plan("PlanUnknownBase", () => EquipmentRules.ValidateItem(catalog, BodyItem() with { BaseId = "Metadata/Nope" }));
            Bad(() => EquipmentRules.ValidateItem(catalog, BodyItem() with { Mods = [MaxRoll(prefix), MaxRoll(prefix)] }));
        }));
        await test("Equipment: socketables allow plain limits, block family caps and mismatches", () => Check(() =>
        {
            var plain = catalog.Augments.Values.First(a => (a.Limit is "" or "1") && catalog.AugmentEffect(body, a).Length > 0);
            var named = catalog.Augments.Values.First(a => a.Limit.Length > 0 && a.Limit != "1" && catalog.AugmentEffect(body, a).Length > 0);
            var mismatch = catalog.Augments.Values.First(a => catalog.AugmentEffect(body, a).Length == 0);
            EquipmentRules.ValidateItem(catalog, BodyItem() with { SocketCapacity = 1, Augments = [plain.Id] });
            Plan("PlanAugmentLimit", () => EquipmentRules.ValidateItem(catalog, BodyItem() with { SocketCapacity = 1, Augments = [named.Id] }));
            Plan("PlanAugmentInvalid", () => EquipmentRules.ValidateItem(catalog, BodyItem() with { SocketCapacity = 1, Augments = [mismatch.Id] }));
            Bad(() => EquipmentRules.ValidateItem(catalog, BodyItem() with { SocketCapacity = 2, Augments = [plain.Id, plain.Id] }));
            Bad(() => EquipmentRules.ValidateItem(catalog, BodyItem() with { SocketCapacity = 0, Augments = [plain.Id] }));
        }));
        await test("Equipment: slot, quiver and two-hand rules", () => Check(() =>
        {
            GearItem Bare(string cls, string? name = null)
            {
                var b = catalog.Bases.Values.First(x => x.ItemClass == cls);
                return new() { BaseId = b.Id, Name = name ?? b.Name, Rarity = "normal", ItemLevel = Math.Max(1, b.DropLevel) };
            }
            Plan("PlanSlot", () => EquipmentRules.Put(catalog, new(), Bare("Ring"), "Body"));
            var quiver = Bare("Quiver");
            Plan("PlanQuiver", () => EquipmentRules.Put(catalog, new(), quiver, "Off1"));
            var bowPlan = EquipmentRules.Put(catalog, new(), Bare("Bow"), "Main1");
            bowPlan = EquipmentRules.Put(catalog, bowPlan, quiver, "Off1");
            Assert(bowPlan.Slots["Off1"] == quiver.Id);
            var twoHand = EquipmentRules.Put(catalog, new(), Bare("Two Hand Sword"), "Main1");
            Plan("PlanTwoHand", () => EquipmentRules.Put(catalog, twoHand, Bare("Shield"), "Off1"));
            var dual = EquipmentRules.Put(catalog, new(), Bare("One Hand Sword"), "Main1");
            dual = EquipmentRules.Put(catalog, dual, Bare("Shield"), "Off1");
            Assert(dual.Slots.Count == 2);
        }));
        await test("Equipment: flasks equip into flask and charm slots with flask-domain mods", () => Check(() =>
        {
            var life = catalog.Bases.Values.First(b => b.ItemClass == "LifeFlask");
            var flaskMod = catalog.ModsFor(life, 80).First();
            var item = new GearItem { BaseId = life.Id, Name = "Test Flask", Rarity = "magic", ItemLevel = 80, Mods = [MaxRoll(flaskMod)] };
            EquipmentRules.ValidateItem(catalog, item);
            Assert(EquipmentRules.Put(catalog, new(), item, "LifeFlask").Slots.ContainsKey("LifeFlask"));
            var charm = catalog.Bases.Values.First(b => b.ItemClass == "UtilityFlask");
            var charmItem = new GearItem { BaseId = charm.Id, Rarity = "normal", ItemLevel = Math.Max(1, charm.DropLevel) };
            Assert(EquipmentRules.Put(catalog, new(), charmItem, "Charm1").Slots.ContainsKey("Charm1"));
            Plan("PlanSlot", () => EquipmentRules.Put(catalog, new(), charmItem, "LifeFlask"));
        }));
        await test("Equipment: sets, unequip, delete and history", () => Check(() =>
        {
            var history = new PlanHistory<EquipmentPlan>(p => p.Copy());
            var plan = new EquipmentPlan { WeaponSet = 2 };
            var main = new GearItem { BaseId = catalog.Bases.Values.First(b => b.ItemClass == "Bow").Id, Rarity = "normal", ItemLevel = 1 };
            history.Record(plan); plan = EquipmentRules.Put(catalog, plan, main, "Main2");
            Assert(history.CanUndo && !history.CanRedo && plan.Slots.ContainsKey("Main2"));
            plan = history.Undo(plan); Assert(!plan.Slots.ContainsKey("Main2") && history.CanRedo);
            plan = history.Redo(plan); Assert(plan.Slots.ContainsKey("Main2"));
            plan = EquipmentRules.Unequip(catalog, plan, "Main2"); Assert(plan.Items.Length == 1 && plan.Slots.Count == 0);
            plan = EquipmentRules.Delete(catalog, plan, main.Id); Assert(plan.Items.Length == 0);
        }));
        var active = catalog.Gems.Values.First(g => g.Kind == "active");
        var support = catalog.Gems.Values.First(g => g.Kind == "support");
        var spirit = catalog.Gems.Values.First(g => g.Kind == "spirit");
        int badSupportLevel = Enumerable.Range(1, 40).First(l => !support.Levels.Contains(l));
        SkillGroup Group(string name = "Main") => new()
        {
            Name = name, Active = new() { GemId = active.Id, Level = 1 },
            Supports = [new() { GemId = support.Id, Level = 1 }]
        };
        await test("Skills: active plus supports validate; misuse fails closed", () => Check(() =>
        {
            SkillRules.ValidateGroup(catalog, Group());
            Plan("PlanGemKind", () => SkillRules.ValidateGroup(catalog, Group() with { Active = new() { GemId = support.Id, Level = 1 } }));
            Plan("PlanGemKind", () => SkillRules.ValidateGroup(catalog, Group() with { Supports = [new() { GemId = active.Id, Level = 1 }] }));
            Plan("PlanGemLevel", () => SkillRules.ValidateGroup(catalog, Group() with { Supports = [new() { GemId = support.Id, Level = badSupportLevel }] }));
            Bad(() => SkillRules.ValidateGroup(catalog, Group() with { Supports = [new() { GemId = support.Id, Level = 1 }, new() { GemId = support.Id, Level = 1 }] }));
            Bad(() => SkillRules.ValidateGroup(catalog, Group() with
            {
                Supports = catalog.Gems.Values.Where(g => g.Kind == "support").Take(6).Select(g => new GemSelection { GemId = g.Id, Level = 1 }).ToArray()
            }));
            SkillRules.ValidateGroup(catalog, Group("Aura") with { Active = new() { GemId = spirit.Id, Level = 1 }, Supports = [] });
        }));
        await test("Skills: groups persist through editor, save, duplicate, import and export", async () =>
        {
            var repo = new BuildRepository(Path.Combine(folder, "skill-io"));
            var plan = SkillRules.Put(catalog, new(), Group());
            plan = SkillRules.Put(catalog, plan, Group("Second") with { WeaponSet = 1, Enabled = false });
            var editor = new BuildEditor(BuildDocument.Create("Skills")); editor.SetSkills(plan); Assert(editor.IsDirty);
            var doc = await repo.SaveAsync(editor.ToDocument());
            var read = await BuildRepository.ReadDocumentAsync(repo.PathFor(doc.Id));
            Assert(read.Skills!.Groups.Length == 2 && read.Skills.Groups.Any(g => g.Name == "Second" && !g.Enabled));
            var copy = await repo.DuplicateAsync(read, "Copy"); var imported = await repo.ImportAsNewAsync(repo.PathFor(doc.Id));
            Assert(copy.Skills!.Groups.Length == 2 && imported.Skills!.Groups.Length == 2 && copy.Id != read.Id && imported.Id != read.Id);
            var export = Path.Combine(folder, "skills-export.poebuild"); await BuildRepository.WriteDocumentAsync(export, copy);
            Assert((await BuildRepository.ReadDocumentAsync(export)).Skills!.Groups[0].Active.GemId == active.Id);
        });
        await test("Schema 3 files migrate to 4; legacy equipment payload is rejected", async () =>
        {
            var doc = BuildDocument.Create("Legacy 0.3") with { Equipment = new() { Items = [BodyItem()] }, Skills = new() { Groups = [Group()] } };
            var legacy = BuildDocument.Create("Legacy 0.3") with { Tree = new() { ClassIndex = 6, AllocatedNodes = [16732] } };
            var clean = JsonSerializer.SerializeToNode(legacy, BuildRepository.JsonOptions)!;
            clean["schemaVersion"] = 3; var path = Path.Combine(folder, "legacy3.poebuild");
            await File.WriteAllTextAsync(path, clean.ToJsonString());
            var read = await BuildRepository.ReadDocumentAsync(path);
            Assert(read.SchemaVersion == 4 && read.Equipment is null && read.Skills is null);
            Assert(read.Tree!.AllocatedNodes.SequenceEqual([16732]) && read.Tree.ClassIndex == 6);
            foreach (string key in new[] { "equipment", "skills" })
            {
                var json = JsonSerializer.SerializeToNode(doc, BuildRepository.JsonOptions)!;
                json["schemaVersion"] = 3; json.AsObject().Remove(key == "equipment" ? "skills" : "equipment");
                await File.WriteAllTextAsync(path, json.ToJsonString());
                try { await BuildRepository.ReadDocumentAsync(path); throw new Exception("Accepted legacy " + key); }
                catch (BuildFormatException) { }
            }
        });
        await test("Equipment roundtrip preserves items, slots and arrays without aliasing", async () =>
        {
            var repo = new BuildRepository(Path.Combine(folder, "equip-io"));
            var item = BodyItem(); var plan = EquipmentRules.Put(catalog, new(), item, "Body");
            var editor = new BuildEditor(BuildDocument.Create("Equip")); editor.SetEquipment(plan);
            plan.Items[0].Mods[0].Values[0] = -999; item.Mods[0].Values[0] = -999;
            Assert(editor.EquipmentSnapshot!.Items[0].Mods[0].Values[0] != -999);
            var read = await BuildRepository.ReadDocumentAsync(repo.PathFor((await repo.SaveAsync(editor.ToDocument())).Id));
            Assert(read.Equipment!.Slots["Body"] == read.Equipment.Items.Single().Id && read.Equipment.Items[0].Quality == 20);
        });
        await test("Planning: foreign datasets stay preserved but uneditable", async () =>
        {
            var foreign = new EquipmentPlan { DatasetId = "future", Items = [BodyItem()] };
            var skills = new SkillPlan { DatasetId = "future", Groups = [Group()] };
            Plan("PlanDataset", () => EquipmentRules.Validate(catalog, foreign));
            Plan("PlanDataset", () => SkillRules.Validate(catalog, skills));
            var path = Path.Combine(folder, "foreign-plan.poebuild");
            await BuildRepository.WriteDocumentAsync(path, BuildDocument.Create("Foreign") with { Equipment = foreign, Skills = skills });
            var read = await BuildRepository.ReadDocumentAsync(path);
            Assert(read.Equipment!.Items.Length == 1 && read.Skills!.Groups.Length == 1);
        });
        await test("UI regression: catalog display names never expose record internals", () => Check(() =>
        {
            foreach (var b in catalog.Bases.Values) Assert(b.ToString() == b.Name && b.Name.Length > 0);
            foreach (var m in catalog.Mods.Values) Assert(m.ToString().Length > 0 && !m.ToString().Contains('{') && !m.ToString().Contains("Id ="));
            foreach (var a in catalog.Augments.Values) Assert(a.ToString() == a.Name + " · " + a.Kind);
            foreach (var g in catalog.Gems.Values) Assert(g.ToString() == g.Name && g.Name.Length > 0);
        }));
    }
}
