using System.IO;
using System.Windows;
using PoeBuilder.App.ViewModels;
using PoeBuilder.App.Views;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Models;

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


        Layout("JewelEditorWindow", () => new JewelEditorWindow(new JewelDraftViewModel(L, catalog)));
        Layout("SkillGroupEditorWindow", () => new SkillGroupEditorWindow(new GroupDraftViewModel(L, catalog, null, _ => { })));
        Layout("SupportPickerWindow", () => new SupportPickerWindow(L, catalog, []));

        // The shell itself. The passive tree tab has to take the whole workspace — the layout rule the owner
        // asked for after seeing it capped at 16:9 — and the equipment board has to grow with the window. The
        // shots land in %TEMP%\PoeBuilder-window-shots so a human can look at the tabs without clicking
        // through the app.
        string shots = Path.Combine(Path.GetTempPath(), "PoeBuilder-window-shots");
        Directory.CreateDirectory(shots);
        void Shell(string name, string page, double width, double height, Action<FrameworkElement, Window>? verify = null) => Report(name, () =>
        {
            var window = new MainWindow();
            var shell = (MainViewModel)window.DataContext;
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

        Console.WriteLine(failed == 0 ? "WINDOWS: every layout loaded" : "WINDOWS: " + failed + " failed");
        return failed == 0 ? 0 : 1;
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
