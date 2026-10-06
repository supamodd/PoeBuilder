using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PoeBuilder.App.ViewModels;
using PoeBuilder.App.Views;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;
using PoeBuilder.Core.Skills;

namespace PoeBuilder.App.Services;

/// <summary>
/// Hidden developer switch (<c>PoeBuilder.exe --check-windows</c>): builds the editor windows against the
/// pinned data, lays them out off-screen and exercises the drafts behind them, then prints one line per
/// check and returns a non-zero exit code if any of them failed.
/// <para>
/// This exists because the test suite runs on plain .NET without WPF: it cannot construct a view model
/// that carries brushes or images, and it cannot see a XAML mistake at all. Without this switch a broken
/// template is found by the user's first click on the very tab that carries it.
/// </para>
/// </summary>
public static class WindowSelfCheck
{
    public const string Switch = "--check-windows";
    public static bool Requested(string[] args) => args.Contains(Switch);

    public static async Task<int> RunAsync()
    {
        var shell = new MainViewModel();
        await shell.InitializeAsync();
        if (shell.Catalog is null) { Console.Error.WriteLine("catalog missing: " + shell.Status); return 1; }
        var catalog = shell.Catalog;
        var L = shell.L;
        int failed = 0;

        void Report(string name, Action action)
        {
            try { action(); Console.WriteLine("OK   " + name); }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + ": " + e.Message);
                ErrorLog.Append(e, "window-check");
            }
            Console.Out.Flush();
        }
        void Layout(string name, Func<FrameworkElement> build) => Report(name, () =>
        {
            var element = build();
            if (element is not Window window)
            {
                element.Measure(new Size(1280, 800)); element.Arrange(new Rect(0, 0, 1280, 800)); element.UpdateLayout();
                return;
            }
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowInTaskbar = false; window.Left = -32000; window.Top = -32000;
            window.Show(); window.UpdateLayout();
            // A clean draft's window closes right away. One carrying unsaved edits stays open: closing it
            // would raise the editor's own "save this draft?" question, and a scripted run has nobody to
            // answer a modal dialog on a hidden desktop. The process ends without a graceful shutdown, so
            // nothing is left behind.
            if (window.DataContext is ItemDraftViewModel dirty && dirty.IsDirty && !dirty.Accepted)
                Console.WriteLine("     (kept open: its draft has unsaved edits)");
            else window.Close();
        });
        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        // A draft plus the item it committed, so the save path (which is where the equipment rules live) is
        // exercised instead of only the layout.
        (ItemDraftViewModel Draft, Func<GearItem?> Item) NewDraft(string? unique = null, GearItem? item = null, string slot = "Body")
        {
            GearItem? committed = null;
            var draft = new ItemDraftViewModel(L, catalog, item, slot, saved => committed = saved);
            if (unique is not null) draft.SelectedUnique = unique;
            return (draft, () => committed);
        }
        GearItem Save(ItemDraftViewModel draft, Func<GearItem?> item)
        {
            draft.SaveCommand.Execute(null);
            if (!draft.Accepted) throw new Exception("the draft refused to save: " + draft.Error);
            var saved = item() ?? throw new Exception("the draft accepted but committed nothing");
            EquipmentRules.ValidateItem(catalog, saved);
            return saved;
        }

        // Item editor: a rare item built from the pool, a unique with its variants, an imported unique
        // whose only source of truth is its own text.
        var (rareDraft, rareItem) = NewDraft();
        Layout("ItemEditorWindow (rare)", () => new ItemEditorWindow(rareDraft));
        Report("ItemDraft: a rare item saves through the equipment rules", () => Assert(
            Save(rareDraft, rareItem).BaseId.Length > 0, "a base is chosen for a new rare item"));

        var (unique, uniqueItem) = NewDraft("Morior Invictus");
        Report("ItemDraft: a unique arrives with its variant, its lines and its icon", () => Assert(
            unique.HasUniqueData && unique.UniqueLines.Count > 0 && unique.Notes.StartsWith("Rarity: UNIQUE\nMorior Invictus\n", StringComparison.Ordinal),
            "unique data, lines and text: lines=" + unique.UniqueLines.Count + " notes=" + unique.Notes.Length));
        Report("ItemDraft: its variants are selectable and only the chosen one applies", () => Assert(
            unique.HasUniqueVariants && unique.UniqueVariantChoices.Count == unique.UniqueVariantChoices.Select(c => c.Name).Distinct().Count(),
            "variant choices: " + unique.UniqueVariantChoices.Count));
        Layout("ItemEditorWindow (unique)", () => new ItemEditorWindow(unique));
        Report("ItemDraft: a unique offers its own personal mods and can take one", () =>
        {
            // The other half of the owner's report: with a unique selected the "available mods" list was empty,
            // because a unique carries no catalog affixes. Its own lines fill it now — across every variant
            // PoB2 lists — so "+10 to Spirit per Socket filled" can be put on the item.
            Assert(unique.HasUniqueData && unique.UniqueLineOptions.Count > 0,
                "the personal mod list is filled: " + unique.UniqueLineOptions.Count);
            var spirit = unique.UniqueLineOptions.FirstOrDefault(o => o.Resolved == "+14 to Spirit per Socket filled")
                ?? throw new Exception("the Spirit line of Morior Invictus is not offered: " + string.Join(" | ", unique.UniqueLineOptions.Take(3).Select(o => o.Resolved)));
            Assert(spirit.Variant.Contains("Spirit", StringComparison.Ordinal), "the offered line names its variant: " + spirit.Variant);
            unique.SelectedUniqueLineOption = spirit;
            int before = unique.UniqueLines.Count;
            unique.AddUniqueLineCommand.Execute(null);
            Assert(unique.UniqueLines.Count == before + 1, "the line is on the item");
            Assert(unique.Notes.Contains("+14 to Spirit per Socket filled", StringComparison.Ordinal), "and in the item's text");
            Assert(unique.UniqueLineOptions.All(o => o.Resolved != spirit.Resolved), "a taken line leaves the offer list");
            unique.UniqueLines[^1].RemoveCommand.Execute(null);
            Assert(unique.UniqueLines.Count == before && !unique.Notes.Contains("+14 to Spirit per Socket filled", StringComparison.Ordinal),
                "taking it off the item puts it back in the list and out of the text");
        });
        Report("ItemDraft: another variant rebuilds the lines and the item text", () =>
        {
            string before = unique.Notes;
            unique.SelectedUniqueVariant = unique.UniqueVariantChoices[0];
            unique.ShowAllVariants = true;
            Assert(unique.Notes != before && unique.UniqueLines.Count > 0, "variant 1 gives a different item");
            Assert(unique.UniqueLines.Any(l => l.HasMarker), "the lines of other variants are marked as such");
            Assert(unique.Preview.Count > 2 && unique.Preview[0].Bold, "the tooltip preview is built from the lines");
        });
        Report("ItemDraft: the edited unique saves as the item it shows", () =>
        {
            var saved = Save(unique, uniqueItem);
            Assert(saved.Rarity == "unique" && saved.Mods.Length == 0 && saved.Notes.Length > 0,
                "a unique carries its text, not affix rolls");
            Assert(saved.Notes.StartsWith("Rarity: UNIQUE\nMorior Invictus\nGrand Regalia", StringComparison.Ordinal),
                "the saved text is the item: " + saved.Notes.Split('\n').FirstOrDefault());
        });

        var (imported, importedItem) = NewDraft(item: new GearItem
        {
            Rarity = "unique", Name = "Headhunter", ItemLevel = 80,
            Notes = "Rarity: UNIQUE\nHeadhunter\nHeavy Belt\nImplicits: 1\n+25 to Strength\n50% increased Stunned Threshold"
        }, slot: "Belt");
        Report("ItemDraft: an imported unique keeps its own text and shows it like the game", () => Assert(
            imported.Notes.Contains("Heavy Belt", StringComparison.Ordinal) && imported.UniqueLines.Count == 2 && imported.UniqueLines[0].IsImplicit && imported.Preview.Count >= 3,
            "imported lines=" + imported.UniqueLines.Count + " preview=" + imported.Preview.Count));
        Layout("ItemEditorWindow (imported unique)", () => new ItemEditorWindow(imported));
        Report("ItemDraft: an imported unique saves with its own text untouched", () => Assert(
            Save(imported, importedItem).Notes == "Rarity: UNIQUE\nHeadhunter\nHeavy Belt\nImplicits: 1\n+25 to Strength\n50% increased Stunned Threshold",
            "the text of an imported item is authoritative and must not be rewritten from the data"));

        // Blistering Bond is the ring that used to crash the editor: its "-(15-10)% to Cold Resistance" line
        // has the sign outside the range, which inverted the band and made Math.Clamp throw. It must open now,
        // showing only the current version's lines.
        var burn = NewDraft("Blistering Bond", null, "Ring1");
        Report("ItemDraft: Blistering Bond opens with its current version only", () => Assert(
            burn.Draft.HasUniqueData && burn.Draft.UniqueLines.Count > 0
            && burn.Draft.UniqueVariantChoices.Count == 1
            && !UniqueItemText.IsOldVersion(burn.Draft.UniqueVariantChoices[0].Name),
            "Blistering Bond current-only: lines=" + burn.Draft.UniqueLines.Count + " variants=" + burn.Draft.UniqueVariantChoices.Count));
        Report("ItemDraft: its negative-roll line has a sane band", () => Assert(
            burn.Draft.UniqueLines.Any(l => UniqueItemText.TryGetRange(l.Template, out decimal lo, out decimal hi) && lo < hi),
            "a current Blistering Bond line carries an ordered range"));
        Layout("ItemEditorWindow (Blistering Bond)", () => new ItemEditorWindow(burn.Draft));

        // A brand-new item opens in the simplified base-only mode (no fields/mods/sockets); the full editor
        // opens only once the item is equipped. The existing ItemEditorWindow(rareDraft) check above now
        // exercises exactly that create-mode layout.
        Report("ItemDraft: a brand-new item is in base-only create mode", () => Assert(
            rareDraft.IsNewItem, "a new gear draft has IsNewItem set (so the advanced column stays collapsed)"));


        Layout("JewelEditorWindow", () => new JewelEditorWindow(new JewelDraftViewModel(L, catalog)));
        Report("JewelDraft: a unique jewel uses only its own mods", () =>
        {
            var jewel = new JewelDraftViewModel(L, catalog);
            var split = jewel.JewelChoices.FirstOrDefault(choice => choice.Unique?.Name == "Split Personality")
                ?? throw new Exception("Split Personality is absent from the unified jewel list");
            Assert(split.Icon is not null, "the unique jewel has an icon in the shared list");
            jewel.SelectedJewel = split;
            var data = catalog.UniqueData.For("Split Personality") ?? throw new Exception("unique jewel data is absent");
            var expected = UniqueItemText.PersonalMods(data, jewel.UniqueMods.Select(mod => mod.Template));
            var available = jewel.AvailableMods.ToArray();
            Assert(available.Length == expected.Count && expected.All(line => available.Any(option => option.UniqueLine?.Text == line.Text)),
                "available options match only Split Personality's own remaining lines");
            Assert(available.All(option => option.Affix is null && option.UniqueLine is not null), "no base affix leaks into the unique pool");
            jewel.SelectedAffix = available.FirstOrDefault() ?? throw new Exception("no unique jewel mods are offered");
            string selectedText = jewel.SelectedAffix.Text;
            jewel.AddAffixCommand.Execute(null);
            Assert(jewel.Notes.Contains(selectedText, StringComparison.Ordinal), "adding a unique line updates the item text");

            var ruby = jewel.JewelChoices.First(choice => choice.Base?.Name == "Ruby");
            jewel.SelectedJewel = ruby;
            Assert(jewel.AvailableMods.All(option => option.Affix is not null && option.UniqueLine is null),
                "switching to Ruby restores its base-affix pool");
        });
        Layout("SkillGroupEditorWindow", () => new SkillGroupEditorWindow(new GroupDraftViewModel(L, catalog, null, _ => { })));
        Layout("ImportCodeWindow", () => new ImportCodeWindow(shell));
        Layout("SupportPickerWindow", () => new SupportPickerWindow(L, catalog, []));

        // The shell itself. The passive tree tab has to take the whole workspace — the layout rule the owner
        // asked for after seeing it capped at 16:9 — and the equipment board has to grow with the window. The
        // shots land in %TEMP%\PoeBuilder-window-shots so a human can look at the tabs without clicking
        // through the app.
        string shots = Path.Combine(Path.GetTempPath(), "PoeBuilder-window-shots");
        Directory.CreateDirectory(shots);
        void Shell(string name, string page, double width, double height, Action<FrameworkElement, Window>? verify = null) => Report(name, () =>
        {
            var window = new MainWindow { DataContext = shell };
            shell.SelectedNav = shell.Navigation.First(n => n.Key == page);
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(width, height));
            content.Arrange(new Rect(0, 0, width, height));
            content.UpdateLayout();
            verify?.Invoke(content, window);
            Shot(content, Path.Combine(shots, page + "-" + (int)width + "x" + (int)height + ".png"), width, height);
        });
        Shell("MainWindow: the passive tree fills the workspace", "Tree", 1920, 1080, (content, window) =>
        {
            var tree = (FrameworkElement)window.FindName("TreeSurface")!;
            Assert(tree.ActualWidth > content.ActualWidth - 250, $"the tree is {tree.ActualWidth} wide in a {content.ActualWidth} workspace");
            // The tree keeps everything under the app's own chrome: title bar, header row and status bar, plus
            // the tree's own command bar. 300 px of 1080 is that budget with room to spare.
            Assert(tree.ActualHeight > content.ActualHeight - 300, $"the tree is {tree.ActualHeight} tall in a {content.ActualHeight} workspace");
            // The floating panels are the tree's own controls now: if either collapses to nothing the tab
            // renders but cannot be driven.
            var panel = (FrameworkElement)window.FindName("NodePanel")!;
            Assert(panel.ActualHeight > 200 && panel.ActualWidth > 200, $"the node panel is {panel.ActualWidth}x{panel.ActualHeight}");
            var toolbar = (FrameworkElement)window.FindName("TreeToolbar")!;
            Assert(toolbar.ActualHeight > 150, "the tree toolbar is " + toolbar.ActualHeight);
            // The sidebar starts folded down to its icons and slides its tab names out on hover: a shell that
            // opens on the wide rail, or one whose rail refuses to grow, is the same bug seen twice.
            var sidebar = (FrameworkElement)window.FindName("SidebarHost")!;
            Assert(sidebar.ActualWidth < 100, "the folded sidebar is " + sidebar.ActualWidth + " wide");
            ((MainWindow)window).SetNavExpandedImmediate(true);
            content.UpdateLayout();
            Assert(sidebar.ActualWidth > 200, "the expanded sidebar is " + sidebar.ActualWidth + " wide");
            Shot(content, Path.Combine(shots, "sidebar-expanded.png"), content.ActualWidth, content.ActualHeight);
            ((MainWindow)window).SetNavExpandedImmediate(false);
            content.UpdateLayout();
            Assert(sidebar.ActualWidth < 100, "the sidebar did not fold back: " + sidebar.ActualWidth);
        });
        Shell("MainWindow: the tree keeps a usable size in a small window", "Tree", 1280, 800, (content, window) =>
        {
            var tree = (FrameworkElement)window.FindName("TreeSurface")!;
            Assert(tree.ActualWidth > 900 && tree.ActualHeight > 420, $"the tree is {tree.ActualWidth}x{tree.ActualHeight}");
        });
        Shell("MainWindow: the equipment board grows with the window", "Items", 1920, 1080, (content, window) =>
        {
            var canvas = (FrameworkElement)window.FindName("SlotCanvas")!;
            Assert(canvas.ActualWidth > 560, $"the slot board is {canvas.ActualWidth} wide");
            Assert(shell.Equipment.Slots.Count == 17, $"the equipment board has {shell.Equipment.Slots.Count} slots");
            var board = (Canvas)window.FindName("EquipmentBoardCanvas")!;
            string[] names = ["Helmet", "Amulet", "Gloves", "Body", "Ring1", "Belt", "Ring2", "Boots", "Main1", "Off1", "Main2", "Off2", "LifeFlask", "ManaFlask", "Charm1", "Charm2", "Charm3"];
            foreach (string name in names)
            {
                var slot = (Button)window.FindName("Slot" + name)!;
                double left = Canvas.GetLeft(slot), top = Canvas.GetTop(slot);
                Assert(left >= 0 && top >= 0 && left + slot.Width <= board.Width && top + slot.Height <= board.Height,
                    $"{slot.Name} is inside the board bounds at {left},{top}");
            }
        });

        Report("Locale: the Russian tree, gems and bases are really loaded and shown", () =>
        {
            // The owner's report was a screenshot of an English tooltip inside a Russian window. This drives
            // the real shell and checks what the tooltip itself would paint, so a locale file that exists but
            // never reaches the tree cannot pass.
            var strings = shell.GameLocale?.Strings ?? throw new Exception("no game strings were loaded");
            Assert(!strings.IsEmpty, "locale-ru.json is missing or empty");
            var tree = shell.Tree;
            Assert(tree.Strings is not null, "the tree was never handed the Russian text");

            var notable = tree.Catalog!.Nodes.Values.First(n => !string.IsNullOrWhiteSpace(n.Name) && n.IsNotable);
            var ru = strings.Node(notable.Id.ToString());
            Assert(ru is not null, "no Russian text for node " + notable.Id);
            Assert(ru!.Name != notable.Name, "the Russian name is just the English one for " + notable.Id);
            Assert(ru.Name.Any(c => c >= '\u0400' && c <= '\u04FF'), "the name is not Russian: " + ru.Name);

            // Same check the tooltip performs, so this fails for exactly the reason the screenshot did.
            var russianUi = L.Language == "ru";
            var shownName = ru is not null && russianUi ? ru.Name : notable.Name;
            var info = tree.Catalog.Describe(notable.Id, tree.Plan);
            var shownStats = ru is not null && russianUi && ru.Stats is { Length: > 0 } ? ru.Stats : info.Stats;
            Assert(shownName == ru!.Name, "the tooltip would still paint the English name");
            if (russianUi) Assert(shownStats.Any(s => s.Any(c => c >= '\u0400' && c <= '\u04FF')),
                "the tooltip would still paint English stat lines");
            // The "[Allies|Союзники]" screenshot: the machine-readable stat id must not reach the screen.
            foreach (var line in shownStats)
                Assert(!System.Text.RegularExpressions.Regex.IsMatch(PoeBuilder.Core.Localization.GameStrings.Clean(line), @"\[[A-Za-z0-9_]+\|"),
                    "a stat id is still painted in the tooltip: " + line);

            // Names from the catalog resolve too, which is what the equipment, jewel and skill tabs show.
            var gem = catalog.Gems.Values.First(g => strings.Find(g.Name) is not null);
            Assert(strings.Name(gem.Name, true).Any(c => c >= '\u0400' && c <= '\u04FF'), "gem name not Russian: " + gem.Name);
            var baseItem = catalog.Bases.Values.First(b => strings.Find(b.Name) is not null);
            Assert(strings.Name(baseItem.Name, true).Any(c => c >= '\u0400' && c <= '\u04FF'), "base name not Russian: " + baseItem.Name);

            // Switching to English must give the English text back, not leave the Russian behind.
            var saved = L.Language;
            try
            {
                L.SetLanguage("en");
                Assert(strings.Name(gem.Name, false) == gem.Name, "an English interface must show the English name");
            }
            finally { L.SetLanguage(saved); }
        });

        Report("Sidebar: the owner's tab icons are packed and actually load", () =>
        {
            // A PNG that fails to pack fails silently at runtime: the tab just keeps its inline path, so
            // nothing would ever tell us the artwork never arrived. Check the real pack URI instead. Only the
            // tabs the owner actually supplied artwork for are required to load; a tab with no PNG is a
            // deliberate choice and falls back to the inline glyph by design.
            var sourceDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "NavIcons");
            var supplied = Directory.Exists(sourceDir)
                ? Directory.GetFiles(sourceDir, "*.png").Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            var expected = shell.Navigation.Where(n => supplied.Contains(n.Key)).Select(n => n.Key).ToList();
            Assert(expected.Count > 0, "no tab icon PNG was found in assets/NavIcons");
            var missing = expected.Where(k => shell.Navigation.First(n => n.Key == k).HasGlyph is false).ToList();
            Assert(missing.Count == 0,
                "these tabs still show the built-in glyph because their PNG did not pack: " + string.Join(", ", missing));
            var withoutArt = shell.Navigation.Where(n => !supplied.Contains(n.Key)).Select(n => n.Key).ToList();
            if (withoutArt.Count > 0)
                Console.WriteLine($"      note: no PNG supplied for {string.Join(", ", withoutArt)}; those tabs keep the built-in glyph");
        });

        Report("Locale: the item editor lists Russian bases and modifiers", () =>
        {
            var locale = shell.GameLocale ?? throw new Exception("no game locale was loaded");
            var previous = shell.L.Language;
            try
            {
                shell.L.SetLanguage("ru");
                // The editor is where the owner found bases and mods still in English: it builds its own
                // lists, so it needs its own locale rather than the tab's.
                // No slot: the base picker shows every base, which is the state the owner saw.
                var draft = new ItemDraftViewModel(shell.L, catalog, null, null, _ => { }) { Locale = locale };
                var bases = draft.Bases.Take(200).ToList();
                Assert(bases.Count > 0, "the editor offered no bases at all");
                // A base may legitimately stay English only when GGG marks it [DNT] ("do not translate"), in which case
                // the game itself shows no Russian name either. Anything else untranslated is a real gap.
                var untranslated = bases.Where(b => !b.Name.Any(c => c >= '\u0400' && c <= '\u04FF')).ToList();
                Assert(untranslated.All(b => (b.Base?.Name ?? "").StartsWith("[DNT]", StringComparison.Ordinal)),
                    "a base without a translation that GGG does not mark [DNT]: "
                        + string.Join(", ", untranslated.Take(3).Select(b => b.Name)));
                Assert(bases.Any(b => b.Name.Any(c => c >= '\u0400' && c <= '\u04FF')),
                    "no base was translated at all");
                // Russian search has to work against the Russian list, or the translated picker is unusable.
                draft.BaseSearch = "Сапоги";
                Assert(draft.Bases.Any(), "a Russian search found no base");
                draft.BaseSearch = "";
                // Modifiers only appear once a base is chosen, so pick a real one out of the catalog.
                draft.SelectedBase = catalog.Bases.Values.First(b => b.ItemClass == "Body Armour");
                draft.ItemLevel = "80";
                var mods = draft.AvailableMods.Take(200).ToList();
                Assert(mods.Count > 0, "the editor offered no modifiers at all");
                Assert(mods.Any(m => m.Text.Any(c => c >= '\u0400' && c <= '\u04FF')),
                    "every offered modifier would still be English");
                draft.ModSearch = "максимум";
                Assert(draft.AvailableMods.Any(), "a Russian search found nothing in the modifier list");
                draft.ModSearch = "maximum";
                Assert(draft.AvailableMods.Any(), "an English search found nothing in the modifier list");
            }
            finally { shell.L.SetLanguage(previous); }
        });

        Report("Locale: modifier text is translated and still carries its rolled numbers", () =>
        {
            var locale = shell.GameLocale ?? throw new Exception("no game locale was loaded");
            var translated = 0;
            foreach (var pool in new[] { catalog.Mods.Values, catalog.JewelMods })
                foreach (var mod in pool)
                {
                    var ru = locale.Strings?.Mod(mod.Text);
                    if (ru is null) continue;
                    translated++;
                    // A rolled value goes in exactly where the '#' sits, so the Russian line must carry the
                    // same number of slots as the English one or the numbers would vanish from the item.
                    int slots(string s) => s.Count(c => c == '#');
                    Assert(slots(ru) == slots(mod.Text),
                        $"slot count differs for {mod.Id}: '{mod.Text}' -> '{ru}'");
                    Assert(ru.Any(c => c >= '\u0400' && c <= '\u04FF'), "not Russian: " + ru);
                }
            Assert(translated > 0, "no modifier text was translated at all");
            var any = catalog.Mods.Values.First(m => locale.Strings?.Mod(m.Text) is not null);
            // An English interface must still show the English text, whatever language the self-check runs in.
            var englishUi = shell.L;
            var previous = englishUi.Language;
            try
            {
                englishUi.SetLanguage("en");
                Assert(new GameLocale(locale.Strings, englishUi).Mod(any.Text) == any.Text,
                    "an English interface must show the English modifier text");
                Assert(new GameLocale(locale.Strings, englishUi).ModMatches(any.Text, "здоровья") == false,
                    "an English interface must not match Russian text");
            }
            finally { englishUi.SetLanguage(previous); }
        });

        Report("Stages: a skill added in one act reaches the next act's tab, and not the one before", () =>
        {
            // The owner's report, driven through the real tabs rather than the editor's setters: each tab holds
            // its own plan and is re-bound on every stage change, so a carry that worked only in the model
            // would still leave the tab empty. Act 2 and Act 3 are visited BEFORE Act 1 is edited — that order
            // is the one that used to drop the edit, because each act had already taken its inherited
            // snapshot and there was no left-to-right path left to travel along.
            var editor = new BuildEditor(BuildDocument.Create("stage-carry"));
            var skills = new SkillsViewModel(L);
            var equipment = new EquipmentViewModel(L);
            var character = new CharacterViewModel(L, shell);
            skills.SetCatalog(catalog);
            equipment.SetCatalog(catalog);
            editor.StageActivated += () => { skills.BindEditor(editor); equipment.BindEditor(editor); character.BindEditor(editor); };
            skills.BindEditor(editor); equipment.BindEditor(editor); character.BindEditor(editor);
            var gem = catalog.Gems.Values.First(g => g.Kind != "support" && g.Levels.Length > 0);
            var act1 = editor.Stages[0].Id; var act2 = editor.Stages[1].Id; var act3 = editor.Stages[2].Id;
            editor.CurrentStageId = act2; editor.CurrentStageId = act3; editor.CurrentStageId = act1;
            // Skills, equipment and jewels all travel the same path: they are one stage snapshot, so a single
            // write per tab is enough to prove each of the three.
            skills.QuickAddVia(new SkillGroup { Name = gem.Name, Active = new() { GemId = gem.Id, Level = gem.Levels[0], Quality = 0 } });
            Assert(skills.Cards.Count == 1, "the skill lands in act 1");
            editor.CurrentStageId = act2;
            Assert(skills.Cards.Count == 1, "act 2 shows the skill added in act 1, but was " + skills.Cards.Count);
            editor.CurrentStageId = act3;
            Assert(skills.Cards.Count == 1, "and so does act 3");
            editor.CurrentStageId = act1;
            Assert(skills.Cards.Count == 1, "while act 1 keeps its own");
            // The character's level is part of the same snapshot, so it travels with the rest. Act 2 has not
            // been edited itself, so it keeps following Act 1 — that is the whole rule, and asserting the
            // opposite here would encode the old one-shot behaviour.
            editor.LevelText = "42";
            editor.CurrentStageId = act2;
            Assert(editor.LevelText == "42", "act 2 carries the level set in act 1, but was " + editor.LevelText);
            editor.CurrentStageId = act1;
            editor.LevelText = "12";
            editor.CurrentStageId = act2;
            Assert(editor.LevelText == "12", "and a later edit in act 1 reaches act 2 again, but was " + editor.LevelText);
        });

        Report("Tree: the game's allocation clicks take the node for the weapon set they name", () =>
        {
            // The tree's own click model, which the mouse handlers call: a plain left click takes the node for
            // both sets, Shift+left for set I, Shift+right for set II. The handlers only pass the set, so this
            // drives exactly what a click does.
            var tree = shell.Tree;
            var catalog = tree.Catalog ?? throw new Exception("the pinned tree did not load");
            tree.BindEditor(new BuildEditor(BuildDocument.Create("window-check")));
            Assert(tree.CanModify, "the tree is editable once a build is bound");
            int start = catalog.Classes.First(c => c.Index == tree.Plan.ClassIndex).StartNodeId;
            var edge = catalog.Edges.First(e => e.From == start || e.To == start);
            int node = edge.From == start ? edge.To : edge.From;
            tree.ClickNode(node);
            Assert(tree.Plan.AllocatedNodes.Contains(node), "a left click allocates the node");
            Assert(!tree.Plan.WeaponSetNodes.ContainsKey(node), "and leaves it a plain allocation of both sets");
            tree.ClickNode(node);
            Assert(!tree.Plan.AllocatedNodes.Contains(node), "a second left click refunds it");
            tree.ClickNode(node, 1);
            Assert(tree.Plan.AllocatedNodes.Contains(node) && tree.Plan.WeaponSetNodes.TryGetValue(node, out int set) && set == 1,
                "Shift+left click takes it for weapon set I");
            tree.ClickNode(node);
            Assert(!tree.Plan.AllocatedNodes.Contains(node) && !tree.Plan.WeaponSetNodes.ContainsKey(node),
                "a plain click refunds it and drops the assignment");
            tree.ClickNode(node, 2);
            Assert(tree.Plan.WeaponSetNodes.TryGetValue(node, out int second) && second == 2, "Shift+right click takes it for weapon set II");
            tree.RefundCommand.Execute(null);
            Assert(!tree.Plan.WeaponSetNodes.ContainsKey(node), "refunding the selected node clears its set");
        });


        Report("Fields: every text box is tall enough to show the line it holds", () =>
        {
            // The owner's report: typing into a search box worked and the search filtered, but nothing appeared
            // in the field. The cause is the TextBox style, not the binding — its content host is a
            // ScrollViewer, so whatever the vertical padding takes is height the line of text no longer has, and
            // a field whose padding outgrows the line clips its own input out of sight. Measured on the real
            // editors rather than on a synthetic box, so it covers the fields the player really types into.
            foreach (var (name, build) in new (string, Func<Window>)[]
            {
                ("item", () => new ItemEditorWindow(new ItemDraftViewModel(L, catalog, null, null, _ => { }))),
                ("jewel", () => new JewelEditorWindow(new JewelDraftViewModel(L, catalog))),
                ("skill", () => new SkillGroupEditorWindow(new GroupDraftViewModel(L, catalog, null, _ => { })))
            })
            {
                var window = build();
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.ShowInTaskbar = false; window.Left = -32000; window.Top = -32000;
                window.Show(); window.UpdateLayout();
                try
                {
                    foreach (var box in Descendants<TextBox>(window))
                    {
                        if (!box.IsVisible || box.ActualWidth <= 0) continue;
                        var host = box.Template?.FindName("PART_ContentHost", box) as ScrollViewer;
                        if (host is null) continue;
                        var sample = new TextBlock { Text = "Проверка ввода 123", FontSize = box.FontSize, FontFamily = box.FontFamily };
                        sample.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        // A multi-line field (the notes box) is allowed to scroll; a single-line one is not, so
                        // anything narrower than the line there is text the player cannot read back.
                        if (box.AcceptsReturn) continue;
                        Assert(host.ActualHeight + 0.5 >= sample.DesiredSize.Height,
                            $"the {name} editor's field leaves {host.ActualHeight:F1}px for a {sample.DesiredSize.Height:F1}px line: "
                            + "its height or vertical padding clips the text out of the box");
                    }
                }
                finally { window.Close(); }
            }
        });

        Report("Fields: the editor's add buttons are inside the window, not clipped off its edge", () =>
        {
            // The owner's report, part two: in a narrow panel the "add" button was the first thing pushed out,
            // while the list it belongs to stayed visible. Every button is measured against the window's own
            // bounds, so a layout that hides one again fails here instead of in front of the player.
            var draft = new ItemDraftViewModel(L, catalog, null, null, _ => { });
            draft.SelectedBase = catalog.Bases.Values.First(b => b.ItemClass == "Body Armour");
            draft.ItemLevel = "80"; draft.ModSearch = "life"; draft.BaseSearch = "меч";
            var jewel = new JewelDraftViewModel(L, catalog);
            jewel.JewelSearch = "руб"; jewel.AffixSearch = "life";
            var skill = new GroupDraftViewModel(L, catalog, null, _ => { });
            var windows = new (string Name, Func<Window> Build)[]
            {
                ("item", () => new ItemEditorWindow(draft)),
                ("jewel", () => new JewelEditorWindow(jewel)),
                ("skill", () => new SkillGroupEditorWindow(skill))
            };
            foreach (var (name, build) in windows)
            {
                var window = build();
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.ShowInTaskbar = false; window.Left = -32000; window.Top = -32000;
                window.Show(); window.UpdateLayout();
                try
                {
                    var shots = Path.Combine(Path.GetTempPath(), "PoeBuilder-window-shots");
                    Directory.CreateDirectory(shots);
                    Shot(window, Path.Combine(shots, name + "-editor.png"), window.ActualWidth, window.ActualHeight);
                    var clipped = new List<string>();
                    foreach (var button in Descendants<Button>(window))
                    {
                        if (!button.IsVisible || button.ActualWidth <= 0) continue;
                        var edge = button.TransformToAncestor(window).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
                        if (edge.Right > window.ActualWidth + 0.5 || edge.Left < -0.5)
                            clipped.Add($"{button.Content} (right edge {edge.Right:F0} of {window.ActualWidth:F0})");
                    }
                    Assert(clipped.Count == 0, "these buttons reach past the window edge: " + string.Join("; ", clipped.Take(4)));
                }
                finally { window.Close(); }
            }
        });

        Report("Skills: a group is born from the quick-add row, not from a separate button", () =>
        {
            // The owner's request: the skills tab already adds a skill straight away, so the "new group"
            // button and the window behind it were a second, slower way in. The check asserts the button is
            // gone from the real tab and that what is left still reaches the group editor, because the cards
            // are ItemsControl items and nothing else selects or edits them.
            var skills = shell.Skills;
            var window = new MainWindow { DataContext = shell };
            shell.SelectedNav = shell.Navigation.First(n => n.Key == "Skills");
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(1920, 1080));
            content.Arrange(new Rect(0, 0, 1920, 1080));
            content.UpdateLayout();
            // The commands are gated on CanEdit, which needs a bound build: the shell opens empty.
            skills.BindEditor(new BuildEditor(BuildDocument.Create("skill-quick-add")));
            var gem = catalog.Gems.Values.First(g => g.Kind != "support" && g.Levels.Length > 0);
            skills.QuickAddVia(new SkillGroup { Name = gem.Name, Active = new() { GemId = gem.Id, Level = gem.Levels[0], Quality = 0 } });
            content.UpdateLayout();
            try
            {
                // The label is matched as text, in both languages, because the key it used to come from is gone.
                var buttons = Descendants<Button>(content).Where(b => b.ActualWidth > 0).Select(b => b.Content?.ToString() ?? "").ToArray();
                Assert(!buttons.Any(text => text is "Новая группа" or "New group"),
                    "the tab still offers a \"new group\" button; groups come from the quick-add row: " + string.Join(", ", buttons));
                Assert(skills.Cards.Count > 0, "the quick-add row produced no card to work with");
                var card = skills.Cards[^1];
                skills.SelectCardCommand.Execute(card);
                Assert(skills.Selected?.Id == card.Id, "clicking a card does not select the group");
                // The pencil opens the group editor for this card. Its command is checked rather than
                // executed: the editor is modal and would hang the check on a dialog.
                Assert(skills.EditCardCommand.CanExecute(card), "the card's edit button is disabled");
                Assert(skills.EditCommand.CanExecute(null), "the toolbar's edit button is disabled once a card is selected");
                // IsVisible is false for a window that was only measured, so the pencil is found by its laid-out
                // size instead — same reason the other template checks use ActualWidth.
                Assert(Descendants<Button>(content).Any(b => Equals(b.Content, "✎") && b.ActualWidth > 0),
                    "the card has no edit button, so the group editor is unreachable");
            }
            finally { window.Close(); }
        });

        Report("RegEx: the tab turns picked catalog modifiers into a usable search string", () =>
        {
            // The generator is only worth anything if it stays inside the data it owns: a category may hold
            // only modifiers the game can actually roll on it, and the string may only ever describe the
            // game's own template text.
            var regex = shell.RegEx;
            regex.SetCatalog(catalog);
            Assert(!regex.Empty, "the tab has no categories at all");
            foreach (var category in regex.Categories)
            {
                Assert(category.Mods.Count > 0, $"category {category.Id} offers nothing");
                var baseIds = catalog.Bases.Values
                    .Where(b => catalog.ModsFor(b, int.MaxValue).Any(m => m.Id == category.Mods[0].Id))
                    .Select(b => b.ItemClass).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Assert(baseIds.Count > 0, $"category {category.Id} is not backed by any base in the catalog");
            }
            regex.SelectedCategory = regex.Categories.FirstOrDefault(c => c.Id == "Ring") ?? regex.Categories[0];
            Assert(regex.Visible.Count > 0, "a category lists no modifiers to pick from");
            var row = regex.Visible.First(r => r.HasNumber);
            // The three gestures the owner asked for, driven through the row itself.
            row.Cycle(optional: false, excluded: false);
            Assert(row.Requirement == RegexRequirement.Required, "a plain click must make the modifier required");
            Assert(regex.Output.Length > 0, "a picked modifier produced no search string");
            row.Cycle(optional: true, excluded: false);
            Assert(row.Requirement == RegexRequirement.Optional, "a Shift+click must make it optional");
            row.Cycle(optional: false, excluded: true);
            Assert(row.Requirement == RegexRequirement.Excluded, "a right click must exclude it");
            Assert(regex.Output.StartsWith("!"), "an excluded modifier must be negated, but was: " + regex.Output);
            // A bound that cannot be read must narrow nothing rather than corrupt the string.
            row.Cycle(optional: false, excluded: false);
            row.MinText = "не число"; row.MaxText = "";
            Assert(row.BadBound && row.Min is null, "an unreadable bound must be reported, not applied");
            Assert(!regex.Output.Contains("не"), "the unreadable text must not leak into the string: " + regex.Output);
            Assert(regex.Length == regex.Output.Length && regex.Length <= regex.Limit, "the counter disagrees with the string");
            // An OR needs something to choose between, so a second modifier is picked before the mode is tested.
            var second = regex.Visible.First(r => r.Id != row.Id && r.HasNumber);
            second.Cycle(optional: false, excluded: false);
            regex.Logic = RegexLogic.Any;
            Assert(regex.Output.Contains(','), "\"Any\" must produce an OR, but was: " + regex.Output);
            regex.ClearCommand.Execute(null);
            Assert(regex.Output.Length == 0 && regex.PickedCount == 0, "clear must leave nothing behind");
        });

        Report("RegEx: the tab renders its picker, its logic and its result", () =>
        {
            var window = new MainWindow { DataContext = shell };
            shell.SelectedNav = shell.Navigation.First(n => n.Key == "RegEx");
            // This one has to be a real window, not just a measured subtree: the check asks where a field sits
            // inside the window, and that needs the visual tree the Show creates.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowInTaskbar = false; window.Left = -32000; window.Top = -32000;
            window.Show(); window.UpdateLayout();
            var content = (FrameworkElement)window.Content;
            try
            {
                Assert(!shell.RegEx.Empty, "the RegEx tab rendered with nothing to show");
                Assert(Descendants<ComboBox>(content).Any(c => c.ActualWidth > 0), "the category picker is missing");
                Assert(Descendants<RadioButton>(content).Count(r => r.ActualWidth > 0) >= 3, "the three logic options are missing");
                var hosts = Descendants<ListBox>(content).Count() + Descendants<ScrollViewer>(content).Count();
                Assert(shell.RegEx.Visible.Count > 0 && hosts > 0, "the modifier list did not render");
                // Nothing may reach past the window edge: the two bounds and the text sit on one row.
                foreach (var box in Descendants<TextBox>(content).Where(b => b.ActualWidth > 0))
                {
                    var edge = box.TransformToAncestor(window).TransformBounds(new Rect(0, 0, box.ActualWidth, box.ActualHeight));
                    Assert(edge.Right <= window.ActualWidth + 0.5,
                        $"a field reaches {edge.Right:F0} of {window.ActualWidth:F0}: it is clipped");
                }
            }
            finally { window.Close(); }
        });

        Console.WriteLine(failed == 0 ? "WINDOWS: every layout loaded" : "WINDOWS: " + failed + " failed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Every element of one type in a laid-out tree, so a check can measure what a template really
    /// produced instead of what the XAML asked for. Item templates are not realised until they are shown,
    /// which is why this runs against a window that has already been displayed.</summary>
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>Renders a laid-out element to a PNG, so a layout change can be looked at instead of guessed
    /// at. A failure to write a shot is not a failed check: the picture is for a human, the assertions carry
    /// the verdict.</summary>
    private static void Shot(FrameworkElement element, string path, double width, double height)
    {
        try
        {
            var target = new System.Windows.Media.Imaging.RenderTargetBitmap((int)width, (int)height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            target.Render(element);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
            using var stream = File.Create(path);
            encoder.Save(stream);
            Console.WriteLine("     shot: " + path);
        }
        catch (Exception e) { Console.WriteLine("     (no shot: " + e.Message + ")"); }
    }
}
