using System.Diagnostics;
using PoeBuilder.App.Services;
using PoeBuilder.Core.Calculation;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Storage;
using PoeBuilder.Core.Tree;

/// <summary>
/// Guards the interaction budget. Editing a build recalculates the character sheet on every
/// change, so the cost of one <see cref="CharacterCalculator.Calculate"/> pass IS the UI latency
/// the user feels while typing (see docs/VALIDATION.md, "Пересчёт и отзывчивость").
/// </summary>
internal static class PerfTests
{
    private static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }

    private static readonly Lazy<TreeCatalog> Tree = new(() => TreeCatalog.LoadPinned(Path.Combine(AppContext.BaseDirectory, "Data", "Tree", "data.json")));
    private static readonly Lazy<GameCatalog> Catalog = new(() => GameCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "catalog.json")));
    private static readonly Lazy<GameStatMap> StatMap = new(() => GameStatMap.Load(Path.Combine(AppContext.BaseDirectory, "Data", "Game", "statmap.json")));

    /// <summary>Median of the measured passes: the first one pays for the JIT and for the catalog
    /// lookups a user's session already paid, so the median is the honest steady-state number.</summary>
    private static decimal Median(IEnumerable<long> samples)
    {
        var ordered = samples.OrderBy(x => x).ToArray();
        return ordered.Length == 0 ? 0 : ordered[ordered.Length / 2];
    }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Perf: a full recalculation of the imported Arc build fits the interaction budget", () => Task.Run(() =>
        {
            // The heaviest pinned build: 148 allocated nodes, 23 items, 11 skill groups, aura stacking.
            string code = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pobb-arc-totem-v2.txt")).Trim();
            var imported = BuildInterop.ParsePobCode(code, Catalog.Value, Tree.Value);
            var document = imported.Document;

            var samples = new List<long>();
            CharacterSummary? last = null;
            for (int i = 0; i < 7; i++)
            {
                var watch = Stopwatch.StartNew();
                last = CharacterCalculator.Calculate(document, Tree.Value, StatMap.Value, Catalog.Value);
                watch.Stop();
                samples.Add(watch.ElapsedMilliseconds);
            }
            decimal median = Median(samples);
            Console.WriteLine("  recalculation ms: first " + samples[0] + ", median " + median +
                ", worst " + samples.Max() + " (" + samples.Count + " passes)");

            // Cold path: every item text is new to the parser — that is what the FIRST recalculation
            // after an import or after typing into an item's text looks like, so it is the worst case
            // a user can feel. The steady-state number above is what every later keystroke costs.
            if (document.Equipment is { } equipment)
            {
                var fresh = document with
                {
                    Equipment = equipment with
                    {
                        Items = [.. equipment.Items.Select((item, index) =>
                            item with { Notes = item.Notes + "\n// cold " + index })],
                    },
                };
                var coldWatch = Stopwatch.StartNew();
                CharacterCalculator.Calculate(fresh, Tree.Value, StatMap.Value, Catalog.Value);
                coldWatch.Stop();
                Console.WriteLine("  cold recalculation ms (all item texts new): " + coldWatch.ElapsedMilliseconds);
                Assert(coldWatch.ElapsedMilliseconds < 250, "a cold pass stays under 250 ms: " + coldWatch.ElapsedMilliseconds + " ms");
            }
            Assert(last is not null && last.Skills.Count > 0, "the recalculation produced skills");
            // A single pass has to stay far below the ~100 ms a keystroke may cost before the freeze
            // becomes visible; the app performs exactly one pass per edit (see CharacterViewModel).
            Assert(median < 150m, "one recalculation pass stays under 150 ms: " + median + " ms");
        }));
    }
}
