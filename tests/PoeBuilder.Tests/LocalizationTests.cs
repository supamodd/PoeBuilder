using System.Text.RegularExpressions;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Filters;
using PoeBuilder.Core.Localization;

/// <summary>The loot-filter writer, reader and validator: a filter the game would refuse must never reach a
/// save or an export, and a file the game accepts must survive a round trip through the editor.</summary>
public static class FilterTests
{
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static LootFilterDocument Sample() => LootFilterDocument.Create("Тест") with
    {
        Blocks =
        [
            new()
            {
                Kind = FilterBlockKind.Show, Comment = "Валюта",
                Conditions = [new() { Keyword = "ItemClass", Value = "Currency" }],
                Style = new() { FontColor = [170, 230, 110, 255], Sound = "IdRes3", FontSize = 45 }
            },
            new()
            {
                Kind = FilterBlockKind.Hide, Comment = "Мусор",
                Conditions = [new() { Keyword = "Rarity", Value = "Normal" }, new() { Keyword = "DropLevel", IsRange = true, Min = 0, Max = 10 }]
            }
        ]
    };

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Filter: the writer produces the lines the game parses", () => Task.Run(() =>
        {
            var text = FilterWriter.Write(Sample());
            Assert(text.Contains("Show # Валюта"), "the block header and its comment are missing:\n" + text);
            Assert(text.Contains("    ItemClass \"Currency\""), "a textual condition must be quoted and indented");
            Assert(text.Contains("    FontColor 170 230 110 255"), "a colour must be written as four numbers");
            Assert(text.Contains("    Sound \"IdRes3\""), "the sound must be quoted, as the game's own files write it");
            Assert(text.Contains("Hide # Мусор"), "a hide block must start with Hide");
            Assert(text.Contains("    DropLevel 0 10"), "a range must be written as two figures");
        }));

        await test("Filter: what the writer writes, the reader reads back", () => Task.Run(() =>
        {
            var read = FilterReader.Read(FilterWriter.Write(Sample()).Split('\n'));
            Assert(read.Blocks.Count == 2, "the two blocks did not survive, but there were " + read.Blocks.Count);
            var show = read.Blocks[0];
            Assert(show.Kind == FilterBlockKind.Show && show.Comment == "Валюта", "the first block came back wrong");
            Assert(show.Conditions[0] is { Keyword: "ItemClass", Value: "Currency" }, "the condition came back wrong");
            Assert(show.Style.FontColor is [170, 230, 110, 255], "the colour came back wrong");
            Assert(show.Style.Sound == "IdRes3", "the sound came back wrong");
            Assert(show.Style.FontSize == 45, "the font size came back wrong");
            var hide = read.Blocks[1];
            Assert(hide.Kind == FilterBlockKind.Hide, "the hide block came back as a show");
            Assert(hide.Conditions.Any(c => c.Keyword == "DropLevel" && c is { IsRange: true, Min: 0, Max: 10 }),
                "the range came back wrong");
            Assert(read.Unknown.Count == 0, "the reader reported lines it wrote itself: " + string.Join("; ", read.Unknown));
        }));

        await test("Filter: a line the reader cannot place is reported, never dropped", () => Task.Run(() =>
        {
            // A popular third-party filter carries fields this editor has no field for. The file must open, and
            // the player must be told what will be lost rather than finding out at the next save.
            var read = FilterReader.Read(
            [
                "Show # Unknown fields",
                "    ItemClass \"Currency\"",
                "    SomeFieldWeDoNotKnow 42",
                "Hide",
                "    Rarity \"Normal\""
            ]);
            Assert(read.Blocks.Count == 2, "an unfamiliar field cost the player a block");
            Assert(read.Unknown.Contains("SomeFieldWeDoNotKnow 42"), "the unfamiliar line was not reported");
        }));

        await test("Filter: a rule the game would refuse is refused here too", () => Task.Run(() =>
        {
            static bool Refuses(LootFilterDocument filter)
            {
                try { FilterWriter.Write(filter); return false; }
                catch (FilterFormatException) { return true; }
            }
            // Each of these is a file the client rejects outright, so catching it here is the only place the
            // player can be told what to fix.
            Assert(Refuses(Sample() with { Blocks = [new() { Kind = FilterBlockKind.Show }] }),
                "a Show block with no condition would style every item and must be refused");
            Assert(Refuses(Sample() with { Blocks = [new() { Conditions = [new() { Keyword = "NotACondition", Value = "x" }] }] }),
                "a keyword the game does not know would make it reject the whole file");
            Assert(Refuses(Sample() with { Blocks = [new() { Conditions = [new() { Keyword = "Rarity", Value = "Legendary" }] }] }),
                "a rarity the game does not know must be refused");
            Assert(Refuses(Sample() with { Blocks = [new() { Conditions = [new() { Keyword = "ItemClass" }] }] }),
                "a condition with no value must be refused");
            Assert(Refuses(Sample() with { Blocks = [new() {
                Conditions = [new() { Keyword = "ItemClass", Value = "Currency" }],
                Style = new() { Sound = "NotAGameSound" } }] }),
                "a sound the game does not ship would make it refuse the file");
            Assert(Refuses(Sample() with { Blocks = [new() {
                Kind = FilterBlockKind.Hide,
                Conditions = [new() { Keyword = "ItemClass", Value = "Currency" }],
                Style = new() { FontColor = [1, 2, 3, 255] } }] }),
                "a Hide block carries no style, so a styled one is a file the game will not load");
            Assert(Refuses(Sample() with { Blocks = [new() {
                Conditions = [new() { Keyword = "ItemClass", Value = "Currency" }],
                Style = new() { FontSize = 33 } }] }),
                "a font size outside the game's own list must be refused");
        }));

        await test("Filter: every preset is a file the game can load", () => Task.Run(() =>
        {
            foreach (var preset in FilterPresets.All)
            {
                var document = preset.Build();
                Assert(document.Blocks.Count > 0, $"{preset.Id} offers no rules at all");
                // Read a preset back the way the game would: every line must be one this writer can place.
                Assert(FilterReader.Read(FilterWriter.Write(document).Split('\n')).Unknown.Count == 0,
                    $"{preset.Id} wrote a line the reader cannot place");
            }
        }));

        await test("Filter: a filter round-trips through the library on disk", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "pbe-filters-" + Guid.NewGuid().ToString("N"));
            try
            {
                var repository = new LootFilterRepository(root);
                var saved = await repository.SaveAsync(Sample());
                var (library, unreadable) = await repository.ReadLibraryAsync();
                Assert(library.Count == 1 && unreadable.Count == 0, "the saved filter did not come back");
                var again = await repository.SaveAsync(saved with { Name = "Переименованный" });
                Assert(again.UpdatedUtc >= saved.UpdatedUtc, "a re-save must move the update time forward");
                await repository.MoveToTrashAsync(again.Id);
                Assert((await repository.ReadLibraryAsync()).Filters.Count == 0, "the deleted filter is still listed");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }
}

/// <summary>The RegEx generator: every pattern it builds is matched back against the rolled line it is meant
/// to find, because a search string that matches nothing is worse than no string at all.</summary>
public static class RegexTests
{
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    /// <summary>What the game actually writes on an item, for a template that rolls as this text.</summary>
    private static bool Matches(string pattern, string rolled) => Regex.IsMatch(rolled, pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("RegEx: the generated pattern matches the rolled line, not the template", () => Task.Run(() =>
        {
            // The pinned template carries the generator's own range notation; the item carries a rolled line.
            var pattern = ItemFilterRegex.TextPattern("Adds 1 to (2-3) Cold damage to Attacks");
            Assert(pattern == @"Adds \d+ to \d+ Cold damage to Attacks", "unexpected pattern: " + pattern);
            Assert(Matches(pattern, "Adds 8 to 12 Cold damage to Attacks"), "a rolled line must match");
            Assert(!Matches(pattern, "Adds Fire damage to Attacks"), "a different element must not match");
            Assert(!Matches(pattern, "Adds 8 to 12 Lightning damage to Attacks"), "a different damage type must not match");
        }));

        await test("RegEx: a numeric bound narrows the first number only", () => Task.Run(() =>
        {
            var pattern = ItemFilterRegex.TextPattern("Adds 1 to (2-3) Cold damage to Attacks", 10m, 20m);
            Assert(Matches(pattern, "Adds 12 to 15 Cold damage to Attacks"), "a value inside the bound matches");
            Assert(!Matches(pattern, "Adds 5 to 15 Cold damage to Attacks"), "a value below the bound must not match");
            Assert(!Matches(pattern, "Adds 30 to 40 Cold damage to Attacks"), "a value above the bound must not match");
            // The upper figure is left open: the player bounded one number, and narrowing a second one they
            // never mentioned would silently drop items.
            Assert(Matches(pattern, "Adds 12 to 99 Cold damage to Attacks"), "the second number stays open");
        }));

        await test("RegEx: a bound the player cannot type narrows nothing instead of breaking", () => Task.Run(() =>
        {
            Assert(ItemFilterRegex.NumberPattern(null, null) == ItemFilterRegex.Any, "no bound leaves the number open");
            Assert(ItemFilterRegex.NumberPattern(20m, 10m) == ItemFilterRegex.NumberPattern(10m, 20m),
                "reversed bounds are read the same way round");
            Assert(ItemFilterRegex.NumberPattern(1.5m, null) == ItemFilterRegex.Any, "a fractional bound is ignored, not misapplied");
        }));

        await test("RegEx: Russian е and ё both match, and the fold never breaks the pattern", () => Task.Run(() =>
        {
            var pattern = ItemFilterRegex.TextPattern("+(10-20)% к сопротивлению");
            Assert(pattern.Contains("[её]"), "the ё pair must be folded: " + pattern);
            Assert(pattern.Contains(@"\d+"), "the range must have become a number matcher: " + pattern);
            Assert(Matches(pattern, "+15% к сопротивлению"), "the plain е spelling matches");
            Assert(Matches(pattern, "+15% к сопротивлёнию"), "the ё spelling matches too");
            Assert(!Matches(pattern, "+15% к маны"), "a different word must not match");
            // Matching the pattern against itself is meaningless once it holds a class — the literal text of the
            // class is not one of its members. Compiling it is the check that matters: a fold that produced an
            // unbalanced bracket would throw right here.
            _ = new Regex(pattern);
        }));

        await test("RegEx: logic combines the picked modifiers the way the tab says", () => Task.Run(() =>
        {
            var clauses = new[]
            {
                new RegexClause("Adds # to Fire damage", null, null, RegexRequirement.Required),
                new RegexClause("+#% to Fire Resistance", null, null, RegexRequirement.Optional),
                new RegexClause("+#% to Cold Resistance", null, null, RegexRequirement.Excluded)
            };
            var all = ItemFilterRegex.Build(clauses, RegexLogic.All).Text;
            Assert(!all.Contains(','), "\"All\" is a space-separated AND, but was: " + all);
            // A "!" is a negation in every mode, not an alternative, so it survives "All" as well.
            Assert(all.Contains("!"), "the exclusion survives \"All\", but was: " + all);

            var any = ItemFilterRegex.Build(clauses, RegexLogic.Any).Text;
            Assert(any.Contains(','), "\"Any\" is an OR, but was: " + any);

            var mixed = ItemFilterRegex.Build(clauses, RegexLogic.Mixed).Text;
            Assert(mixed.StartsWith("Adds "), "\"Mixed\" leads with the required modifier, but was: " + mixed);
            Assert(mixed.Contains('(') && mixed.Contains(')'), "the optional modifier is grouped, but was: " + mixed);
            Assert(mixed.Contains("!"), "the exclusion survives, but was: " + mixed);
        }));

        await test("RegEx: the character limit is reported rather than silently overrun", () => Task.Run(() =>
        {
            var empty = ItemFilterRegex.Build([], RegexLogic.All);
            Assert(empty.Text.Length == 0 && empty.Notes.Contains("Empty"),
                "nothing picked says so instead of producing a junk string");
            var huge = new string('a', 200) + " to " + new string('b', 200);
            Assert(ItemFilterRegex.TextPattern(huge).Length > ItemFilterRegex.CharacterLimit, "the probe is over the limit");
            var result = ItemFilterRegex.Build([new RegexClause(huge, null, null, RegexRequirement.Required)], RegexLogic.All);
            Assert(result.Overflow && result.Notes.Contains("Overflow"), "an over-long string is flagged, but was " + result.Length);
            Assert(result.Limit == 250, "the limit the game enforces is 250");
        }));

        await test("RegEx: a template's own parentheses never leave the string unbalanced", () => Task.Run(() =>
        {
            foreach (var template in new[]
                     {
                         "Adds (10-20) to (30-40) Fire damage to Attacks",
                         "+#% to Fire Resistance (Local)",
                         "(A) 10 to 20 Physical Damage"
                     })
            {
                var pattern = ItemFilterRegex.TextPattern(template);
                Assert(pattern.Count(c => c == '(') == pattern.Count(c => c == ')'),
                    $"unbalanced parentheses from \"{template}\": {pattern}");
            }
        }));
    }
}

/// <summary>Russian game text: joining by identity, falling back to English instead of inventing, and
/// searching both languages at once.</summary>
public static class LocalizationTests
{
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static async Task Run(Func<string, Func<Task>, Task> test)
    {
        await test("Locale: an English name and its Russian form join on the normalised name", () => Task.Run(() =>
        {
            // Written the way the extractor writes it: keys are already normalised.
            var file = Path.Combine(Path.GetTempPath(), "pbe-locale-join.json");
            File.WriteAllText(file, """
                {"provenance":{"source":"test"},
                 "tree":{"25092":{"name":"Бонус к крит. урону на вас","stats":["12% уменьшение [CriticalDamageBonus|X] от ударов по вам"]}},
                 "names":{"oracle":{"en":"Oracle","ru":"Оракул"},
                          "ancestorscry":{"en":"Ancestral Cry","ru":"Крик предков"}}}
                """);
            var s = GameStrings.Load(file);
            Assert(s.Name("Oracle", true) == "Оракул", "exact English name resolves");
            // Punctuation and case differ between the two spellings; the key must fold them together.
            Assert(s.Name("ancestral cry", true) == "Крик предков", "a differently punctuated spelling still resolves");
            Assert(s.Name("Oracle", false) == "Oracle", "an English interface keeps the English name");
            Assert(s.Name("Not Translated At All", true) == "Not Translated At All", "a missing translation falls back to English, never to invented text");
            Assert(s.Node("25092")?.Name == "Бонус к крит. урону на вас", "tree text joins on the node id");
            Assert(s.Node("25092")?.Stats?[0].Contains("[CriticalDamageBonus|") == true, "the stat id survives translation");
            Assert(s.Node("does-not-exist") is null, "an unknown node id yields nothing rather than a guess");
            File.Delete(file);
        }));

        await test("Locale: search matches Russian and English at the same time", () => Task.Run(() =>
        {
            var file = Path.Combine(Path.GetTempPath(), "pbe-locale-search.json");
            File.WriteAllText(file, """
                {"tree":{},"names":{"oracle":{"en":"Oracle","ru":"Оракул"},"meteor":{"en":"Meteor","ru":"Метеор"}}}
                """);
            var s = GameStrings.Load(file);
            // A Russian player searching the displayed text.
            Assert(s.Name("Oracle", true).Contains("Ораку", StringComparison.OrdinalIgnoreCase), "Russian query matches");
            // An English player searching the same row while it displays Russian: the underlying English
            // name is what makes this work, and it is why both are searched rather than only the shown one.
            Assert(s.Name("Oracle", true).Contains("Oracle", StringComparison.OrdinalIgnoreCase) == false, "the shown text alone would miss an English query");
            Assert(s.Name("Oracle", false) == "Oracle", "reading the stored English name is what an English search uses");
            File.Delete(file);
        }));

        await test("Locale: a missing locale file is empty, not broken", () => Task.Run(() =>
        {
            var s = GameStrings.Load(Path.Combine(Path.GetTempPath(), "pbe-locale-does-not-exist.json"));
            Assert(s.IsEmpty, "a missing file yields an empty set");
            Assert(s.Name("Oracle", true) == "Oracle", "and every name falls back to English");
        }));

        await test("Locale: a translated stat line is wholly one language, never a spliced hybrid", () => Task.Run(() =>
        {
            // The owner's screenshot showed "[Allies|Союзники] in your [Presence|присутствии] have 6%
            // increased [Attack|атаки] Speed": the placeholder texts were translated while the English
            // sentence around them stayed. A real locale must contain no Latin words outside the ids.
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "Game", "locale-ru.json");
            Assert(File.Exists(path), "locale-ru.json is missing - run build/extract-poe2db-ru.ps1");
            var s = GameStrings.Load(path);
            Assert(!s.IsEmpty, "locale-ru.json is empty");

            int checkedLines = 0, hybrids = 0;
            string? firstHybrid = null;
            foreach (var id in new[] { "25092", "11335", "42761", "35715" })
            {
                var node = s.Node(id);
                if (node?.Stats is null) continue;
                foreach (var line in node.Stats)
                {
                    // The [StatId|Text] placeholders are ids and are meant to be Latin; strip them first.
                    var bare = System.Text.RegularExpressions.Regex.Replace(line, @"\[[^\]]*\]", "");
                    if (System.Text.RegularExpressions.Regex.IsMatch(bare, @"[A-Za-z]{4,}"))
                    {
                        hybrids++;
                        firstHybrid ??= id + ": " + line;
                    }
                    checkedLines++;
                }
            }
            Assert(checkedLines > 0, "no translated stat lines were found to check");
            Assert(hybrids == 0, hybrids + " translated lines still contain English words (e.g. " + firstHybrid + ")");
        }));
    }
}