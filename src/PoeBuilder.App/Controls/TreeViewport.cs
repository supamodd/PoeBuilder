using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PoeBuilder.App.ViewModels;
using PoeBuilder.Core.Calculation;
using PoeBuilder.Core.Tree;

namespace PoeBuilder.App.Controls;

/// <summary>Immediate-mode WPF rendering: no thousands of UIElements or network dependencies.</summary>
public sealed class TreeViewport : FrameworkElement
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(TreeViewModel), typeof(TreeViewport), new PropertyMetadata(null, ModelChanged));
    public TreeViewModel? Model { get => (TreeViewModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(TreeViewport), new PropertyMetadata(0.13));
    public double Zoom { get => (double)GetValue(ZoomProperty); private set => SetValue(ZoomProperty, value); }
    private Point _center;
    private Point? _dragStart, _lastDrag;
    private bool _dragged;
    private int? _hover;
    private TreeCatalog? _catalog;
    private readonly Dictionary<string, (Point Center, double Zoom)> _views = [];
    private BitmapSource? _portrait;
    private string _portraitKey = "";
    private Rect _portraitRect;
    private bool _fitPending;
    private readonly Dictionary<string, BitmapSource> _icons = [];
    private readonly Dictionary<string, Int32Rect> _frames = [];
    private BitmapSource? _atlas;
    private Dictionary<int, PassiveVariant> _descriptions = [];
    private HashSet<int> _socketed = [];
    private IReadOnlyDictionary<int, int> _weaponSets = new Dictionary<int, int>();
    private int _activeWeaponSet = 1;
    private readonly List<(TreeEdge Edge, Geometry Shape)> _edges = [];
    private int? _start;
    private static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Brush BackgroundInk = Brush("#0D1319"), NodeInk = Brush("#080C10"), Gold = Brush("#D9B576"), Bronze = Brush("#6D5940"), Dim = Brush("#303A42"), Cyan = Brush("#77C9CE"), TextInk = Brush("#CCD2D6");
    // PoB2 colours a weapon-set allocation by its own set: set I uses colorCodes.NEGATIVE (#DD0022, red) and
    // set II colorCodes.POSITIVE (#33FF77, green) — see Classes/PassiveTreeView.lua:716-730 and 1072-1073.
    /// <summary>PoB2's own weapon-set colours (Classes/PassiveTreeView.lua uses colorCodes.NEGATIVE for the
    /// first set and colorCodes.POSITIVE for the second): published so a test can pin them.</summary>
    public const string WeaponSetOneColor = "#DD0022", WeaponSetTwoColor = "#33FF77";
    private static readonly Brush SetOne = Brush(WeaponSetOneColor), SetTwo = Brush(WeaponSetTwoColor);
    /// <summary>The tint of a socketed jewel's radius circle (PoB2 draws the same reach around its sockets).</summary>
    private static readonly Brush JewelRadiusInk = Brush("#8A6FA8E8");
    private static Pen Pen(Brush brush, double width) { var p = new Pen(brush, width); p.Freeze(); return p; }
    private static readonly Pen IdleEdge = Pen(Dim, 10), ActiveEdge = Pen(Gold, 15), PreviewEdge = Pen(Cyan, 17), LockedEdge = Pen(Dim, 5);
    private static readonly Pen SetOneEdge = Pen(SetOne, 15), SetTwoEdge = Pen(SetTwo, 15);
    public TreeViewport()
    {
        ClipToBounds = true; Focusable = true; Cursor = Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        SizeChanged += (_, _) => { if (_fitPending && ActualWidth > 1 && ActualHeight > 1) { _fitPending = false; Reset(); } InvalidateVisual(); };
    }
    private static void ModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (TreeViewport)d;
        if (e.OldValue is TreeViewModel old) { old.StateChanged -= view.UpdateState; old.FocusRequested -= view.FocusNode; }
        if (e.NewValue is TreeViewModel next) { next.StateChanged += view.UpdateState; next.FocusRequested += view.FocusNode; }
        view.UpdateState();
    }
    private void UpdateState()
    {
        if (_catalog != Model?.Catalog)
        {
            _fitPending = false;
            if (_catalog is not null) _views[_catalog.DatasetId] = (_center, Zoom);
            _catalog = Model?.Catalog; _edges.Clear(); _icons.Clear(); _frames.Clear(); _atlas = null;
            if (_catalog is not null)
            {
                if (_views.TryGetValue(_catalog.DatasetId, out var view)) { _center = view.Center; Zoom = view.Zoom; }
                else if (_catalog.IsAscendancyGraph) { _fitPending = ActualWidth <= 1 || ActualHeight <= 1; if (!_fitPending) Reset(); }
                else { var start = _catalog.Classes.FirstOrDefault(c => c.Index == Model!.Plan.ClassIndex); if (start is not null) { var n = _catalog.Nodes[start.StartNodeId]; _center = new(n.X, n.Y); Zoom = 0.13; } }
                foreach (var e in _catalog.Edges) _edges.Add((e, MakeEdge(e)));
                try { LoadAtlas(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
                { Model?.ReportAtlasError(e.Message); }
            }
        }
        if (_catalog is not null && Model is not null)
        {
            var plan = Model.Plan;
            _descriptions = _catalog.Nodes.Values.ToDictionary(n => n.Id, n => _catalog.Describe(n.Id, plan));
            _socketed = Model.JewelSockets.Where(s => s.JewelId is not null).Select(s => s.NodeId).ToHashSet();
            _weaponSets = Model.WeaponSetNodes; _activeWeaponSet = Model.ActiveWeaponSet;
            _start = _catalog.Classes.FirstOrDefault(c => c.Index == plan.ClassIndex)?.StartNodeId;
            if (_portraitKey != Model.PortraitKey)
            {
                _portraitKey = Model.PortraitKey; _portrait = null;
                try
                {
                    var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(Path.Combine(AppContext.BaseDirectory, "Data", "Tree", "Portraits", _portraitKey + ".jpg"));
                    image.EndInit(); image.Freeze(); _portrait = image;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
                { Model.ReportAtlasError(e.Message); }
            }
            if (_catalog.IsAscendancyGraph)
            {
                double minX = _catalog.Nodes.Values.Min(n => n.X), maxX = _catalog.Nodes.Values.Max(n => n.X), minY = _catalog.Nodes.Values.Min(n => n.Y), maxY = _catalog.Nodes.Values.Max(n => n.Y);
                double size = Math.Max(maxX - minX, maxY - minY) * 1.2;
                _portraitRect = new((minX + maxX - size) / 2, (minY + maxY - size) / 2, size, size);
            }
            else _portraitRect = new(-1400, -1400, 2800, 2800);
        }
        InvalidateVisual();
    }
    private void LoadAtlas()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "Data", "Tree");
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(Path.Combine(folder, "skills.png")); bitmap.EndInit(); bitmap.Freeze(); _atlas = bitmap;
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "skills.json")));
        foreach (var p in json.RootElement.GetProperty("frames").EnumerateObject())
        {
            var f = p.Value.GetProperty("frame");
            _frames[p.Name] = new(f.GetProperty("x").GetInt32(), f.GetProperty("y").GetInt32(), f.GetProperty("w").GetInt32(), f.GetProperty("h").GetInt32());
        }
    }
    private Geometry MakeEdge(TreeEdge edge)
    {
        var a = _catalog!.Nodes[edge.From]; var b = _catalog.Nodes[edge.To];
        var points = new List<Point>();
        if (edge.CenterX is double cx && edge.CenterY is double cy)
        {
            double r1 = Math.Sqrt(Math.Pow(a.X - cx, 2) + Math.Pow(a.Y - cy, 2)), r2 = Math.Sqrt(Math.Pow(b.X - cx, 2) + Math.Pow(b.Y - cy, 2));
            if (r1 > 1 && Math.Abs(r1 - r2) < 8)
            {
                double angle = Math.Atan2(a.Y - cy, a.X - cx), delta = Math.Atan2(b.Y - cy, b.X - cx) - angle;
                if (delta > Math.PI) delta -= 2 * Math.PI; if (delta < -Math.PI) delta += 2 * Math.PI;
                int segments = Math.Clamp((int)(Math.Abs(delta) * r1 / 55), 2, 48);
                for (int i = 1; i < segments; i++) { double t = angle + delta * i / segments; points.Add(new(cx + r1 * Math.Cos(t), cy + r1 * Math.Sin(t))); }
            }
        }
        points.Add(new(b.X, b.Y));
        var geometry = new StreamGeometry(); using (var context = geometry.Open()) { context.BeginFigure(new(a.X, a.Y), false, false); context.PolyLineTo(points, true, false); }
        geometry.Freeze(); return geometry;
    }
    private BitmapSource? Icon(PassiveNode n)
    {
        if (_atlas is null || !_descriptions.TryGetValue(n.Id, out var description)) return null;
        string key = (n.IsKeystone ? "keystoneActive:" : n.IsNotable ? "notableActive:" : "normalActive:") + description.Icon;
        if (_icons.TryGetValue(key, out var bitmap)) return bitmap;
        if (!_frames.TryGetValue(key, out var frame))
        {
            // Some ascendancy variants use a different sprite size than their base graph frame.
            key = new[] { "normalActive:", "notableActive:", "keystoneActive:" }.Select(p => p + description.Icon).FirstOrDefault(_frames.ContainsKey) ?? "";
            if (!_frames.TryGetValue(key, out frame)) return null;
        }
        var crop = new CroppedBitmap(_atlas, frame); crop.Freeze(); _icons.Add(key, crop); return crop;
    }
    private bool Owned(int id) => id == _start || Model?.Allocated.Contains(id) == true;
    private static double Radius(PassiveNode n) => n.IsStart ? 160 : n.IsKeystone ? 110 : n.IsNotable ? 85 : n.IsJewel ? 77 : 58;
    private Point Screen(double x, double y) => new((x - _center.X) * Zoom + ActualWidth / 2, (y - _center.Y) * Zoom + ActualHeight / 2);
    private Point World(Point p) => new((p.X - ActualWidth / 2) / Zoom + _center.X, (p.Y - ActualHeight / 2) / Zoom + _center.Y);
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackgroundInk, null, new Rect(RenderSize));
        if (_catalog is null || Model is null) return;
        double left = _center.X - ActualWidth / Zoom / 2 - 240, right = _center.X + ActualWidth / Zoom / 2 + 240;
        double top = _center.Y - ActualHeight / Zoom / 2 - 240, bottom = _center.Y + ActualHeight / Zoom / 2 + 240;
        var visible = new Rect(new Point(left, top), new Point(right, bottom));
        dc.PushTransform(new MatrixTransform(Zoom, 0, 0, Zoom, ActualWidth / 2 - _center.X * Zoom, ActualHeight / 2 - _center.Y * Zoom));
        if (_portrait is not null)
        {
            dc.PushOpacity(_catalog.IsAscendancyGraph ? 0.50 : 0.92);
            dc.DrawImage(_portrait, _portraitRect); dc.Pop();
        }
        foreach (var (edge, shape) in _edges)
        {
            if (!visible.IntersectsWith(shape.Bounds)) continue;
            bool allocated = Owned(edge.From) && Owned(edge.To);
            bool preview = (Model.Preview.Contains(edge.From) || Owned(edge.From)) && (Model.Preview.Contains(edge.To) || Owned(edge.To)) && !allocated;
            // PoB2 paints a connector by the allocation mode of either endpoint (Classes/PassiveTreeView.lua:724-730),
            // so a weapon-set cluster is visible as a red (set I) or green (set II) branch of the tree.
            int edgeSet = _weaponSets.TryGetValue(edge.From, out var fromSet) ? fromSet : _weaponSets.GetValueOrDefault(edge.To);
            var edgePen = allocated
                ? edgeSet == 1 ? SetOneEdge : edgeSet == 2 ? SetTwoEdge : ActiveEdge
                : preview ? PreviewEdge : !_catalog.Nodes[edge.From].IsSupported || !_catalog.Nodes[edge.To].IsSupported ? LockedEdge : IdleEdge;
            dc.DrawGeometry(null, edgePen, shape);
        }
        // A socketed jewel with a stated radius draws its reach, exactly like PoB2 does around its sockets
        // (classes/PassiveTreeView draws the radius circle of the selected/hovered socket; ours shows every
        // filled socket so the two Time-Lost jewels of a build are visible at once).
        foreach (int socketId in _socketed)
        {
            if (!_catalog.Nodes.TryGetValue(socketId, out var socket)) continue;
            double radius = JewelRadius.OuterRadius(Model.JewelRadiusProvider?.Invoke(socketId) ?? 0);
            if (radius <= 0 || socket.X < left - radius || socket.X > right + radius || socket.Y < top - radius || socket.Y > bottom + radius) continue;
            dc.DrawEllipse(null, Pen(JewelRadiusInk, Math.Max(4, 6 / Zoom)), new Point(socket.X, socket.Y), radius, radius);
        }
        foreach (var node in _catalog.Nodes.Values)
        {
            if (node.X < left || node.X > right || node.Y < top || node.Y > bottom) continue;
            double r = Radius(node); var position = new Point(node.X, node.Y);
            bool active = Owned(node.Id), selected = Model.SelectedId == node.Id, preview = Model.Preview.Contains(node.Id), match = Model.SearchMatches.Contains(node.Id);
            if (selected || match) dc.DrawEllipse(null, Pen(selected ? Gold : Cyan, 2 / Zoom), position, r + 24, r + 24);
            // A weapon-set node carries its set's colour instead of the gold "allocated" ring, so the two
            // clusters of a set-swapping build are told apart at a glance (PoB2 does the same).
            int weaponSet = _weaponSets.GetValueOrDefault(node.Id);
            var nodePen = active
                ? weaponSet == 1 ? Pen(SetOne, 14) : weaponSet == 2 ? Pen(SetTwo, 14) : Pen(Gold, 14)
                : Pen(preview ? Cyan : node.IsSupported ? Bronze : Dim, 9);
            dc.DrawEllipse(NodeInk, nodePen, position, r, r);
            // A jewel socket that actually holds a jewel is drawn like PoB2: a bright ring plus a gem
            // mark, so the sockets the build uses are visible without hovering.
            if (node.IsJewel && _socketed.Contains(node.Id))
            {
                dc.DrawEllipse(null, Pen(Cyan, 20 / Zoom + 6), position, r * 0.82, r * 0.82);
                var gem = new StreamGeometry();
                using (var ctx = gem.Open())
                {
                    ctx.BeginFigure(new Point(position.X, position.Y - r * 0.5), true, true);
                    ctx.LineTo(new Point(position.X + r * 0.42, position.Y), true, false);
                    ctx.LineTo(new Point(position.X, position.Y + r * 0.5), true, false);
                    ctx.LineTo(new Point(position.X - r * 0.42, position.Y), true, false);
                }
                gem.Freeze();
                dc.DrawGeometry(Cyan, null, gem);
            }
            if (Zoom >= 0.035 && Icon(node) is BitmapSource bitmap)
            {
                dc.PushOpacity(active || selected ? 1 : node.IsSupported ? 0.65 : 0.25);
                double size = r * 1.42; dc.DrawImage(bitmap, new Rect(node.X - size / 2, node.Y - size / 2, size, size)); dc.Pop();
            }
            if (!node.IsSupported && Zoom > 0.09) dc.DrawLine(Pen(Dim, 13), new(node.X - r * 0.65, node.Y + r * 0.65), new(node.X + r * 0.65, node.Y - r * 0.65));
        }
        dc.Pop();
        foreach (var node in _catalog.Nodes.Values.Where(n => n.IsStart))
        {
            Point p = Screen(node.X, node.Y);
            if (p.X < 0 || p.Y < 0 || p.X > ActualWidth || p.Y > ActualHeight) continue;
            var text = new FormattedText(_descriptions[node.Id].Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 11, node.Id == _start ? Gold : TextInk, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(p.X - text.Width / 2, p.Y + Radius(node) * Zoom + 5));
        }
        if (IsKeyboardFocused) dc.DrawRectangle(null, Pen(Bronze, 1), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));
        DrawWeaponSetLegend(dc);
    }
    /// <summary>Legend for PoB2's weapon-set colours (set I red, set II green) with the set that is currently
    /// in hand marked, because only its nodes contribute to the numbers.</summary>
    private void DrawWeaponSetLegend(DrawingContext dc)
    {
        if (Model is null || _catalog is null || _weaponSets.Count == 0 || ActualWidth < 200) return;
        double pixels = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var rows = new (string Label, Brush Swatch, bool Active)[]
        {
            (Model.L["WeaponSet1"], SetOne, _activeWeaponSet == 1),
            (Model.L["WeaponSet2"], SetTwo, _activeWeaponSet == 2)
        };
        string hint = Model.L["WeaponSetLegend"];
        var hintText = Text(hint, 10, Bronze, pixels);
        var labels = rows.Select(r => Text(r.Label + (r.Active ? " · " + Model.L["WeaponSetActive"] : ""), 11, TextInk, pixels)).ToArray();
        double width = Math.Max(hintText.Width, labels.Max(t => t.Width) + 22) + 20;
        double height = rows.Length * 18 + 26;
        double left = 12, top = Math.Max(12, ActualHeight - height - 12);
        dc.DrawRoundedRectangle(Brush("#CC0D1319"), Pen(Bronze, 1), new Rect(left, top, width, height), 6, 6);
        double y = top + 8;
        for (int i = 0; i < rows.Length; i++)
        {
            dc.DrawRectangle(rows[i].Swatch, Pen(Bronze, 1), new Rect(left + 8, y + 2, 10, 10));
            dc.DrawText(labels[i], new Point(left + 24, y));
            y += 18;
        }
        dc.DrawText(hintText, new Point(left + 8, y + 2));
    }
    private static FormattedText Text(string value, double size, Brush brush, double pixels) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, pixels);
    public void Reset()
    {
        if (_catalog is null) return;
        double minX = _catalog.Nodes.Values.Min(n => n.X), maxX = _catalog.Nodes.Values.Max(n => n.X), minY = _catalog.Nodes.Values.Min(n => n.Y), maxY = _catalog.Nodes.Values.Max(n => n.Y);
        _center = new((minX + maxX) / 2, (minY + maxY) / 2);
        Zoom = Math.Clamp(Math.Min(Math.Max(100, ActualWidth - 60) / Math.Max(800, maxX - minX + 500), Math.Max(100, ActualHeight - 60) / Math.Max(800, maxY - minY + 500)), 0.015, 0.6); InvalidateVisual();
    }
    public void FocusNode(int id)
    {
        if (_catalog is null || !_catalog.Nodes.TryGetValue(id, out var node)) return;
        _center = new(node.X, node.Y); Zoom = Math.Max(Zoom, 0.13); InvalidateVisual();
    }
    public void AdjustZoom(double factor) => ZoomAt(factor, new(ActualWidth / 2, ActualHeight / 2));
    private void ZoomAt(double factor, Point position)
    {
        var before = World(position); Zoom = Math.Clamp(Zoom * factor, 0.015, 0.6); var after = World(position);
        _center = new(_center.X + before.X - after.X, _center.Y + before.Y - after.Y); InvalidateVisual();
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e) { ZoomAt(e.Delta > 0 ? 1.17 : 1 / 1.17, e.GetPosition(this)); e.Handled = true; }
    private int? Hit(Point point)
    {
        if (_catalog is null) return null;
        var world = World(point); double closest = double.MaxValue; int? result = null;
        foreach (var node in _catalog.Nodes.Values)
        {
            double dx = node.X - world.X, dy = node.Y - world.Y, d = dx * dx + dy * dy;
            double r = Math.Max(Radius(node), 5 / Zoom);
            if (d <= r * r && d < closest) { closest = d; result = node.Id; }
        }
        return result;
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        Focus(); _lastDrag = _dragStart = e.GetPosition(this); _dragged = false; CaptureMouse();
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left && Hit(e.GetPosition(this)) is int id)
        { Model?.Select(id); if (Model?.AllocateCommand.CanExecute(null) == true) Model.AllocateCommand.Execute(null); }
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var current = e.GetPosition(this);
        if (_lastDrag is Point last && _dragStart is Point start)
        {
            if ((current - start).Length > 4) _dragged = true;
            if (_dragged) { var delta = current - last; _center = new(_center.X - delta.X / Zoom, _center.Y - delta.Y / Zoom); InvalidateVisual(); Cursor = Cursors.SizeAll; }
            _lastDrag = current; return;
        }
        int? hit = Hit(current); Cursor = hit.HasValue ? Cursors.Hand : Cursors.Cross;
        if (hit != _hover)
        {
            _hover = hit;
            if (_hover is int hovered && _descriptions.TryGetValue(hovered, out var info))
            {
                // A jewel socket reports the socketed jewel's own affixes (PoB2 shows the jewel here,
                // together with the radius it would use once the radius model is in place).
                string? socketInfo = _socketed.Contains(hovered) || (_catalog?.Nodes[hovered].IsJewel ?? false)
                    ? Model?.SocketInfoProvider?.Invoke(hovered) : null;
                var impact = Model?.NodeImpactProvider?.Invoke(hovered);
                string weaponSet = Model?.WeaponSetNote(hovered) ?? "";
                ToolTip = new TextBlock
                {
                    Text = info.Name + "\n\n" + string.Join("\n", info.Stats)
                        + (weaponSet.Length == 0 ? "" : "\n\n" + weaponSet)
                        + (string.IsNullOrEmpty(socketInfo) ? "" : "\n\n" + socketInfo)
                        + (string.IsNullOrEmpty(impact) ? "" : "\n\n" + (Model?.L["NodeImpactDefault"] ?? "Allocating adds:") + "\n" + impact),
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 420
                };
            }
            else ToolTip = null;
        }
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        if (!_dragged && e.ChangedButton == MouseButton.Left && Hit(e.GetPosition(this)) is int id) Model?.Select(id);
        _dragStart = _lastDrag = null; ReleaseMouseCapture(); Cursor = Cursors.Cross; e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { _dragStart = _lastDrag = null; base.OnLostMouseCapture(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        ICommand? command = e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control ? Model?.UndoCommand : e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control ? Model?.RedoCommand : e.Key == Key.Delete ? Model?.RefundCommand : e.Key == Key.Enter ? Model?.AllocateCommand : null;
        if (command?.CanExecute(null) == true) { command.Execute(null); e.Handled = true; }
        base.OnKeyDown(e);
    }
}
