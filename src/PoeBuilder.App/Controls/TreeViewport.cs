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
    /// <summary>
    /// The node tooltip the game shows on hover, built once per hovered node and painted in screen space: a
    /// framed panel with the node's name, its modifier lines, its state and the click hints. It replaced the
    /// WPF <c>ToolTip</c>, which appeared only after the system's own delay, was styled by the system and
    /// could not carry the game's frame.
    /// </summary>
    private string _tipTitle = "", _tipState = "";
    private bool _tipAllocated, _tipHints;
    private readonly List<(string Text, Brush Ink, double Size)> _tipRows = [];
    private Point _pointer;
    private TreeCatalog? _catalog;
    private readonly Dictionary<string, (Point Center, double Zoom)> _views = [];
    private BitmapSource? _portrait;
    private string _portraitKey = "";
    private Rect _portraitRect;
    private bool _fitPending;
    private readonly Dictionary<string, BitmapSource> _icons = [];
    private readonly Dictionary<string, Int32Rect> _frames = [];
    /// <summary>PoB2's node frame art, one PNG per atlas slice (see <see cref="TreeFrameArt"/>).</summary>
    private readonly Dictionary<string, BitmapSource> _frameArt = [];
    private BitmapSource? _atlas;
    private Dictionary<int, PassiveVariant> _descriptions = [];
    private HashSet<int> _socketed = [];
    /// <summary>Reach of each filled socket, computed once per state change: the provider can walk the
    /// whole radius model, and asking it per socket on every frame was part of the panning stutter.</summary>
    private Dictionary<int, double> _jewelRadii = [];
    private IReadOnlyDictionary<int, int> _weaponSets = new Dictionary<int, int>();
    private int _activeWeaponSet = 1;
    private readonly List<EdgeDraw> _edges = [];
    /// <summary>A connection with everything the art needs, precomputed once per data set:
    /// <c>Kind</c> and <c>Orbit</c> decide the sprite, the rest is placement. <c>Pieces</c> holds the
    /// per-quarter clip geometry, because building it inside the render pass allocated a new
    /// <c>StreamGeometry</c> for every visible connection on every frame — that is what made panning
    /// stutter.</summary>
    private sealed record EdgeDraw(TreeEdge Edge, Geometry Shape, ConnectionKind Kind, int Orbit, Point Centre,
        double Radius, double StartAngle, double Sweep, Point Start, double Length)
    {
        /// <summary>The angular pieces a single 90° sprite cannot cover, with their clips ready.</summary>
        public List<(Geometry Clip, double Start, double Sweep)> Pieces { get; } = [];
    }
    private readonly Dictionary<string, BitmapSource> _orbitArt = [];
    /// <summary>Class and ascendancy backdrops (TreeClassArtTable).</summary>
    private readonly Dictionary<string, BitmapSource> _classArt = [];
    /// <summary>PoB2's node effect art, one PNG per mastery pattern (see <see cref="TreeEffectArt"/>).</summary>
    private readonly Dictionary<string, BitmapSource> _effectArt = [];
    /// <summary>PoB2's background texture (<c>Background2</c>), drawn in screen space under everything.</summary>
    private BitmapSource? _background;
    /// <summary>The corner of <see cref="_background"/> PoB2 samples, cut for the viewport size it was made
    /// for: PoB2 asks for a crop of <c>width / 100</c> by <c>height / 100</c> pixels stretched over the
    /// whole viewport (<c>Classes/PassiveTreeView.lua:575-582</c>), which is why its background reads as a
    /// soft dark field rather than a texture. Cutting it per frame would allocate on every pan, so the
    /// result is kept until the viewport changes size.</summary>
    private BitmapSource? _backgroundCrop;
    private Size _backgroundCropSize;
    private TreeClassArt? _classCentre;
    /// <summary>The ascendancy PoB2 draws bright while the other circles are dimmed
    /// (<c>Classes/PassiveTreeView.lua:633-637</c>): the build's choice, or the graph on screen.</summary>
    private string _currentAscendancy = "";
    private int? _start;
    private static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Brush BackgroundInk = Brush("#0D1319"), NodeInk = Brush("#080C10"), Gold = Brush("#D9B576"), Bronze = Brush("#6D5940"), Dim = Brush("#303A42"), Cyan = Brush("#77C9CE"), TextInk = Brush("#CCD2D6");
    // The hover tooltip's own inks: the game prints the node's name in bone white on a framed header, its
    // modifier lines in the modifier blue, its state in grey and the key hints in the panel's bronze.
    private static readonly Brush TipInk = Brush("#EFE6D2"), TipStat = Brush("#9CB2FF"), TipNote = Brush("#98A2AC"), TipHint = Brush("#B99C6B"), TipBack = Brush("#F20A0E13"), TipHead = Brush("#FF151A21");
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
    /// <summary>Pens that keep a constant screen thickness carry a zoom-dependent width, and the render
    /// pass used to allocate one or two frozen pens for every visible node on every frame — that is what
    /// made panning and zooming stutter. They are now cached per distinct width.</summary>
    private readonly Dictionary<(Brush Brush, double Width), Pen> _penCache = [];
    private Pen CachedPen(Brush brush, double width)
    {
        var key = (Brush: brush, Width: Math.Round(width, 2));
        if (_penCache.TryGetValue(key, out var pen)) return pen;
        pen = Pen(key.Brush, key.Width);
        _penCache[key] = pen;
        return pen;
    }
    public TreeViewport()
    {
        ClipToBounds = true; Focusable = true; Cursor = Cursors.Cross;
        // Linear instead of HighQuality: the tree draws hundreds of small sprites per frame, and WPF's
        // high-quality scaling re-samples every one of them on every pan/zoom step, which is the other
        // half of the stutter. Linear is visually indistinguishable here and much cheaper.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
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
            _catalog = Model?.Catalog; _edges.Clear(); _icons.Clear(); _frames.Clear(); _frameArt.Clear(); _orbitArt.Clear(); _classArt.Clear(); _effectArt.Clear(); _background = null; _backgroundCrop = null; _classCentre = null; _atlas = null;
            if (_catalog is not null)
            {
                if (_views.TryGetValue(_catalog.DatasetId, out var view)) { _center = view.Center; Zoom = view.Zoom; }
                else if (_catalog.IsAscendancyGraph) { _fitPending = ActualWidth <= 1 || ActualHeight <= 1; if (!_fitPending) Reset(); }
                else { var start = _catalog.Classes.FirstOrDefault(c => c.Index == Model!.Plan.ClassIndex); if (start is not null) { var n = _catalog.Nodes[start.StartNodeId]; _center = new(n.X, n.Y); Zoom = 0.13; } }
                foreach (var e in _catalog.Edges) _edges.Add(MakeEdgeDraw(e));
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
            _jewelRadii = _socketed.ToDictionary(id => id,
                id => JewelRadius.OuterRadius(Model.JewelRadiusProvider?.Invoke(id) ?? 0));
            _weaponSets = Model.WeaponSetNodes; _activeWeaponSet = Model.ActiveWeaponSet;
            _start = _catalog.Classes.FirstOrDefault(c => c.Index == plan.ClassIndex)?.StartNodeId;
            // PoB2 puts the chosen ascendancy's own art at the class centre (:592-602) and leaves the class
            // art there while no ascendancy is chosen; the ring circles are drawn either way, and the chosen
            // one stays bright while the others are dimmed (:633-637).
            string chosen = _catalog.IsAscendancyGraph
                ? _catalog.Classes.FirstOrDefault()?.Name ?? ""
                : Model.SelectedAscendancy?.Name ?? "";
            _currentAscendancy = chosen;
            _classCentre = chosen.Length > 0 && TreeClassArtTable.Ascendancy(chosen) is { } ascendancyArt
                ? ascendancyArt with { X = 0, Y = 0 }
                : TreeClassArtTable.Class(_catalog.Classes.FirstOrDefault(c => c.Index == plan.ClassIndex)?.Name ?? "");
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
            else _portraitRect = new(-TreeClassArtTable.CentreSize / 2, -TreeClassArtTable.CentreSize / 2, TreeClassArtTable.CentreSize, TreeClassArtTable.CentreSize);
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
        // PoB2's node frames: one PNG per atlas slice, named by the sprite name its tree.json uses
        // (PSSkillFrame, NotableFrameAllocated, …). A missing folder simply leaves the frame fallback
        // in place, so an old data set keeps rendering a usable tree.
        string artFolder = Path.Combine(folder, "Art");
        if (!Directory.Exists(artFolder)) return;
        foreach (string file in Directory.EnumerateFiles(artFolder, "*.png", SearchOption.AllDirectories))
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(file); image.EndInit(); image.Freeze();
            // The folder says what a sprite is ("orbit" for connectors, "class" for backdrops) and so does
            // the sprite's own name ("BGTree", "ClassesWarrior"). Routing by both keeps an older data
            // folder — one where the backdrops still sat beside the node frames — drawing real backdrops
            // instead of silently treating them as frames.
            string name = Path.GetFileNameWithoutExtension(file);
            string group = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
            bool connector = group.Equals("orbit", StringComparison.OrdinalIgnoreCase);
            bool backdrop = group.Equals("class", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Classes", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("BGTree", StringComparison.OrdinalIgnoreCase);
            bool effect = group.Equals("effect", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Mastery", StringComparison.OrdinalIgnoreCase);
            if (name.Equals("Background2", StringComparison.OrdinalIgnoreCase)) { _background = image; continue; }
            var target = connector ? _orbitArt : backdrop ? _classArt : effect ? _effectArt : _frameArt;
            target[name] = image;
        }
    }
    /// <summary>Precomputes one connection: the stroked geometry kept as the fallback for a data set
    /// without connector sprites, plus PoB2's own classification — an arc when both endpoints share an
    /// orbit around a group centre (<c>Classes/PassiveTree.lua:640-680</c>), a straight segment
    /// otherwise (<c>:683-699</c>). For a line, <c>StartAngle</c> carries the direction and
    /// <c>Length</c> the distance; for an arc, <c>Centre</c>/<c>Radius</c>/<c>StartAngle</c>/<c>Sweep</c>.</summary>
    private EdgeDraw MakeEdgeDraw(TreeEdge edge)
    {
        var a = _catalog!.Nodes[edge.From]; var b = _catalog.Nodes[edge.To];
        var points = new List<Point>();
        var kind = ConnectionKind.Line;
        int orbit = 0;
        var centre = new Point();
        double radius = 0, startAngle = 0, sweep = 0;
        if (edge.CenterX is double cx && edge.CenterY is double cy)
        {
            double r1 = Math.Sqrt(Math.Pow(a.X - cx, 2) + Math.Pow(a.Y - cy, 2)), r2 = Math.Sqrt(Math.Pow(b.X - cx, 2) + Math.Pow(b.Y - cy, 2));
            if (r1 > 1 && Math.Abs(r1 - r2) < 8)
            {
                double angle = Math.Atan2(a.Y - cy, a.X - cx), delta = Math.Atan2(b.Y - cy, b.X - cx) - angle;
                if (delta > Math.PI) delta -= 2 * Math.PI; if (delta < -Math.PI) delta += 2 * Math.PI;
                int segments = Math.Clamp((int)(Math.Abs(delta) * r1 / 55), 2, 48);
                for (int i = 1; i < segments; i++) { double t = angle + delta * i / segments; points.Add(new(cx + r1 * Math.Cos(t), cy + r1 * Math.Sin(t))); }
                kind = ConnectionKind.Orbit;
                orbit = TreeConnectionArt.OrbitForRadius(r1);
                centre = new Point(cx, cy); radius = r1; startAngle = angle; sweep = delta;
            }
        }
        points.Add(new(b.X, b.Y));
        var geometry = new StreamGeometry(); using (var context = geometry.Open()) { context.BeginFigure(new(a.X, a.Y), false, false); context.PolyLineTo(points, true, false); }
        geometry.Freeze();
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        if (kind == ConnectionKind.Line) startAngle = Math.Atan2(dy, dx);
        var draw = new EdgeDraw(edge, geometry, kind, orbit, centre, radius, startAngle, sweep, new Point(a.X, a.Y), length);
        // The clip geometry of every quarter piece is built once here, not in the render pass.
        if (kind == ConnectionKind.Orbit)
            foreach (var (pieceStart, pieceSweep) in TreeConnectionArt.SplitArc(startAngle, sweep))
                draw.Pieces.Add((Wedge(centre, pieceStart, pieceSweep, radius * 1.15), pieceStart, pieceSweep));
        return draw;
    }

    /// <summary>PoB2's own background: one crop of <c>Background2</c> stretched over the whole viewport in
    /// screen space, so it stays put while the tree pans (<c>Classes/PassiveTreeView.lua:575-582</c>).
    /// The crop is the corner PoB2 asks for and is cached, because cutting a bitmap allocates.</summary>
    private void DrawBackground(DrawingContext dc)
    {
        if (_background is null || ActualWidth < 1 || ActualHeight < 1) return;
        var size = new Size(Math.Round(ActualWidth), Math.Round(ActualHeight));
        if (_backgroundCrop is null || _backgroundCropSize != size)
        {
            // PoB2 samples width/100 by height/100 pixels of the full 1024 px art; the shipped copy is a
            // quarter of that, so the same fraction of the image is the same region of the original.
            double share = _background.PixelWidth / 1024.0;
            int width = Math.Clamp((int)Math.Ceiling(size.Width / 100 * share), 1, _background.PixelWidth);
            int height = Math.Clamp((int)Math.Ceiling(size.Height / 100 * share), 1, _background.PixelHeight);
            var crop = new CroppedBitmap(_background, new Int32Rect(0, 0, width, height));
            crop.Freeze(); _backgroundCrop = crop; _backgroundCropSize = size;
        }
        dc.DrawImage(_backgroundCrop, new Rect(RenderSize));
    }

    /// <summary>A pie slice used to clip one arc piece to its own span. The sprite covers a full quarter
    /// circle, so a shorter connection has to be cut down; PoB2 clips by moving the quad's corners
    /// (<c>PassiveTree.lua:710-728</c>), and clipping the geometry is the exact version of that idea.</summary>
    private static Geometry Wedge(Point centre, double start, double sweep, double radius)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(centre, true, true);
            int steps = Math.Max(2, (int)(Math.Abs(sweep) * radius / 40));
            var rim = new List<Point>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                double t = start + sweep * i / steps;
                rim.Add(new(centre.X + radius * Math.Cos(t), centre.Y + radius * Math.Sin(t)));
            }
            context.PolyLineTo(rim, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Draws a connection with PoB2's own sprite, returning false when it is not available so
    /// the caller can keep the stroked geometry.</summary>
    private bool DrawConnectionArt(DrawingContext dc, EdgeDraw draw, ConnectionState state)
    {
        string? file = TreeConnectionArt.File(draw.Kind, draw.Orbit, state);
        if (file is null || !_orbitArt.TryGetValue(file, out var art)) return false;
        if (draw.Kind == ConnectionKind.Line)
        {
            // PoB2 lays the strip along the segment and repeats it every art.width pixels
            // (endS = distance / art.width, PassiveTree.lua:685-697): a tiled brush on a rotated
            // rectangle is the same thing, and it keeps the texture's own orientation.
            double width = art.PixelWidth, height = art.PixelHeight;
            var brush = new ImageBrush(art)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, width, height),
                Stretch = Stretch.Fill,
            };
            brush.Freeze();
            // The placement comes from the core, where a test can check it: a rotation about the
            // segment's first endpoint.
            var turn = TreeConnectionArt.LinePlacement(draw.Start.X, draw.Start.Y, draw.StartAngle);
            dc.PushTransform(new MatrixTransform(ToMatrix(turn)));
            dc.DrawRectangle(brush, null, new Rect(draw.Start.X, draw.Start.Y - height / 2, draw.Length, height));
            dc.Pop();
            return true;
        }
        // An orbit sprite's arc centre is its bottom-right pixel and its radius is the measured one, so
        // the art is rotated about that pixel and scaled onto the catalogue's circle. The pieces (and
        // their clips) were computed once per data set, so nothing is allocated per frame here.
        foreach (var (clip, pieceStart, pieceSweep) in draw.Pieces)
        {
            var placement = TreeConnectionArt.OrbitPlacement(file, draw.Radius, pieceStart + pieceSweep / 2,
                draw.Centre.X, draw.Centre.Y, art.PixelWidth, art.PixelHeight);
            dc.PushClip(clip);
            dc.PushTransform(new MatrixTransform(ToMatrix(placement)));
            dc.DrawImage(art, new Rect(0, 0, art.PixelWidth, art.PixelHeight));
            dc.Pop();
            dc.Pop();
        }
        return true;
    }

    /// <summary>
    /// Calls a tooltip provider and swallows the one error a tooltip has no answer for: a saved plan that
    /// fails the tree's own rules makes every plan walk throw, and a mouse move must not take the window
    /// down because of it. The rest of the tooltip (the node's own name and stats) still shows, and the tree
    /// reports the invalid state itself.
    /// </summary>
    private static string? Safe(Func<int, string?>? provider, int id)
    {
        if (provider is null) return null;
        try { return provider(id); }
        catch (TreeRuleException) { return null; }
    }

    /// <summary>
    /// Collects everything the hovered node's tooltip shows: the node's own name and modifier lines, the
    /// weapon set it is allocated for, the jewel in a socket, and the effect it has on the character sheet.
    /// Same providers the WPF tooltip used — the panel is simply ours now, so it can look like the game's and
    /// appear at once.
    /// </summary>
    private void BuildTooltip(int id)
    {
        _tipTitle = ""; _tipState = ""; _tipRows.Clear();
        if (Model is null || !_descriptions.TryGetValue(id, out var info)) return;
        _tipAllocated = Owned(id);
        _tipTitle = info.Name;
        _tipState = Model.L[_tipAllocated ? "TreeTipAllocated" : "TreeTipUnallocated"];
        foreach (var stat in info.Stats) _tipRows.Add((stat, TipStat, 13));
        // A provider walks the plan (and the jewels in it), so a saved state that fails the tree's own rules
        // must report nothing here instead of taking the window down on a mouse move.
        void Extra(string? text, Brush ink)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
                if (line.Trim().Length > 0) _tipRows.Add((line.Trim(), ink, 11.5));
        }
        Extra(Model.WeaponSetNote(id), TipHint);
        Extra((_socketed.Contains(id) || (_catalog?.Nodes[id].IsJewel ?? false)) ? Safe(Model.SocketInfoProvider, id) : null, TipNote);
        Extra(Safe(Model.NodeImpactProvider, id), TipNote);
        _tipHints = Model.CanModify && _catalog?.Nodes[id].IsStart != true;
    }

    /// <summary>
    /// Draws the hover tooltip in screen space, next to the pointer and kept inside the viewport: a framed
    /// panel whose header carries the node's name, then its modifier lines, its state ("Not allocated") and
    /// the key hints — the layout the game's own passive-tree tooltip uses.
    /// </summary>
    private void DrawNodeTooltip(DrawingContext dc)
    {
        if (Model is null || _tipTitle.Length == 0 || ActualWidth < 40 || ActualHeight < 40) return;
        double pixels = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double maxWidth = Math.Min(340, Math.Max(160, ActualWidth - 24));
        FormattedText Make(string text, double size, Brush brush, bool bold = false, TextAlignment alignment = TextAlignment.Left) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
                size, brush, pixels) { MaxTextWidth = maxWidth, TextAlignment = alignment };
        var title = Make(_tipTitle, 15.5, TipInk, bold: true, TextAlignment.Center);
        var state = Make(_tipState, 11.5, _tipAllocated ? Gold : TipNote);
        var rows = new List<FormattedText>();
        foreach (var (text, ink, size) in _tipRows) rows.Add(Make(text, size, ink));
        var hints = _tipHints ? new[]
        {
            Make(Model.L["TreeTipKeyLeft"], 11.5, TipHint),
            Make(Model.L["TreeTipKeyShiftLeft"], 11.5, TipHint),
            Make(Model.L["TreeTipKeyShiftRight"], 11.5, TipHint),
            Make(Model.L["TreeTipKeyRight"], 11.5, TipHint)
        } : [];
        double header = title.Height + 16;
        // The hints block is a caption line plus the four key lines, and the panel has to be tall enough for
        // all of them: reserving only the lines cut the last hint in half.
        double body = 12 + rows.Sum(r => r.Height + 3) + 10 + state.Height + (hints.Length == 0 ? 0 : 24 + hints.Sum(h => h.Height + 2));
        double width = Math.Min(maxWidth + 26, Math.Max(title.Width, Math.Max(state.Width, Math.Max(rows.Count == 0 ? 0 : rows.Max(r => r.Width), hints.Length == 0 ? 0 : hints.Max(h => h.Width)))) + 26);
        double height = header + Math.Max(24, body);
        // The pointer is the anchor: the panel opens to its lower right and is pushed back inside the
        // viewport when it would leave it, which is what the game does at the screen's edge.
        double left = _pointer.X + 18, top = _pointer.Y + 12;
        if (left + width > ActualWidth - 8) left = _pointer.X - width - 18;
        if (top + height > ActualHeight - 8) top = Math.Max(8, ActualHeight - height - 8);
        left = Math.Clamp(left, 8, Math.Max(8, ActualWidth - width - 8));
        var panel = new Rect(left, top, width, height);
        dc.DrawRoundedRectangle(TipBack, Pen(Brush("#2A2317"), 5), panel, 7, 7);
        dc.DrawRoundedRectangle(null, Pen(Gold, 1.4), panel, 7, 7);
        var head = new Rect(panel.X + 3, panel.Y + 3, panel.Width - 6, header);
        dc.DrawRoundedRectangle(TipHead, Pen(Brush("#4B3E27"), 1.2), head, 5, 5);
        dc.DrawText(title, new Point(panel.X + width / 2 - title.Width / 2, panel.Y + 8));
        double y = panel.Y + header + 8;
        foreach (var row in rows)
        {
            dc.DrawText(row, new Point(panel.X + 13, y));
            y += row.Height + 3;
        }
        if (rows.Count > 0) y += 6;
        dc.DrawLine(Pen(Brush("#33291B"), 1), new Point(panel.X + 10, y - 3), new Point(panel.Right - 10, y - 3));
        dc.DrawText(state, new Point(panel.X + 13, y));
        y += state.Height + 2;
        if (hints.Length > 0)
        {
            dc.DrawText(Make(Model.L["TreeTipKeys"], 10, Bronze), new Point(panel.X + 13, y));
            y += 15;
            foreach (var hint in hints)
            {
                dc.DrawText(hint, new Point(panel.X + 13, y));
                y += hint.Height + 2;
            }
        }
    }

    private static Matrix ToMatrix(TreeConnectionArt.ArtTransform t) =>
        new(t.M11, t.M12, t.M21, t.M22, t.OffsetX, t.OffsetY);

    /// <summary>Draws PoB2's class layers in its own order: every ascendancy circle around the ring, the
    /// current class's centre art, the glow turned towards the class start node, and finally
    /// <c>BGTree</c> over both (Classes/PassiveTreeView.lua:588-640). The ascendancy the build has chosen
    /// (or is showing the graph of) is drawn bright while every other circle is dimmed to half, which is
    /// what makes the selected one stand out in the reference.</summary>
    private void DrawClassBackdrops(DrawingContext dc, Rect visible)
    {
        if (_classArt.Count == 0) return;
        string current = _currentAscendancy;
        foreach (var ascendancy in TreeClassArtTable.Ring)
            DrawBackdrop(dc, ascendancy, visible,
                current.Length == 0 ? 0.5 : ascendancy.Name.Equals(current, StringComparison.OrdinalIgnoreCase) ? 1 : 0.5);
        if (_classCentre is not null) DrawBackdrop(dc, _classCentre, visible, 1);
        if (_start is int startId && _catalog!.Nodes.TryGetValue(startId, out var start))
        {
            if (_classArt.TryGetValue(TreeClassArtTable.GlowSprite, out var glow))
            {
                // PoB2 rotates the glow quad so it points at the class start node: the class circle looks
                // lit towards the class you picked, which is the effect the reference screenshot shows.
                var turn = new Matrix();
                turn.RotateAt(TreeClassArtTable.GlowRotationDegrees(start.X, start.Y, 0, 0), 0, 0);
                dc.PushTransform(new MatrixTransform(turn));
                dc.PushOpacity(0.9);
                double size = TreeClassArtTable.CentreSize;
                dc.DrawImage(glow, new Rect(-size / 2, -size / 2, size, size));
                dc.Pop();
                dc.Pop();
            }
            if (_classArt.TryGetValue(TreeClassArtTable.RingSprite, out var ring))
            {
                double size = TreeClassArtTable.CentreSize;
                dc.DrawImage(ring, new Rect(-size / 2, -size / 2, size, size));
            }
        }
    }

    private void DrawBackdrop(DrawingContext dc, TreeClassArt art, Rect visible, double opacity)
    {
        if (!_classArt.TryGetValue(art.Sprite, out var image)) return;
        var rect = new Rect(art.X - art.Size / 2, art.Y - art.Size / 2, art.Size, art.Size);
        if (!visible.IntersectsWith(rect)) return;
        dc.PushOpacity(opacity);
        dc.DrawImage(image, rect);
        dc.Pop();
    }

    /// <summary>
    /// The class painting that ships beside the tree data (<c>Data/Tree/Portraits</c>). It is only the
    /// fallback now: a data set that has PoB2's own backdrop art draws that instead, exactly like the
    /// reference picture, and the portrait keeps the class circle from going empty on a data set that has
    /// only the atlas and the portraits.
    /// </summary>
    private void DrawPortrait(DrawingContext dc, double opacity)
    {
        if (_portrait is null) return;
        dc.PushOpacity(opacity);
        dc.DrawImage(_portrait, _portraitRect);
        dc.Pop();
    }
    /// <summary>
    /// Draws a piece of art with its dimming folded into the brush. WPF gives every
    /// <see cref="DrawingContext.PushOpacity"/> group its own intermediate surface in software
    /// rasterisation, which cost the tree ~3.7 ms per node — 15x the rest of a zoomed-out frame. A brush
    /// blends its own opacity into the primitive instead, so no surface is needed. The brushes are cached:
    /// the set of opacities a node can have is tiny (allocated/selected, supported, unsupported).
    /// </summary>
    private void DrawArt(DrawingContext dc, BitmapSource art, Rect rect, double opacity)
    {
        if (opacity >= 1) { dc.DrawImage(art, rect); return; }
        var key = (Art: art, Percent: (int)Math.Round(opacity * 100));
        if (!_artBrushes.TryGetValue(key, out var brush))
        {
            brush = new ImageBrush(art) { Opacity = key.Percent / 100.0, Stretch = Stretch.Fill };
            brush.Freeze(); _artBrushes[key] = brush;
        }
        dc.DrawRectangle(brush, null, rect);
    }
    private readonly Dictionary<(BitmapSource Art, int Percent), ImageBrush> _artBrushes = [];
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
        DrawBackground(dc);
        if (_catalog is null || Model is null) return;
        double left = _center.X - ActualWidth / Zoom / 2 - 240, right = _center.X + ActualWidth / Zoom / 2 + 240;
        double top = _center.Y - ActualHeight / Zoom / 2 - 240, bottom = _center.Y + ActualHeight / Zoom / 2 + 240;
        var visible = new Rect(new Point(left, top), new Point(right, bottom));
        dc.PushTransform(new MatrixTransform(Zoom, 0, 0, Zoom, ActualWidth / 2 - _center.X * Zoom, ActualHeight / 2 - _center.Y * Zoom));
        // PoB2's own backdrop layers (Classes/PassiveTreeView.lua:588-640): the ascendancy circles around the
        // ring, the current class's centre art, then BGTree and the glow rotated towards the class start node.
        // The ascendancy graph is a view of its own — its nodes live in coordinates of their own, away from
        // the ring — so it keeps the ascendancy's portrait as its backdrop, and PoB2's art layers are drawn
        // for the main tree, which is where the class picture belongs. Both are drawn before the connections.
        if (_catalog.IsAscendancyGraph) DrawPortrait(dc, 0.50);
        else
        {
            DrawClassBackdrops(dc, visible);
            if (_classArt.Count == 0 || _classCentre is null) DrawPortrait(dc, 0.92);
        }
        foreach (var draw in _edges)
        {
            if (!visible.IntersectsWith(draw.Shape.Bounds)) continue;
            var edge = draw.Edge;
            bool allocated = Owned(edge.From) && Owned(edge.To);
            bool preview = (Model.Preview.Contains(edge.From) || Owned(edge.From)) && (Model.Preview.Contains(edge.To) || Owned(edge.To)) && !allocated;
            // PoB2 paints a connector by the allocation mode of either endpoint (Classes/PassiveTreeView.lua:724-730),
            // so a weapon-set cluster is visible as a red (set I) or green (set II) branch of the tree.
            int edgeSet = _weaponSets.TryGetValue(edge.From, out var fromSet) ? fromSet : _weaponSets.GetValueOrDefault(edge.To);
            var edgePen = allocated
                ? edgeSet == 1 ? SetOneEdge : edgeSet == 2 ? SetTwoEdge : ActiveEdge
                : preview ? PreviewEdge : !_catalog.Nodes[edge.From].IsSupported || !_catalog.Nodes[edge.To].IsSupported ? LockedEdge : IdleEdge;
            var connectionState = allocated ? ConnectionState.Active
                : preview ? ConnectionState.Intermediate : ConnectionState.Normal;
            // PoB2's own connector sprite carries the state (Classes/PassiveTree.lua:723-731); the stroked
            // geometry stays as the fallback for a data set that ships no sprites.
            if (!DrawConnectionArt(dc, draw, connectionState))
            {
                dc.DrawGeometry(null, edgePen, draw.Shape);
                continue;
            }
            // A weapon-set branch keeps its set colour on top of the art: the sprite only knows
            // Normal/Intermediate/Active, never which weapon set paid for the connection.
            if (allocated && edgeSet != 0) dc.DrawGeometry(null, CachedPen(edgeSet == 1 ? SetOne : SetTwo, 6), draw.Shape);
        }
        // A socketed jewel with a stated radius draws its reach, exactly like PoB2 does around its sockets
        // (classes/PassiveTreeView draws the radius circle of the selected/hovered socket; ours shows every
        // filled socket so the two Time-Lost jewels of a build are visible at once).
        foreach (int socketId in _socketed)
        {
            if (!_catalog.Nodes.TryGetValue(socketId, out var socket)) continue;
            double radius = _jewelRadii.GetValueOrDefault(socketId);
            if (radius <= 0 || socket.X < left - radius || socket.X > right + radius || socket.Y < top - radius || socket.Y > bottom + radius) continue;
            dc.DrawEllipse(null, CachedPen(JewelRadiusInk, Math.Max(4, 6 / Zoom)), new Point(socket.X, socket.Y), radius, radius);
        }
        foreach (var node in _catalog.Nodes.Values)
        {
            if (node.X < left || node.X > right || node.Y < top || node.Y > bottom) continue;
            double r = Radius(node); var position = new Point(node.X, node.Y);
            bool active = Owned(node.Id), selected = Model.SelectedId == node.Id, preview = Model.Preview.Contains(node.Id), match = Model.SearchMatches.Contains(node.Id);
            if (selected || match) dc.DrawEllipse(null, CachedPen(selected ? Gold : Cyan, 2 / Zoom), position, r + 24, r + 24);
            // A weapon-set node carries its set's colour instead of the gold "allocated" ring, so the two
            // clusters of a set-swapping build are told apart at a glance (PoB2 does the same).
            int weaponSet = _weaponSets.GetValueOrDefault(node.Id);
            // PoB2 draws a passive as its frame sprite with the skill icon on top, and the sprite carries
            // the state (TreeData/0_5/tree.json → nodeOverlay: PSSkillFrame / …Highlighted / …Active).
            // The ellipse stays as the fallback for a data set whose art is missing, so the tree still
            // renders state when only skills.png is shipped.
            var frameState = active ? NodeFrameState.Allocated : preview ? NodeFrameState.CanAllocate : NodeFrameState.Unallocated;
            string? sprite = TreeFrameArt.Sprite(_catalog.IsAscendancyGraph, node.IsKeystone, node.IsNotable,
                node.IsJewel, node.IsStart, frameState);
            // PoB2 draws a node's effect art under its frame, and for a mastery the pattern *is* the node:
            // it has neither a frame nor an icon (the pinned tree's masteries are the "OnlyImage" nodes of
            // Classes/PassiveTree.lua:922-948, whose base art is the pattern). An effect that is not being
            // allocated keeps PoB2's own 15 % ghost (PassiveTreeView.lua:1026-1040).
            double effectRadius = TreeEffectArt.Radius(node.IsMastery, !string.IsNullOrWhiteSpace(node.EffectArt));
            string? effectName = effectRadius > 0 ? TreeEffectArt.Sprite(node.EffectArt) : null;
            BitmapSource? effect = effectName is null ? null : _effectArt.GetValueOrDefault(effectName);
            bool patternOnly = node.IsMastery && effect is not null;
            if (effect is not null)
            {
                double size = effectRadius * 2;
                DrawArt(dc, effect, new Rect(node.X - size / 2, node.Y - size / 2, size, size),
                    active || preview ? 1 : TreeEffectArt.IdleOpacity);
            }
            // PoB2 draws a node as its own art ("base") and then its frame ("overlay") ON TOP of it
            // (Classes/PassiveTreeView.lua:1064, :1126). The order is what makes the node read as the game's:
            // the icon atlas ships square patches with an opaque background, so an icon drawn last sat as a
            // square over the node's round frame and hid the ring — the frame is the state (PoB2 picks
            // PSSkillFrame / NotableFrameAllocated / … by it) and it is what lights up when a node is taken.
            BitmapSource? frameArt = sprite is null ? null : _frameArt.GetValueOrDefault(sprite);
            bool framed = frameArt is not null;
            var (artWidth, artHeight) = framed ? TreeFrameArt.Size(sprite!) : (0, 0);
            double frameWidth = framed ? r * 2 : 0, frameHeight = framed ? frameWidth * artHeight / artWidth : 0;
            if (!patternOnly && !node.IsStart && Zoom >= 0.035 && Icon(node) is BitmapSource bitmap)
            {
                // PoB2's own icon share of the frame (37/54 for a normal node, 54/80 for a notable…) keeps
                // the art inside the ring instead of spilling over it, and the icon is dimmed while the node
                // is not taken (PoB2's LessLuminance halves an unallocated node's art).
                double size = framed ? frameWidth * TreeFrameArt.IconShare(sprite) : r * 1.42;
                DrawArt(dc, bitmap, new Rect(node.X - size / 2, node.Y - size / 2, size, size),
                    active || selected ? 1 : node.IsSupported ? 0.55 : 0.25);
            }
            if (patternOnly)
            {
                // The pattern is the node: PoB2's mastery nodes have neither a frame nor an icon, so
                // nothing is drawn over the art here (Classes/PassiveTreeView.lua:922-948).
            }
            else if (framed)
            {
                DrawArt(dc, frameArt!, new Rect(node.X - frameWidth / 2, node.Y - frameHeight / 2, frameWidth, frameHeight),
                    active || selected ? 1 : node.IsSupported ? 0.92 : 0.3);
            }
            else
            {
                var nodePen = active
                    ? weaponSet == 1 ? CachedPen(SetOne, 14) : weaponSet == 2 ? CachedPen(SetTwo, 14) : CachedPen(Gold, 14)
                    : CachedPen(preview ? Cyan : node.IsSupported ? Bronze : Dim, 9);
                dc.DrawEllipse(NodeInk, nodePen, position, r, r);
            }
            // The art can only say "allocated", never which weapon set paid for the node, so a
            // weapon-set allocation keeps a thin ring in its set's colour on top of the frame.
            if (active && weaponSet != 0)
                dc.DrawEllipse(null, CachedPen(weaponSet == 1 ? SetOne : SetTwo, 7), position, r * 0.88, r * 0.88);
            // A jewel socket that actually holds a jewel is drawn like PoB2: a bright ring plus a gem
            // mark, so the sockets the build uses are visible without hovering.
            if (node.IsJewel && _socketed.Contains(node.Id))
            {
                dc.DrawEllipse(null, CachedPen(Cyan, 20 / Zoom + 6), position, r * 0.82, r * 0.82);
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
            if (!patternOnly && !node.IsSupported && Zoom > 0.09) dc.DrawLine(CachedPen(Dim, 13), new(node.X - r * 0.65, node.Y + r * 0.65), new(node.X + r * 0.65, node.Y - r * 0.65));
        }
        dc.Pop();
        // The game's own hover tooltip: a framed panel with the node's name and what it does, drawn over
        // everything (and over the world transform, so it never scales with the zoom).
        DrawNodeTooltip(dc);
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
    /// <summary>What the art loader found, reported by the off-screen renderer
    /// (<see cref="Services.TreeRenderHarness"/>): the number of frame, connector and backdrop sprites
    /// in memory. A zero means the data folder shipped no art and the fallback drawing is in use.</summary>
    internal (int Frames, int Connectors, int Backdrops, int Effects) ArtInventory =>
        (_frameArt.Count, _orbitArt.Count, _classArt.Count, _effectArt.Count);

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
    /// <summary>Places the view at a chosen world point. The off-screen renderer
    /// (<see cref="Services.TreeRenderHarness"/>) has no mouse, and a shot that is to be compared with a
    /// reference screenshot has to be reproducible.</summary>
    internal void SetView(double x, double y, double zoom) { _center = new(x, y); Zoom = Math.Clamp(zoom, 0.015, 0.6); InvalidateVisual(); }
    /// <summary>Puts the viewport in the state a hover leaves it in, so the off-screen renderer
    /// (<see cref="Services.TreeRenderHarness"/>) can shoot the hover tooltip — a harness has no mouse. The
    /// panel is built from the same providers a real hover uses, so a shot shows what a user would see.</summary>
    internal void HoverAt(int? nodeId, double x, double y)
    {
        _hover = nodeId; _pointer = new Point(x, y);
        if (nodeId is int id) BuildTooltip(id); else HideTooltip();
        InvalidateVisual();
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
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle or MouseButton.Right)) return;
        Focus(); _lastDrag = _dragStart = e.GetPosition(this); _dragged = false; CaptureMouse();
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var current = e.GetPosition(this);
        _pointer = current;
        if (_lastDrag is Point last && _dragStart is Point start)
        {
            if ((current - start).Length > 4) _dragged = true;
            if (_dragged)
            {
                var delta = current - last; _center = new(_center.X - delta.X / Zoom, _center.Y - delta.Y / Zoom);
                // A drag is a pan: the panel would follow the mouse over the tree and never settle.
                HideTooltip();
                InvalidateVisual(); Cursor = Cursors.SizeAll;
            }
            _lastDrag = current; return;
        }
        int? hit = Hit(current); Cursor = hit.HasValue ? Cursors.Hand : Cursors.Cross;
        if (hit != _hover)
        {
            _hover = hit;
            if (_hover is int hovered) BuildTooltip(hovered); else HideTooltip();
            InvalidateVisual();
        }
        else if (hit.HasValue) InvalidateVisual();
    }
    private void HideTooltip()
    {
        if (_tipTitle.Length == 0) return;
        _tipTitle = ""; _tipState = ""; _tipRows.Clear(); _tipHints = false;
    }
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = null; HideTooltip(); InvalidateVisual();
        base.OnMouseLeave(e);
    }
    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_dragStart is null) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (!_dragged && Hit(e.GetPosition(this)) is int id)
        {
            // The game's own click model, which the owner asked for by name:
            //   left click            — take (or drop) the node for BOTH weapon sets;
            //   Shift + left click    — take (or drop) it for weapon set I;
            //   Shift + right click   — take (or drop) it for weapon set II;
            //   right click           — inspect only, which is what Shift+click used to do here.
            if (e.ChangedButton == MouseButton.Left) Model?.ClickNode(id, shift ? 1 : null);
            else if (e.ChangedButton == MouseButton.Right) { if (shift) Model?.ClickNode(id, 2); else Model?.Select(id); }
            // The node's own state may have changed (a different frame sprite, a weapon-set colour), so the
            // panel is rebuilt from the plan that is now in force.
            if (_hover == id) BuildTooltip(id);
        }
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
