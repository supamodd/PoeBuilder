using System.Text.Json;
using System.Text.Json.Nodes;
using PoeBuilder.App.ViewModels;
using PoeBuilder.Core.Calculation;
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
            Assert(catalog.Bases.Count == 1845 + 9, "the pinned bases plus the nine jewel bases of the affix table");
            Assert(catalog.Augments.Count == 300 && catalog.Gems.Count == 1115);
            foreach (string cls in new[] { "Body Armour", "Helmet", "Gloves", "Boots", "Belt", "Ring", "Amulet", "Shield", "Quiver",
                     "Bow", "Wand", "One Hand Sword", "Two Hand Sword", "LifeFlask", "ManaFlask", "UtilityFlask" })
                Assert(catalog.Bases.Values.Any(b => b.ItemClass == cls), cls);
            Assert(catalog.Bases.Values.Where(b => !GameCatalog.IsJewel(b)).All(b => b.ModPool.Length > 0 && catalog.Data.ModPools.ContainsKey(b.ModPool)));
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
        await test("Equipment: a socketed soul core raises the character's maximum Life", () => Task.Run(() =>
        {
            // Soul Core of Jiquani sockets into a body armour and grants "5% increased maximum Life". The
            // effect is freeform game text, so it must move through the same reverse table that reads an
            // imported item's rune lines — a socket is not decorative, it contributes to the build.
            var augment = catalog.Augments.Values.FirstOrDefault(a =>
                catalog.AugmentEffect(body, a).Contains("increased maximum Life", StringComparison.OrdinalIgnoreCase));
            Assert(augment is not null, "the catalog must carry a body armour %Life soul core");
            var plain = EquipmentRules.Put(catalog, new(), BodyItem(), "Body");
            var socketed = EquipmentRules.Put(catalog, new(), BodyItem() with { SocketCapacity = 1, Augments = [augment!.Id] }, "Body");
            var before = CharacterCalculator.Calculate(BuildDocument.Create("Augment") with { Level = 70, Equipment = plain }, null, null, catalog);
            var after = CharacterCalculator.Calculate(BuildDocument.Create("Augment") with { Level = 70, Equipment = socketed }, null, null, catalog);
            Assert(after.Life > before.Life && Math.Abs(after.Life / before.Life - 1.05m) < 0.005m,
                $"expected ~x1.05 maximum Life, got {before.Life} -> {after.Life}");
            Assert(before.Extras.TryGetValue("AugmentsApplied", out _) == false && after.Extras["AugmentsApplied"] == 1,
                "the socket's stat lands in the calculation once");
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
        await test("Schema 3 files migrate to 6; legacy equipment payload is rejected", async () =>
        {
            var doc = BuildDocument.Create("Legacy 0.3") with { Equipment = new() { Items = [BodyItem()] }, Skills = new() { Groups = [Group()] } };
            var legacy = BuildDocument.Create("Legacy 0.3") with { Tree = new() { ClassIndex = 6, AllocatedNodes = [16732] } };
            var clean = JsonSerializer.SerializeToNode(legacy, BuildRepository.JsonOptions)!;
            clean["schemaVersion"] = 3; var path = Path.Combine(folder, "legacy3.poebuild");
            await File.WriteAllTextAsync(path, clean.ToJsonString());
            var read = await BuildRepository.ReadDocumentAsync(path);
            Assert(read.SchemaVersion == 6 && read.Equipment is null && read.Skills is null);
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
        await test("Equipment: a file whose item breaks today's rule is repaired on read, not refused", async () =>
        {
            // Exactly the reported blocker: one item in a native file carried more modifier lines than the
            // structure rule allows, so the WHOLE file was refused ("Invalid equipment item structure.")
            // and the build could not be added. Reading repairs what has a sane bound, keeps the item, and
            // names the change in the notes; the file on disk stays untouched until an explicit save.
            var rolls = bodyMods.Take(13).Select(MaxRoll).ToArray();
            Assert(rolls.Length == 13 && rolls.Select(r => r.Id).Distinct().Count() == 13, "fixture rolls: " + rolls.Length);
            // A 14th roll that is only half there (no id) and a duplicate of the first: both are repaired
            // away and named, not carried into the plan.
            var item = BodyItem() with
            {
                Mods = [.. rolls, new ModRoll { Id = "", Values = [1] }, MaxRoll(prefix)],
                Quality = 90, ItemLevel = 250, SocketCapacity = 9
            };
            var doc = BuildDocument.Create("Repair") with
            {
                Equipment = new EquipmentPlan { Items = [item], Slots = new() { ["Body"] = item.Id } }
            };
            var path = Path.Combine(folder, "repair.poebuild");
            await File.WriteAllTextAsync(path, JsonSerializer.SerializeToNode(doc, BuildRepository.JsonOptions)!.ToJsonString());
            var read = await BuildRepository.ReadDocumentAsync(path);
            var repaired = read.Equipment!.Items.Single();
            Assert(repaired.Mods.Length == EquipmentRepair.MaximumMods,
                "mods bounded to " + EquipmentRepair.MaximumMods + ", got " + repaired.Mods.Length);
            Assert(repaired.Quality == 60 && repaired.ItemLevel == 100 && repaired.SocketCapacity == 6,
                "clamped: q" + repaired.Quality + " ilvl" + repaired.ItemLevel + " cap" + repaired.SocketCapacity);
            Assert(read.Notes.Contains("Снаряжение восстановлено", StringComparison.Ordinal), "the repair is named in the notes");
            Assert(read.Equipment.Slots["Body"] == repaired.Id, "the slot survives the repair");
            // The exact user flow that was blocked: importing the file as a NEW build. It used to fail at
            // the save step with "Invalid equipment item structure." and nothing was added to the library.
            var repo = new BuildRepository(Path.Combine(folder, "repair-import"));
            var added = await repo.ImportAsNewAsync(path);
            Assert(added.Id != doc.Id && added.Equipment!.Items.Single().Mods.Length == EquipmentRepair.MaximumMods,
                "the imported copy is repaired and saved");
            Assert((await repo.ReadLibraryAsync()).Builds.Count == 1, "the library now holds the imported build");
        });
        await test("Equipment: an item that cannot be represented at all is dropped, and said so", async () =>
        {
            // A roll that merely looks broken is repaired (the test above covers the counts); an item that
            // has no identity at all cannot be referenced by a slot either, so it is dropped and named.
            var item = BodyItem() with { Id = Guid.Empty };
            var doc = BuildDocument.Create("Dropped") with
            {
                Equipment = new EquipmentPlan { Items = [item], Slots = new() { ["Body"] = item.Id } }
            };
            var path = Path.Combine(folder, "dropped.poebuild");
            await File.WriteAllTextAsync(path, JsonSerializer.SerializeToNode(doc, BuildRepository.JsonOptions)!.ToJsonString());
            var read = await BuildRepository.ReadDocumentAsync(path);
            Assert(read.Equipment!.Items.Length == 0, "the unrepresentable item is gone");
            Assert(read.Equipment.Slots.Count == 0, "its slot reference is gone too");
            Assert(read.Notes.Contains("удалён из плана", StringComparison.Ordinal), "the drop is named in the notes");
        });
        await test("Uniques: the variant the game uses selects the lines, ranges resolve to their maximum", () => Check(() =>
        {
            var anvil = catalog.UniqueData.For("The Anvil");
            Assert(anvil is not null && anvil.Variants.Length == 3, "The Anvil lists three variants");
            Assert(UniqueItemText.CurrentVariant(anvil!) == 3, "no recorded choice means the last listed variant (Current)");
            var current = UniqueItemText.Lines(anvil!, 3);
            var oldest = UniqueItemText.Lines(anvil!, 1);
            Assert(current.Count > 0 && oldest.Count > 0, "both variants have modifier lines");
            Assert(current.Any(l => l.Text == "25% increased Block chance") && !current.Any(l => l.Text == "20% increased Block chance"),
                "the Current variant's own block-chance line");
            Assert(!oldest.Any(l => l.Text is "20% increased Block chance" or "25% increased Block chance"),
                "historical variant-exclusive block-chance lines are excluded");
            Assert(UniqueItemText.Resolve("+(30-40) to maximum Life") == "+40 to maximum Life", "a range is shown at its maximum");
            Assert(current[0].Kind == UniqueLineKind.Implicit && current[0].Resolved == "+40 to maximum Life",
                "the declared implicit comes first and is resolved");
            Assert(current.Count(l => l.Kind == UniqueLineKind.Implicit) == anvil!.Implicits, "the implicit count matches the data");
            Assert(current.Skip(1).All(l => l.Kind == UniqueLineKind.Modifier), "everything after the implicit block is explicit");
            var everyLine = UniqueItemText.Lines(anvil!, 3, includeAllVariants: true);
            Assert(everyLine.All(l => l.Variants.Length == 0 || l.Variants.Any(v => !UniqueItemText.IsOldVersion(UniqueItemText.VariantName(anvil!, v)))),
                "including all variants still excludes historical lines");
        }));
        await test("Uniques: a planner-built item's text is the same shape our importer reads", () => Check(() =>
        {
            var morior = catalog.UniqueData.For("Morior Invictus");
            Assert(morior is not null && morior.Variants.Length >= 20, "Morior Invictus carries many variants");
            Assert(UniqueItemText.VariantName(morior!, 1) == morior!.Variants[0], "variant numbers are 1-based over the data's names");
            int chosen = morior.Variants.Length;
            string text = UniqueItemText.Build("Morior Invictus", morior, chosen);
            Assert(text.StartsWith("Rarity: UNIQUE\nMorior Invictus\nGrand Regalia\n", StringComparison.Ordinal),
                "header, name and base type: " + text.Split('\n')[0]);
            var built = UniqueItemText.Lines(morior, chosen);
            var parsed = UniqueItemText.Parse(text);
            Assert(parsed[0].Kind == UniqueLineKind.Name && parsed[1].Kind == UniqueLineKind.BaseType, "name and base type survive");
            Assert(parsed.Where(l => l.Kind is UniqueLineKind.Implicit or UniqueLineKind.Modifier).Select(l => l.Text).SequenceEqual(built.Select(l => l.Resolved)),
                "every modifier line round-trips");
            Assert(UniqueItemText.Build("Morior Invictus", morior, 1) != text, "another variant builds a different item");
        }));
        await test("Uniques: an imported item's own text is read in the game's order", () => Check(() =>
        {
            var lines = UniqueItemText.Parse("Rarity: UNIQUE\nHeadhunter\nHeavy Belt\nImplicits: 1\n+25 to Strength\n50% increased Stun Threshold\n10% increased Movement Speed");
            Assert(lines.Count == 5, "name, base type and three modifiers: " + lines.Count);
            Assert(lines[0].Kind == UniqueLineKind.Name && lines[0].Text == "Headhunter", "the name is the first line");
            Assert(lines[1].Kind == UniqueLineKind.BaseType && lines[1].Text == "Heavy Belt", "the base type is the second");
            Assert(lines[2].Kind == UniqueLineKind.Implicit && lines[2].Text == "+25 to Strength", "the declared implicit comes first");
            Assert(lines[3].Kind == UniqueLineKind.Modifier && lines[4].Kind == UniqueLineKind.Modifier, "the rest are explicit");
            Assert(UniqueItemText.Parse("Rarity: UNIQUE\nX\nY\nQuality: +20%").Last().Kind == UniqueLineKind.Note, "a property stays a property");
            Assert(UniqueItemText.Parse("--------\n").Count == 0 && UniqueItemText.Parse(null).Count == 0, "separators and empty text produce no lines");
        }));
        await test("Uniques: a unique without a pinned base still fits its slot", () => Check(() =>
        {
            // The crash the owner hit: a unique picked in the editor has no catalog base (BaseId is empty),
            // and the plan used to index the base dictionary with that empty id — a KeyNotFoundException out
            // of EquipmentRules.Validate the moment the item was dropped into a slot.
            var morior = new GearItem { Rarity = "unique", Name = "Morior Invictus", ItemLevel = 80, Notes = UniqueItemText.Build("Morior Invictus", catalog.UniqueData.For("Morior Invictus")!, 29) };
            EquipmentRules.ValidateItem(catalog, morior);
            Assert(EquipmentRules.ResolveBase(catalog, morior)?.ItemClass == "Body Armour",
                "PoB2's own base type for the name resolves to the pinned base");
            Assert(EquipmentRules.UniqueBaseType(catalog, morior) == "Grand Regalia", "the base type comes from PoB2's table");
            var plan = EquipmentRules.Put(catalog, new(), morior, "Body");
            Assert(plan.Slots["Body"] == morior.Id, "the body slot accepts it");
            // A unique whose class does not belong in the slot is refused with a rule, not with a crash.
            Plan("PlanSlot", () => EquipmentRules.Put(catalog, new(), new GearItem { Rarity = "unique", Name = "Headhunter", ItemLevel = 80 }, "Boots"));
            // An item that is neither a known base nor a known unique has no class at all: the item rules catch
            // it before the slot does, which is the honest order (a base-less rare is only legal as a jewel).
            Plan("PlanUnknownBase", () => EquipmentRules.Put(catalog, new(), new GearItem { Rarity = "rare", Name = "?" }, "Body"));
        }));
        await test("Uniques: Morior Invictus offers its own personal mods", () => Check(() =>
        {
            var morior = catalog.UniqueData.For("Morior Invictus");
            var live = UniqueItemText.Lines(morior!, UniqueItemText.CurrentVariant(morior!));
            var offered = UniqueItemText.PersonalMods(morior!, live.Select(l => l.Text));
            Assert(live.Count > 0 && offered.Count > live.Count, $"offered {offered.Count}, live {live.Count}");
            Assert(offered.Any(l => l.Resolved == "+14 to Spirit per Socket filled"),
                "the Spirit roll the game can print is reachable from the item's own list");
            Assert(!offered.Any(l => live.Any(liveLine => liveLine.Text == l.Text)), "a line already on the item is not offered twice");
            var spirit = offered.First(l => l.Resolved == "+14 to Spirit per Socket filled");
            Assert(spirit.Variants.Length > 0 && UniqueItemText.VariantLabel(morior!, spirit).Contains("Spirit", StringComparison.Ordinal),
                "an offered line names the variant it belongs to: " + UniqueItemText.VariantLabel(morior!, spirit));
            // Taking everything the list offers (plus the lines the item already carries) leaves nothing: the
            // offer is exactly the data minus what is on the item.
            var leftovers = UniqueItemText.PersonalMods(morior!, live.Concat(offered).Select(l => l.Text));
            Assert(leftovers.Count == 0, "the whole list can be taken, but these stayed: " + string.Join(" | ", leftovers.Select(l => l.Text)));
            Assert(live.Concat(offered).Count() == UniqueItemText.Lines(morior!, 1, includeAllVariants: true).Count,
                "every line of the data is either on the item or offered");
        }));
        await test("Icons: unique art resolves to the packed picture, its base type, or nothing", () => Check(() =>
        {
            Assert(ItemArt.RelativePath("Art/2DItems/Belts/Uniques/Headhunter.dds") == "Items/2DItems/Belts/Uniques/Headhunter.png",
                "a unique art path maps to the packed layout");
            Assert(ItemArt.RelativePath("Art/2DItems/Belts/Headhunter") is null && ItemArt.CdnUri("") is null,
                "a path that is not an Art dds resolves to nothing");
            if (!catalog.BasesByName.TryGetValue("Grand Regalia", out var regalia))
                throw new Exception("the base type a unique names is not in the catalog");
            Assert(regalia.ItemClass == "Body Armour", "the base a unique names is a real base");
            Assert(ItemArt.UniqueRelativePath(catalog, "Headhunter", _ => true) is not null, "a known unique has its own picture");
            // Nothing bundled: the honest fallback is the art of its base type, never an invented picture.
            string? baseArt = ItemArt.RelativePath(regalia.Art);
            Assert(ItemArt.UniqueRelativePath(catalog, "Morior Invictus", path => path == baseArt) == baseArt,
                "an unshipped unique falls back to its base type's art");
            Assert(ItemArt.UniqueRelativePath(catalog, "Not A Real Unique", _ => true) is null, "an unknown name resolves to nothing");
            // The packed manifest is the record of what actually ships: it must exist and be populated.
            string manifestPath = Path.Combine(AppContext.BaseDirectory, "Data", "Icons", "manifest.json");
            Assert(File.Exists(manifestPath), "the icon manifest ships with the tests");
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            int packed = manifest.RootElement.GetProperty("filesSha256").EnumerateObject().Count();
            Assert(packed > 1000, "the packed icon set is recorded: " + packed);
        }));
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
        await test("Equipment: the spawn-tag pools reach affixes the class pool never offered", () => Check(() =>
        {
            // The pinned snapshot rolls LocalArmourAndStunThreshold1 ("Beetle's": increased Armour and Stun
            // Threshold) on str_armour bases, and the catalog's per-class pool for body armour does not
            // contain it — so before the affix table an armour piece simply could not be given the mod the
            // game puts on it. Its kind, level and stats come from the snapshot, not from the catalog.
            Assert(catalog.Affix.Mods.Count > 2500 && catalog.Affix.TagPools.Count > 50, "the affix table is loaded");
            Assert(catalog.Affix.TagPools.ContainsKey("str_armour") && catalog.Affix.TagPools.ContainsKey("body_armour"));
            var strengthPlate = catalog.Bases.Values.First(b => b.ItemClass == "Body Armour" && b.Tags.Contains("str_armour") && b.DropLevel <= 80);
            var classPool = new HashSet<string>(catalog.Data.ModPools[strengthPlate.ModPool], StringComparer.Ordinal);
            var armourAndStun = catalog.ModsFor(strengthPlate, 100).First(m => m.Id == "LocalArmourAndStunThreshold1");
            Assert(!classPool.Contains(armourAndStun.Id), "this str_armour affix was in no class pool before");
            Assert(armourAndStun.Kind == "prefix" && armourAndStun.Level == 10 && armourAndStun.Stats.Length == 2, "kind, level and stats come from the snapshot");
            var item = new GearItem
            {
                BaseId = strengthPlate.Id, Name = "Test Plate", Rarity = "rare", ItemLevel = 80,
                Mods = [MaxRoll(armourAndStun), MaxRoll(catalog.Mods["IncreasedLife1"])]
            };
            EquipmentRules.ValidateItem(catalog, item);
            // Widening the pool invents nothing: every offered affix is a real prefix/suffix of the pinned
            // snapshot, and every one of them is reachable through the base's own class pool or its tags.
            foreach (var b in catalog.Bases.Values.Where(b => !GameCatalog.IsJewel(b)).Take(60))
            {
                var pool = new HashSet<string>(catalog.Data.ModPools[b.ModPool], StringComparer.Ordinal);
                foreach (var mod in catalog.ModsFor(b, 100))
                {
                    Assert(mod.Kind is "prefix" or "suffix" && mod.Level >= 1 && mod.Stats.Length > 0, mod.Id + " is not a real affix");
                    Assert(pool.Contains(mod.Id) || b.Tags.Any(t => catalog.Affix.TagPools.TryGetValue(t, out var tagPool) && tagPool.Contains(mod.Id)),
                        mod.Id + " is offered by no source for " + b.Name);
                }
            }
        }));
        await test("Jewels: real bases, their own affix pool, and PoB2's 2 + 2 rule", () => Check(() =>
        {
            Assert(catalog.JewelBases.Count == 9, "every pinned jewel base is exposed");
            Assert(catalog.JewelBases.All(b => GameCatalog.IsJewel(b) && b.Art.StartsWith("Art/2DItems/Jewels/", StringComparison.Ordinal)), "jewel bases carry their own art path");
            var ruby = catalog.BasesByName["Ruby"];
            var sapphire = catalog.BasesByName["Sapphire"];
            var rubyPool = catalog.ModsFor(ruby, 100).ToArray();
            Assert(rubyPool.Length > 20 && rubyPool.All(m => m.Kind is "prefix" or "suffix"), "a jewel base answers with kinded affixes: " + rubyPool.Length);
            Assert(rubyPool.Any(m => catalog.Affix.TagPools["strjewel"].Contains(m.Id)), "the strength jewel's own pool is reachable");
            Assert(!catalog.ModsFor(sapphire, 100).Any(m => catalog.Affix.TagPools["str_radius_jewel"].Contains(m.Id)), "a Sapphire is not offered a Time-Lost Ruby's radius affix");
            var prefixes = rubyPool.Where(m => m.Kind == "prefix").Take(2).ToArray();
            var suffix = rubyPool.First(m => m.Kind == "suffix");
            var secondSuffix = rubyPool.Where(m => m.Kind == "suffix").Skip(1).First();
            Assert(prefixes.Length == 2, "two prefixes must exist to test the cap");
            GearItem Jewel(params ItemMod[] mods) => new()
            { BaseId = ruby.Id, Name = "Test Ruby", Rarity = "rare", ItemLevel = 80, Mods = [.. mods.Select(MaxRoll)] };
            EquipmentRules.ValidateItem(catalog, Jewel(prefixes[0], prefixes[1], suffix, secondSuffix));
            // PoB2's own jewel rule is 2 + 2 (Classes/Item.lua), so a third prefix of the same kind fails.
            var threePrefixes = rubyPool.Where(m => m.Kind == "prefix").Take(3).ToArray();
            Plan("PlanAffixCap", () => EquipmentRules.ValidateItem(catalog, Jewel(threePrefixes[0], threePrefixes[1], threePrefixes[2])));
            Plan("PlanAffixCap", () => EquipmentRules.ValidateItem(catalog, Jewel(prefixes[0], prefixes[1], suffix, secondSuffix) with { Rarity = "magic" }));
            // A jewel fits no equipment slot.
            Assert(!EquipmentRules.Fits("Ring1", ruby) && !EquipmentRules.Fits("Main1", ruby) && !EquipmentRules.Fits("Off1", ruby));
            Assert(EquipmentRules.AffixCaps(ruby) == (2, 2) && EquipmentRules.AffixCaps(body) == (3, 3), "jewel 2 + 2, everything else 3 + 3");
            // A jewel's affixes must come from its own base: an armour prefix is not a jewel affix.
            Plan("PlanModInvalid", () => EquipmentRules.ValidateItem(catalog, Jewel(prefixes[0], catalog.Mods[prefix.Id])));
        }));
        await test("Icons: every jewel picture is bundled under the layout the catalog names", () => Check(() =>
        {
            var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "Icons", "manifest.json")));
            var files = new HashSet<string>(manifest.RootElement.GetProperty("filesSha256").EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
            foreach (var jewel in catalog.JewelBases)
                Assert(files.Contains(ItemArt.RelativePath(jewel.Art)!), "jewel art not bundled: " + jewel.Art);
            // A unique item is shown by its own art; where the pinned mirror does not carry a sprite (13 of
            // 430 paths) the honest fallback is the art of its base type, which is bundled too.
            var uniques = catalog.Uniques.Values.ToArray();
            int own = uniques.Count(u => ItemArt.RelativePath(u.Icon) is { } rel && files.Contains(rel));
            Assert(own > 430, "the unique sprites are bundled: " + own + " of " + uniques.Length);
            // A supplemented identity ("Hands of Wisdom and Action") is one the pinned export and PoB2's own
            // data both miss: neither its own art nor a base type is pinned, so an empty slot is the honest
            // state. Everything the pinned tables do know must resolve to a bundled picture.
            var unresolved = uniques
                .Where(u => !(ItemArt.RelativePath(u.Icon) is { } rel && files.Contains(rel)) && ItemArt.UniqueRelativePath(catalog, u.Name, files.Contains) is null)
                .Select(u => u.Name).Distinct().ToArray();
            Assert(unresolved.All(name => name == "Hands of Wisdom and Action"), "no bundled picture for: " + string.Join(", ", unresolved));
            Assert(files.Count > 1790, "the pack grew by the jewel and unique sprites: " + files.Count);
        }));
    }
}
