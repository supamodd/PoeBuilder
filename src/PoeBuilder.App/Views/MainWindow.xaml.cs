using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PoeBuilder.App.Services;
using PoeBuilder.App.ViewModels;
using PoeBuilder.Core.Equipment;
using PoeBuilder.Core.Filters;

namespace PoeBuilder.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _closeApproved;
    private bool _closePending;
    private BuildEditor? _notesBuildEditor;
    private bool _syncingNotes;
    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModelPropertyChanged;
        BindNotesEditor(_viewModel.Editor);
        SourceInitialized += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            MinWidth = Math.Min(MinWidth, Math.Max(640, area.Width - 24));
            MinHeight = Math.Min(MinHeight, Math.Max(480, area.Height - 24));
            WindowState = WindowState.Normal;
            Left = area.Left; Top = area.Top;
            Width = area.Width; Height = area.Height;
        };
        if (!DesignerProperties.GetIsInDesignMode(this))
        {
            Loaded += async (_, _) => await _viewModel.InitializeAsync();
            Closing += OnClosing;
        }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        e.Cancel = true;
        if (_closePending) return;
        _closePending = true;
        try
        {
            if (await _viewModel.PrepareCloseAsync())
            {
                _closeApproved = true;
                // Defer the second Close until the original Closing event has unwound.
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
        finally { _closePending = false; }
    }
    // Sidebar fold: the rail shows icons only and slides the tab names out under the pointer. The keyboard
    // keeps it open too, so Tab-ing through the tabs is not a race against the fold closing.
    private static readonly TimeSpan NavOpenTime = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan NavCloseTime = TimeSpan.FromMilliseconds(150);

    private SidebarFoldState SidebarFold => (SidebarFoldState)Resources["SidebarFoldState"];

    private void SidebarEnter(object sender, MouseEventArgs e) => AnimateNav(true);
    private void SidebarLeave(object sender, MouseEventArgs e) => AnimateNav(false);
    private void SidebarLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Either input alone keeps the names out, and neither may fold the rail while the other still is
        // on it: the pointer that lost its focus event would otherwise leave the sidebar shut under a
        // stationary cursor, with no MouseEnter left to reopen it.
        if (!PointerInsideSidebar() && !FocusInsideSidebar()) AnimateNav(false);
    }
    /// <summary>Puts the sidebar in a given state with no animation and leaves it there. The window check lays
    /// the shell out synchronously and then measures the rail, which a running animation would report
    /// half-way; this is the state a finished animation lands on.</summary>
    public void SetNavExpandedImmediate(bool expanded)
    {
        _viewModel.SetNavExpanded(expanded);
        SidebarFold.SetNow(expanded);
    }

    private void AnimateNav(bool expanded)
    {
        _viewModel.SetNavExpanded(expanded);
        // One eased animation drives the whole gesture: width, margins, icon centring and the names' fade all
        // read this value. Nothing can then arrive a frame apart from anything else, and that mismatch — not
        // the duration — is what makes a panel feel like it jerks instead of unfolds.
        // Retargeting mid-flight keeps the motion continuous: a fold interrupted by the pointer coming back
        // resumes from where the rail actually is, instead of restarting from an end it never reached.
        SidebarFold.AnimateTo(expanded, expanded ? NavOpenTime : NavCloseTime, easeOut: expanded);
    }
    private bool PointerInsideSidebar() => Mouse.DirectlyOver is Visual over && Inside(over);
    private bool FocusInsideSidebar() => Keyboard.FocusedElement is Visual focused && Inside(focused);
    private bool Inside(Visual visual)
    {
        for (DependencyObject? node = visual; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, SidebarHost)) return true;
            if (node is not Visual) return false; // walked out of the visual tree into a content element
        }
        return false;
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void ResetTreeClick(object sender, RoutedEventArgs e) => TreeSurface.Reset();
    private void ZoomInClick(object sender, RoutedEventArgs e) => TreeSurface.AdjustZoom(1.12);
    private void ZoomOutClick(object sender, RoutedEventArgs e) => TreeSurface.AdjustZoom(1 / 1.12);

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Editor)) BindNotesEditor(_viewModel.Editor);
        if (e.PropertyName == nameof(MainViewModel.Page)) AnimateContentTransition();
    }

    /// <summary>Animated tab switch: instead of panels snapping between Collapsed/Visible on one frame, the
    /// whole content host eases in from a low opacity and a short rise, so every workspace change reads as a
    /// deliberate transition rather than a hard replace.</summary>
    private void AnimateContentTransition()
    {
        if (MainContent is null) return;
        MainContent.BeginAnimation(UIElement.OpacityProperty, null);
        MainContent.BeginAnimation(TranslateTransform.YProperty, null);
        MainContent.Opacity = 0.35;
        MainContent.RenderTransform = new TranslateTransform(0, 14);
        var fade = new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(200)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var rise = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(200)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var stash = MainContent;
        fade.Completed += (_, _) =>
        {
            // Restore the local value to full before clearing the animation: once the animation is removed the
            // effective opacity falls back to the local 0.35 we set above, which would darken every page for
            // good. Explicit 1 keeps the switch result at full brightness.
            stash.Opacity = 1;
            stash.BeginAnimation(UIElement.OpacityProperty, null);
            stash.BeginAnimation(TranslateTransform.YProperty, null);
            stash.RenderTransform = Transform.Identity;
        };
        MainContent.BeginAnimation(UIElement.OpacityProperty, fade);
        MainContent.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    private void BindNotesEditor(BuildEditor? editor)
    {
        if (_notesBuildEditor is not null) _notesBuildEditor.PropertyChanged -= NotesBuildPropertyChanged;
        _notesBuildEditor = editor;
        if (_notesBuildEditor is not null) _notesBuildEditor.PropertyChanged += NotesBuildPropertyChanged;
        LoadNotesDocument();
    }
    private void NotesBuildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_syncingNotes && e.PropertyName is nameof(BuildEditor.Notes) or nameof(BuildEditor.NotesRtf))
            LoadNotesDocument();
    }
    private void LoadNotesDocument()
    {
        _syncingNotes = true;
        try
        {
            NotesEditor.Document.Blocks.Clear();
            bool loadedRtf = false;
            if (_notesBuildEditor?.NotesRtf is { Length: > 0 } encoded)
            {
                try
                {
                    using var stream = new MemoryStream(Convert.FromBase64String(encoded));
                    new TextRange(NotesEditor.Document.ContentStart, NotesEditor.Document.ContentEnd).Load(stream, DataFormats.Rtf);
                    loadedRtf = true;
                }
                catch (Exception exception) when (exception is FormatException or ArgumentException or IOException or InvalidOperationException or System.Windows.Markup.XamlParseException)
                { NotesEditor.Document.Blocks.Clear(); }
            }
            if (loadedRtf) return;
            string[] lines = (_notesBuildEditor?.Notes ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines) NotesEditor.Document.Blocks.Add(new Paragraph(new Run(line)));
        }
        finally { _syncingNotes = false; }
    }
    private void NotesEditor_TextChanged(object sender, TextChangedEventArgs e) => PersistNotesDocument();
    private void PersistNotesDocument()
    {
        if (_syncingNotes || _notesBuildEditor is null) return;
        var range = new TextRange(NotesEditor.Document.ContentStart, NotesEditor.Document.ContentEnd);
        string text = range.Text;
        if (text.EndsWith("\r\n", StringComparison.Ordinal)) text = text[..^2];
        else if (text.EndsWith('\n')) text = text[..^1];
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.Rtf);
        _syncingNotes = true;
        try { _notesBuildEditor.SetNotesContent(text, Convert.ToBase64String(stream.ToArray())); }
        finally { _syncingNotes = false; }
    }
    private void NotesFormatClick(object sender, RoutedEventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        var selection = NotesEditor.Selection;
        var style = selection.GetPropertyValue(TextElement.FontStyleProperty);
        var weight = selection.GetPropertyValue(TextElement.FontWeightProperty);
        string face = style is FontStyle italic && italic == FontStyles.Italic ? "AppFontItalic"
            : weight is FontWeight currentWeight && currentWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight()
                ? "AppFontBold" : "AppFont";
        selection.ApplyPropertyValue(TextElement.FontFamilyProperty, (FontFamily)FindResource(face));
        PersistNotesDocument();
    }));
    private void NotesColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex }) return;
        var color = (Color)ColorConverter.ConvertFromString(hex);
        NotesEditor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(color));
        NotesEditor.Focus(); PersistNotesDocument();
    }
    private void NotesSectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string heading } || string.IsNullOrWhiteSpace(heading)) return;
        if (NotesEditor.Document.Blocks.Count == 1 && NotesEditor.Document.Blocks.FirstBlock is Paragraph { Inlines.Count: 0 })
            NotesEditor.Document.Blocks.Clear();
        var paragraph = new Paragraph(new Run(heading))
        {
            Margin = new Thickness(0, 12, 0, 3), FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Gold")
        };
        NotesEditor.Document.Blocks.Add(paragraph);
        NotesEditor.Document.Blocks.Add(new Paragraph());
        NotesEditor.CaretPosition = NotesEditor.Document.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        NotesEditor.Focus(); PersistNotesDocument();
    }

    // --- Equipment drag-and-drop: drag an inventory item (right list) onto a slot it fits. ---
    private const string ItemFormat = "PoeBuilder.GearItem";
    private GearRow? _dragRow;
    private Point _dragStart;
    private EquipmentViewModel Equipment() => ((MainViewModel)DataContext).Equipment;
    private void Items_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragRow = RowFrom(e.OriginalSource as DependencyObject);
        _dragStart = e.GetPosition((IInputElement)sender);
    }
    private void Items_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragRow is null || e.LeftButton != MouseButtonState.Pressed) return;
        Point p = e.GetPosition((IInputElement)sender);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _dragRow; _dragRow = null;
        DragDrop.DoDragDrop((System.Windows.Controls.ListBox)sender, new DataObject(ItemFormat, row.Id.ToString()), DragDropEffects.Move);
    }
    private static GearRow? RowFrom(DependencyObject? d) => RowOf<GearRow>(d);
    /// <summary>The nearest ancestor whose DataContext is a <typeparamref name="T"/>, so a click inside a
    /// template finds the row it belongs to without every call site walking the tree itself.</summary>
    private static T? RowOf<T>(DependencyObject? d) where T : class
    {
        while (d is not null)
        {
            if (d is FrameworkElement { DataContext: T row }) return row;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }
    private void Slot_DragOver(object sender, DragEventArgs e)
    {
        var slot = (sender as Button)?.Content as InventorySlot;
        bool ok = slot is not null && GetDraggedId(e) is { } id && Equipment().CanEquipItem(id, slot.Id);
        if (slot is not null) slot.IsHighlight = ok;
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }
    private void Slot_DragLeave(object sender, DragEventArgs e)
    {
        if ((sender as Button)?.Content is InventorySlot slot) slot.IsHighlight = false;
    }
    private void Slot_Drop(object sender, DragEventArgs e)
    {
        var slot = (sender as Button)?.Content as InventorySlot;
        if (slot is null) return;
        slot.IsHighlight = false;
        if (GetDraggedId(e) is { } id && Equipment().CanEquipItem(id, slot.Id)) Equipment().EquipDraggedItem(id, slot.Id);
        e.Handled = true;
    }
    private static string? GetDraggedId(DragEventArgs e) =>
        e.Data.GetDataPresent(ItemFormat) ? e.Data.GetData(ItemFormat) as string : null;

    /// <summary>The RegEx tab's AND / OR / Mixed choice. The radio buttons already bind their checked state
    /// through the converter; this handler only catches a click on a button that is not the current value,
    /// which is what a two-way binding cannot see.</summary>
    private void RegexLogicChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag, IsChecked: true } || DataContext is not MainViewModel shell) return;
        if (Enum.TryParse<RegexLogic>(tag, out var logic) && shell.RegEx.Logic != logic) shell.RegEx.Logic = logic;
    }

    // --- Filter tab: rule rows. ---
    // The rule list and its condition rows are ItemsControl items, which select nothing, so every button here
    // carries the row it belongs to in its Tag and the handlers go looking for it up the visual tree.
    /// <summary>The row a button belongs to, taken from its Tag. The Tag is the row object itself rather than a
    /// name, so the handler cannot be wired to the wrong one by a copy-paste.</summary>
    private static T? FilterRowOf<T>(object? sender) where T : class => (sender as FrameworkElement)?.Tag as T;

    private void FilterPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: FilterPresets.Preset preset }) LootFilter().ApplyPreset(preset);
    }
    private void FilterMoveUp(object sender, RoutedEventArgs e)
    {
        if (FilterRowOf<FilterBlockRow>(sender) is { } row) LootFilter().Move(row, -1);
    }
    private void FilterMoveDown(object sender, RoutedEventArgs e)
    {
        if (FilterRowOf<FilterBlockRow>(sender) is { } row) LootFilter().Move(row, 1);
    }
    private void FilterRemoveBlock(object sender, RoutedEventArgs e)
    {
        if (FilterRowOf<FilterBlockRow>(sender) is { } row) LootFilter().RemoveBlock(row);
    }
    private void FilterAddCondition(object sender, RoutedEventArgs e)
    {
        if (FilterRowOf<FilterBlockRow>(sender) is { } row) row.AddCondition();
    }
    private void FilterRemoveCondition(object sender, RoutedEventArgs e)
    {
        if (FilterRowOf<FilterConditionRow>(sender) is not { } row) return;
        // The condition button's own Tag is the condition; the block it belongs to is the nearest ancestor
        // carrying one, which the visual-tree walk finds without a second binding.
        if (RowOf<FilterBlockRow>((sender as FrameworkElement)?.Parent) is { } block) block.RemoveCondition(row);
    }
    /// <summary>The keyword list resets the row's value, so switching from a rarity to a number does not leave
    /// a name behind as the value. The handler only sees the change; the reset itself is the row's business.</summary>
    private void FilterKeywordChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedItem: string keyword } box) return;
        // The first selection arrives with the template itself, before the row exists to be changed.
        if (FilterRowOf<FilterConditionRow>(box) is not { } row || row.Keyword == keyword) return;
        if (DataContext is MainViewModel shell) shell.LootFilter.Rebind(row, keyword);
    }
    /// <summary>The minimap marker is stored as the game's own icon index, not as its name, so the file carries
    /// a number the client already knows. The list is the game's own icon names in its own order.</summary>
    private void FilterIconChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: int index } || index < 0) return;
        if (RowOf<FilterBlockRow>(VisualTreeHelper.GetParent((DependencyObject)sender)) is { } block)
            block.MinimapIcon = index.ToString();
    }
    private FilterViewModel LootFilter() => ((MainViewModel)DataContext).LootFilter;

    // --- RegEx tab: one modifier row, three gestures. ---
    // Plain click takes the modifier as required, Shift makes it optional and the right button excludes it.
    // The three states are what the game's own filter grammar expresses with spaces, commas and "!", so the
    // row cycles between them rather than opening a dialog.
    private void RegexRowClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf<RegexModRow>(e.OriginalSource as DependencyObject) is not { } row) return;
        // A click that lands in the bound's own text box is that box's business, not the row's.
        if (e.OriginalSource is TextBox) return;
        row.Cycle((Keyboard.Modifiers & ModifierKeys.Shift) != 0, e.ChangedButton == MouseButton.Right);
        e.Handled = true;
    }
}
