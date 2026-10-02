using PoeBuilder.Core.Equipment;

/// <summary>Guards the "open an imported item" path: a stored roll's id may legally come from the base
/// pool, the jewel pool, or be a synthetic stat-id fallback, and resolving it to the mod it was rolled
/// from must never throw or drop the modifier. <see cref="ItemModResolver"/> is what the item editor and
/// the equipment tooltip use, so this is where the regression is pinned.</summary>
internal static class ItemViewTests
{
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }

    private static readonly Lazy<GameCatalog> Catalog = new(() => GameCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "catalog.json")));

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("ItemView: every jewel-pool id is reachable through AllMods", () => Task.Run(() =>
        {
            var catalog = Catalog.Value;
            Assert(catalog.JewelMods.Count > 0, "catalog carries jewel mods");
            foreach (var jewel in catalog.JewelMods)
                Assert(catalog.AllMods.ContainsKey(jewel.Id), "jewel mod id should resolve: " + jewel.Id);
        }));

        await test("ItemView: a base-affix roll resolves to its pinned mod, never null", () => Task.Run(() =>
        {
            var catalog = Catalog.Value;
            var baseMod = catalog.Mods.Values.FirstOrDefault(m => m.Stats.Length > 0) ?? throw new Exception("no base mod");
            var roll = new ModRoll { Id = baseMod.Id, Values = baseMod.Stats.Select(s => s.Max).ToArray() };
            var resolved = ItemModResolver.For(catalog, roll, out bool isReal);
            Assert(isReal, "base id is a real mod");
            Assert(ReferenceEquals(resolved, baseMod), "base roll maps to its own pinned mod");
        }));

        await test("ItemView: a jewel-pool roll resolves instead of throwing (was catalog.Mods[roll.Id])", () => Task.Run(() =>
        {
            var catalog = Catalog.Value;
            var jewel = catalog.JewelMods.First(m => m.Stats.Length > 0) ?? throw new Exception("no jewel mod");
            var roll = new ModRoll { Id = jewel.Id, Values = jewel.Stats.Select(s => s.Max).ToArray() };
            var resolved = ItemModResolver.For(catalog, roll, out bool isReal);
            Assert(isReal, "jewel id is real (AllMods includes the jewel pool)");
            Assert(resolved.Id == jewel.Id && resolved.Text == jewel.Text, "jewel roll maps to its jewel mod");
        }));

        await test("ItemView: a synthetic stat-id roll stays visible with its values, never null", () => Task.Run(() =>
        {
            var catalog = Catalog.Value;
            string syntheticId = "local_added_fire_damage_to_attacks";
            Assert(!catalog.AllMods.ContainsKey(syntheticId), "test id is genuinely not a pinned mod");
            var roll = new ModRoll { Id = syntheticId, Values = [42m] };
            var resolved = ItemModResolver.For(catalog, roll, out bool isReal);
            Assert(!isReal, "synthetic id is reported as unresolved");
            Assert(resolved.Id == syntheticId, "synthetic keeps the stored id");
            Assert(ItemModResolver.Readable(syntheticId) == "local added fire damage to attacks", "readable stand-in text");
            Assert(resolved.Stats.Length == 1 && resolved.Stats[0].Min == 42m, "synthetic mirrors the roll value");
        }));

        await test("ItemView: no stored roll (real or synthetic) makes resolution fail closed", () => Task.Run(() =>
        {
            var catalog = Catalog.Value;
            var rolls = new List<ModRoll>();
            rolls.AddRange(catalog.Mods.Values.Where(m => m.Stats.Length > 0).Take(40).Select(m => new ModRoll { Id = m.Id, Values = m.Stats.Select(s => s.Max).ToArray() }));
            rolls.AddRange(catalog.JewelMods.Where(m => m.Stats.Length > 0).Take(40).Select(m => new ModRoll { Id = m.Id, Values = m.Stats.Select(s => s.Max).ToArray() }));
            rolls.Add(new ModRoll { Id = "synthetic_stat_" + Guid.NewGuid().ToString("N"), Values = [7m] });
            foreach (var roll in rolls)
            {
                var resolved = ItemModResolver.For(catalog, roll);
                Assert(resolved is not null && resolved.Id == roll.Id, "resolution always yields a matching mod");
            }
        }));
        await test("ItemView: a base's own implicit comes from the pinned extraction with its values", () => Task.Run(() =>
        {
            var catalog = Catalog.Value;
            var golden = catalog.Bases.Values.FirstOrDefault(b => b.Name == "Golden Blade")
                ?? throw new Exception("Golden Blade base not found");
            Assert(golden.Implicits.Contains("+(16-24) to all Attributes"), "extracted implicit text reaches the base");
            Assert(golden.ImplicitStats.Any(s => s.Id == "additional_all_attributes" && s.Min == 16m && s.Max == 24m),
                "extracted implicit stat range reaches the base");
            Assert(catalog.Affix.BaseImplicits.Count == 462, "the pinned extraction carries 462 bases with implicits: " + catalog.Affix.BaseImplicits.Count);
            // Every extracted entry has merged into its base: the editor's tooltip is fed from the same
            // b.Implicits array the merge filled, so a known implicit is exactly one verified source.
            Assert(golden.Implicits.Length == catalog.Affix.BaseImplicits[golden.Id].Length, "merged implicit count matches the extraction");
        }));
        await test("ItemView: hidden weapon implicits keep their stats and no fake display text", () => Task.Run(() =>
        {
            var catalog = Catalog.Value;
            // Energy Blade's lightning damage is a *hidden* weapon implicit: mods.json carries its stat but
            // an empty text array, and PoB2's own base data renders no implicit line for it either (its
            // implicitModTypes are empty). The game does not display it, so the extraction must NOT invent
            // text — and the merge must preserve the stat regardless. This locks in that behaviour so a
            // later "coverage" pass cannot inject wrong text from a by-name source.
            var energy = catalog.Bases.Values.First(b => b.Id == "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/StormBladeOneHand");
            Assert(energy.Implicits.Length == 0, "a hidden implicit stays display-empty");
            Assert(!catalog.Affix.BaseImplicits.ContainsKey(energy.Id), "a hidden implicit is not in the display extraction");
            Assert(energy.ImplicitStats.Any(s => s.Id == "local_minimum_added_lightning_damage"),
                "the hidden implicit's stat survives the merge");
            // And every line the extraction does carry must be a real display line, never a stat without text.
            Assert(catalog.Affix.BaseImplicits.Values.All(lines => lines.All(l => !string.IsNullOrEmpty(l.Text))),
                "every extracted implicit has display text");
        }));
    }
}
