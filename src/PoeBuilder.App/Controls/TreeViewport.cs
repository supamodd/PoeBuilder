using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PoeBuilder.App.ViewModels;
using PoeBuilder.Core.Calculation;
using PoeBuilder.Core.Localization;
using PoeBuilder.Core.Tree;

namespace PoeBuilder.App.Controls;

/// <summary>Immediate-mode WPF rendering: no thousands of UIElements or network dependencies.</summary>
public sealed class TreeViewport : FrameworkElement
{
    private static readonly FontFamily AppFont = new("pack://application:,,,/PoeBuilder;component/Assets/Jost%20Medieval.ttf#Jost%20Medieval");
    private static readonly FontFamily AppFontBold = new("pack://application:,,,/PoeBuilder;component/Assets/Jost%20Medieval%20Bold.ttf#Jost%20Medieval%20Bold");
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
    private readonly List<(Rect Bounds, int VariantId)> _attributePickerTargets = [];
    private Point _pointer;
    private TreeCatalog? _catalog;
    private readonly Dictionary<string, (Point Center, double Zoom)> _views = [];
    private BitmapSource? _portrait;
    private string _portraitKey = "";
    private Rect _portraitRect;
    private bool _fitPending;
    private readonly Dictionary<string, BitmapSource> _icons = [];
    private static readonly string[] IconFramePrefixes = ["normalActive:", "notableActive:", "keystoneActive:"];
    private readonly Dictionary<string, Int32Rect> _frames = [];
    /// <summary>PoB2's node frame art, one PNG per atlas slice (see <see cref="TreeFrameArt"/>).</summary>
    private readonly Dictionary<string, BitmapSource> _frameArt = [];
    /// <summary>
    /// The ornate bronze header PoB2 draws round a passive's name in its hover tooltip — the three
    /// <c>*passiveheader{left,middle,right}.png</c> caps of <c>Classes/Tooltip.lua:18-32</c>, shipped in
    /// <c>Art/frames</c> and named after <see cref="TreeFrameArt.TooltipHeader*"/>. A data set without
    /// them keeps the plain framed panel, so the tooltip still reads.
    /// </summary>
    private readonly Dictionary<string, BitmapSource> _headerArt = [];
    /// <summary>Which header set the hovered node's tooltip wears (see <see cref="_headerArt"/>).</summary>
    private string _tipHeader = "";
    private BitmapSource? _atlas;
    private Dictionary<int, PassiveVariant> _descriptions = [];
    private HashSet<int> _socketed = [];
    /// <summary>Reach of each filled socket, computed once per state change: the provider can walk the
    /// whole radius model, and asking it per socket on every frame was part of the panning stutter.</summary>
    private List<(int CentreId, double Radius)> _jewelRadii = [];
    /// <summary>The real artwork of the jewel in each filled socket (the game draws it inside the socket's
    /// frame). Computed once per state change like <see cref="_jewelRadii"/>; a socket whose jewel has no
    /// verified art falls back to the generic gem mark.</summary>
    private Dictionary<int, BitmapSource> _jewelIcons = [];
    private IReadOnlyDictionary<int, int> _weaponSets = new Dictionary<int, int>();
    private int _activeWeaponSet = 1;
    private readonly List<EdgeDraw> _edges = [];
    /// <summary>A connection with everything the art needs, precomputed once per data set:
    /// <c>Kind</c> and <c>Orbit</c> decide the sprite, the rest is placement. <c>Pieces</c> holds the
    /// per-quarter clip geometry, because building it inside the render pass allocated a new
    /// <c>StreamGeometry</c> for every visible connection on every frame — that is what made panning
    /// stutter.</summary>
    private sealed record EdgeDraw(TreeEdge Edge, Geometry Shape, ConnectionKind Kind, int Orbit, Point Centre,
        double Radius, double StartAngle, double Sweep, Point Start, double Length, MatrixTransform? LineTransform)
    {
        /// <summary>The angular pieces a single 90° sprite cannot cover, with their clips ready.</summary>
        public List<(Geometry Clip, double Start, double Sweep)> Pieces { get; } = [];
    }
    private readonly Dictionary<string, BitmapSource> _orbitArt = [];
    private readonly Dictionary<BitmapSource, ImageBrush> _lineBrushes = [];
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
    /// <summary>PoB2 shades a passive's tooltip sheet black at 85 % over whatever is behind it
    /// (Classes/Tooltip.lua:472-477 draws it exactly so the tree stays faintly visible through the panel).</summary>
    private static readonly Brush TipSheet = Brush("#D9080A0D");
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
    private const int PenCacheLimit = 512;
    private Pen CachedPen(Brush brush, double width)
    {
        var key = (Brush: brush, Width: Math.Round(width, 2));
        if (_penCache.TryGetValue(key, out var pen)) return pen;
        if (_penCache.Count >= PenCacheLimit) _penCache.Clear();
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
            _catalog = Model?.Catalog; _edges.Clear(); _icons.Clear(); _frames.Clear(); _frameArt.Clear(); _orbitArt.Clear(); _headerArt.Clear(); _lineBrushes.Clear(); _classArt.Clear(); _effectArt.Clear(); _background = null; _backgroundCrop = null; _classCentre = null; _atlas = null;
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
            _jewelRadii = [];
            foreach (int socketId in _socketed)
            {
                int band = Model.JewelRadiusProvider?.Invoke(socketId) ?? 0;
                int centreId = socketId;
                if (plan.RadiusJewels.TryGetValue(socketId, out var rule) && rule.FromKeystone)
                {
                    band = rule.RadiusIndex;
                    centreId = PassiveTreeEngine.KeystoneId(_catalog, rule.KeystoneName);
                }
                double radius = JewelRadius.OuterRadius(band);
                if (centreId != 0 && radius > 0 && !_jewelRadii.Contains((centreId, radius)))
                    _jewelRadii.Add((centreId, radius));
            }
            // The jewel's own art, frozen once per state change so the panning pass never re-decodes it.
            _jewelIcons = [];
            foreach (int id in _socketed)
                if (Model.JewelIconProvider?.Invoke(id) is BitmapSource image)
                    _jewelIcons[id] = image;
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
                ? ascendancyArt with { X = 0, Y = 0, Size = TreeClassArtTable.TableSize }
                : TreeClassArtTable.Class(_catalog.Classes.FirstOrDefault(c => c.Index == plan.ClassIndex)?.Name ?? "") is { } classArt
                    ? classArt with { Size = TreeClassArtTable.TableSize }
                    : null;
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
            // "frames" carries the hover tooltip's header caps (Header<Kind><Part>.png) — not node frames,
            // which sit in the folder root, so the folder is what tells them apart.
            bool tooltipFrame = group.Equals("frames", StringComparison.OrdinalIgnoreCase);
            if (name.Equals("Background2", StringComparison.OrdinalIgnoreCase)) { _background = image; continue; }
            if (tooltipFrame) { _headerArt[name] = image; continue; }
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
        MatrixTransform? lineTransform = null;
        if (kind == ConnectionKind.Line)
        {
            startAngle = Math.Atan2(dy, dx);
            lineTransform = new MatrixTransform(ToMatrix(TreeConnectionArt.LinePlacement(a.X, a.Y, startAngle)));
            lineTransform.Freeze();
        }
        var draw = new EdgeDraw(edge, geometry, kind, orbit, centre, radius, startAngle, sweep, new Point(a.X, a.Y), length, lineTransform);
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
            if (!_lineBrushes.TryGetValue(art, out var brush))
            {
                brush = new ImageBrush(art)
                {
                    TileMode = TileMode.Tile,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, width, height),
                    Stretch = Stretch.Fill,
                };
                brush.Freeze();
                _lineBrushes.Add(art, brush);
            }
            if (draw.LineTransform is null) return false;
            dc.PushTransform(draw.LineTransform);
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
        // The tooltip shows the Russian name and Russian stat lines when the interface is Russian and we
        // hold them for this node; otherwise the pinned English ones. Both lists carry the same GGG stat
        // ids, so which one is painted never changes what the node does.
        var ru = Model.Strings?.Node(id.ToString());
        bool russian = ru is not null && Model.L.Language == "ru";
        _tipTitle = russian ? ru!.Name : info.Name;
        _tipState = Model.L[_tipAllocated ? "TreeTipAllocated" : "TreeTipUnallocated"];
        // Only the readable half of a "[StatId|text]" placeholder is painted: the id is machine data and
        // printing it next to the Russian word is what made the tooltip read "[Allies|Союзники]".
        var lines = russian && ru!.Stats is { Length: > 0 } ? ru.Stats : info.Stats;
        foreach (var stat in lines) _tipRows.Add((GameStrings.Clean(stat), TipStat, 17));
        // A provider walks the plan (and the jewels in it), so a saved state that fails the tree's own rules
        // must report nothing here instead of taking the window down on a mouse move.
        void Extra(string? text, Brush ink)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
                if (line.Trim().Length > 0) _tipRows.Add((line.Trim(), ink, 15.5));
        }
        Extra(Model.WeaponSetNote(id), TipHint);
        Extra((_socketed.Contains(id) || (_catalog?.Nodes[id].IsJewel ?? false)) ? Safe(Model.SocketInfoProvider, id) : null, TipNote);
        Extra(Safe(Model.NodeImpactProvider, id), TipNote);
        _tipHints = Model.CanModify && _catalog?.Nodes[id].IsStart != true;
        // The node's own type picks the header the game's tooltip wears (Classes/PassiveTreeView.lua:1540-1546):
        // a notable, a keystone, a socket and an ascendancy node each get their own ornate cap.
        _tipHeader = _catalog?.Nodes.TryGetValue(id, out var node) == true
            ? TreeFrameArt.TooltipHeader(_catalog.IsAscendancyGraph, node.IsKeystone, node.IsNotable, node.IsJewel) ?? ""
            : "";
    }

    /// <summary>
    /// Draws the hover tooltip in screen space, next to the pointer and kept inside the viewport. The
    /// panel is PoB2's own passive tooltip: a black 85 % sheet, an ornate bronze header strip whose caps
    /// come from the game's <c>*passiveheader*.png</c> art and whose middle is tiled across the name
    /// (<c>Classes/Tooltip.lua:518-555</c>), the node's name centred in it, its modifier lines, its state
    /// ("Not allocated"), the key hints, and a 1 px border in PoB2's tooltip colour
    /// (<c>Tooltip.lua:656-671</c>). A data set that ships no header art falls back to a plain framed
    /// panel with the same text.
    /// </summary>
    private void DrawNodeTooltip(DrawingContext dc)
    {
        if (Model is null || _tipTitle.Length == 0 || ActualWidth < 40 || ActualHeight < 40) return;
        double pixels = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double maxWidth = Math.Min(440, Math.Max(210, ActualWidth - 24));
        FormattedText Make(string text, double size, Brush brush, bool bold = false, TextAlignment alignment = TextAlignment.Left) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(bold ? AppFontBold : AppFont, FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
                size, brush, pixels) { MaxTextWidth = maxWidth, TextAlignment = alignment };
        bool notable = _tipHeader == TreeFrameArt.TooltipHeaderNotable;
        var (poB2Header, poB2Side, poB2Middle, textYOffset, titleSize) = TreeFrameArt.TooltipHeaderShape(notable);
        double headerHeight = poB2Header, sideWidth = poB2Side, middleWidth = poB2Middle;
        var titleMeasure = Make(_tipTitle, titleSize, TipInk, bold: true);
        var state = Make(_tipState, 15.5, _tipAllocated ? Gold : TipNote);
        var rows = new List<FormattedText>();
        foreach (var (text, ink, size) in _tipRows) rows.Add(Make(text, size, ink));
        var hints = _tipHints ? new[]
        {
            Make(Model.L["TreeTipKeyLeft"], 15.5, TipHint),
            Make(Model.L["TreeTipKeyShiftLeft"], 15.5, TipHint),
            Make(Model.L["TreeTipKeyShiftRight"], 15.5, TipHint),
            Make(Model.L["TreeTipKeyRight"], 15.5, TipHint)
        } : [];
        // The hints block is a caption line plus the four key lines, and the panel has to be tall enough for
        // all of them: reserving only the lines cut the last hint in half.
        double body = 14 + rows.Sum(r => r.Height + 4) + 12 + state.Height + (hints.Length == 0 ? 0 : 28 + hints.Sum(h => h.Height + 3));
        double width = Math.Min(maxWidth + 32, Math.Max(titleMeasure.Width, Math.Max(state.Width, Math.Max(rows.Count == 0 ? 0 : rows.Max(r => r.Width), hints.Length == 0 ? 0 : hints.Max(h => h.Width)))) + 32);
        var title = Make(_tipTitle, titleSize, TipInk, bold: true);
        // PoB2's strip is 38 px tall for a 24 px title; the strip grows with the title rather than clipping it.
        double header = Math.Max(headerHeight, title.Height + textYOffset + 8);
        sideWidth = Math.Max(sideWidth, title.Height * 1.1);
        // The name has to sit between the two caps, so the panel is at least that wide plus both caps.
        width = Math.Max(width, Math.Min(maxWidth + 32, title.Width + sideWidth * 2 + 4));
        title.MaxTextWidth = Math.Max(1, width - sideWidth * 2);
        double height = header + Math.Max(28, body);
        // The pointer is the anchor: the panel opens to its lower right and is pushed back inside the
        // viewport when it would leave it, which is what the game does at the screen's edge.
        double left = _pointer.X + 18, top = _pointer.Y + 12;
        if (left + width > ActualWidth - 8) left = _pointer.X - width - 18;
        if (top + height > ActualHeight - 8) top = Math.Max(8, ActualHeight - height - 8);
        left = Math.Clamp(left, 8, Math.Max(8, ActualWidth - width - 8));
        var panel = new Rect(left, top, width, height);
        if (DrawTooltipHeader(dc, panel, header, sideWidth, middleWidth))
        {
            // PoB2 lays the name over the strip (Tooltip.lua:289-291 draws the title into the header's own
            // band), centred between the two caps, so the title's band is the strip itself — no second box round it.
            dc.DrawText(title, new Point(panel.X + sideWidth + Math.Max(0, (width - sideWidth * 2 - title.Width) / 2), panel.Y + (header - title.Height) / 2));
        }
        else
        {
            var head = new Rect(panel.X + 3, panel.Y + 3, panel.Width - 6, header);
            dc.DrawRoundedRectangle(TipHead, Pen(Brush("#4B3E27"), 1.2), head, 5, 5);
            dc.DrawText(title, new Point(panel.X + 16, panel.Y + 10));
        }
        double y = panel.Y + header + 10;
        foreach (var row in rows)
        {
            dc.DrawText(row, new Point(panel.X + 16, y));
            y += row.Height + 4;
        }
        if (rows.Count > 0) y += 7;
        dc.DrawLine(Pen(Brush("#33291B"), 1), new Point(panel.X + 12, y - 3), new Point(panel.Right - 12, y - 3));
        dc.DrawText(state, new Point(panel.X + 16, y));
        y += state.Height + 3;
        if (hints.Length > 0)
        {
            dc.DrawText(Make(Model.L["TreeTipKeys"], 14, Bronze), new Point(panel.X + 16, y));
            y += 19;
            foreach (var hint in hints)
            {
                dc.DrawText(hint, new Point(panel.X + 16, y));
                y += hint.Height + 3;
            }
        }
    }

    /// <summary>
    /// Paints the tooltip the way PoB2 paints a passive's: a black 85 % sheet over the whole panel
    /// (<c>Classes/Tooltip.lua:472-477</c>), the node type's header strip on top of it — left cap, middle
    /// tiled across the name, right cap (<c>:518-555</c>) — and the 1 px border in the tooltip colour
    /// (<c>:656-671</c>). Returns <c>false</c> when the data set ships no header art, so the caller keeps
    /// its own framed panel; the sheet and the border are part of the game's look either way, so they
    /// are drawn by this method on both paths.
    /// </summary>
    private bool DrawTooltipHeader(DrawingContext dc, Rect panel, double header, double sideWidth, double middleWidth)
    {
        dc.DrawRectangle(TipSheet, null, panel);
        bool framed = false;
        if (_tipHeader.Length > 0
            && _headerArt.TryGetValue(_tipHeader + "Left", out var left)
            && _headerArt.TryGetValue(_tipHeader + "Middle", out var middle)
            && _headerArt.TryGetValue(_tipHeader + "Right", out var right))
        {
            framed = true;
            // The caps carry the ornamented corners, so they are drawn at PoB2's own width and the middle
            // fills whatever is left — the same tiling its loop does (:541-552).
            var strip = new Rect(panel.X + 1, panel.Y + 1, panel.Width - 2, header - 1);
            dc.DrawImage(left, new Rect(strip.X, strip.Y, sideWidth, strip.Height));
            double end = strip.Right - sideWidth;
            for (double x = strip.X + sideWidth; x < end - 0.5; x += middleWidth)
                dc.DrawImage(middle, new Rect(x, strip.Y, Math.Min(middleWidth, end - x), strip.Height));
            dc.DrawImage(right, new Rect(end, strip.Y, sideWidth, strip.Height));
        }
        // PoB2 strokes the border last, after the text, so nothing covers it (:656-671).
        var edge = Pen(Brush(TreeFrameArt.TooltipBorderColor), 1);
        dc.DrawRectangle(null, edge, new Rect(panel.X + 0.5, panel.Y + 0.5, panel.Width - 1, panel.Height - 1));
        return framed;
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
                double size = TreeClassArtTable.CentreSize;
                DrawArt(dc, glow, new Rect(-size / 2, -size / 2, size, size), 0.9, highQuality: true);
                dc.Pop();
            }
            if (_classArt.TryGetValue(TreeClassArtTable.RingSprite, out var ring))
            {
                double size = TreeClassArtTable.CentreSize;
                DrawArt(dc, ring, new Rect(-size / 2, -size / 2, size, size), 1, highQuality: true);
            }
        }
        foreach (var ascendancy in TreeClassArtTable.Ring)
            DrawBackdrop(dc, ascendancy, visible,
                current.Length == 0 ? 0.5 : ascendancy.Name.Equals(current, StringComparison.OrdinalIgnoreCase) ? 1 : 0.5);
    }

    private void DrawBackdrop(DrawingContext dc, TreeClassArt art, Rect visible, double opacity)
    {
        if (!_classArt.TryGetValue(art.Sprite, out var image)) return;
        var rect = new Rect(art.X - art.Size / 2, art.Y - art.Size / 2, art.Size, art.Size);
        if (!visible.IntersectsWith(rect)) return;
        DrawArt(dc, image, rect, opacity, highQuality: true);
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
    private void DrawArt(DrawingContext dc, BitmapSource art, Rect rect, double opacity, bool highQuality = false)
    {
        if (opacity >= 1 && !highQuality) { dc.DrawImage(art, rect); return; }
        var key = (Art: art, Percent: (int)Math.Round(opacity * 100), HighQuality: highQuality);
        if (!_artBrushes.TryGetValue(key, out var brush))
        {
            brush = new ImageBrush(art) { Opacity = key.Percent / 100.0, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(brush, highQuality ? BitmapScalingMode.HighQuality : BitmapScalingMode.Linear);
            brush.Freeze(); _artBrushes[key] = brush;
        }
        dc.DrawRectangle(brush, null, rect);
    }
    private readonly Dictionary<(BitmapSource Art, int Percent, bool HighQuality), ImageBrush> _artBrushes = [];
    private BitmapSource? Icon(PassiveNode n)
    {
        if (_atlas is null || !_descriptions.TryGetValue(n.Id, out var description)) return null;
        string key = (n.IsKeystone ? "keystoneActive:" : n.IsNotable ? "notableActive:" : "normalActive:") + description.Icon;
        string requestedKey = key;
        if (_icons.TryGetValue(requestedKey, out var bitmap)) return bitmap;
        if (!_frames.TryGetValue(key, out var frame))
        {
            // Some ascendancy variants use a different sprite size than their base graph frame.
            bool found = false;
            foreach (string prefix in IconFramePrefixes)
            {
                key = prefix + description.Icon;
                if (!_frames.TryGetValue(key, out frame)) continue;
                found = true;
                break;
            }
            if (!found) return null;
        }
        var crop = new CroppedBitmap(_atlas, frame); crop.Freeze();
        _icons[requestedKey] = crop;
        if (key != requestedKey) _icons[key] = crop;
        return crop;
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
        foreach (var (centreId, radius) in _jewelRadii)
        {
            if (!_catalog.Nodes.TryGetValue(centreId, out var centre)) continue;
            if (radius <= 0 || centre.X < left - radius || centre.X > right + radius || centre.Y < top - radius || centre.Y > bottom + radius) continue;
            dc.DrawEllipse(null, CachedPen(JewelRadiusInk, Math.Max(4, 6 / Zoom)), new Point(centre.X, centre.Y), radius, radius);
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
            // PoB2 uses the socket frame as its base image and draws the socketed jewel as the overlay.
            if (node.IsJewel && _socketed.Contains(node.Id))
            {
                if (_jewelIcons.TryGetValue(node.Id, out var jewelArt))
                {
                    double iconSize = framed ? frameWidth * TreeFrameArt.IconShare(sprite) : r * 2;
                    DrawArt(dc, jewelArt, new Rect(node.X - iconSize / 2, node.Y - iconSize / 2, iconSize, iconSize), 1);
                }
                else
                {
                    var gem = new StreamGeometry();
                    using (var ctx = gem.Open())
                    {
                        ctx.BeginFigure(new Point(position.X, position.Y - r * 0.36), true, true);
                        ctx.LineTo(new Point(position.X + r * 0.28, position.Y), true, false);
                        ctx.LineTo(new Point(position.X, position.Y + r * 0.36), true, false);
                        ctx.LineTo(new Point(position.X - r * 0.28, position.Y), true, false);
                    }
                    gem.Freeze();
                    dc.DrawGeometry(Cyan, null, gem);
                }
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
            var text = new FormattedText(_descriptions[node.Id].Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(AppFontBold, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 15, node.Id == _start ? Gold : TextInk, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(p.X - text.Width / 2, p.Y + Radius(node) * Zoom + 5));
        }
        if (IsKeyboardFocused) dc.DrawRectangle(null, Pen(Bronze, 1), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));
        DrawWeaponSetLegend(dc);
        DrawAttributePicker(dc);
    }
    private void DrawAttributePicker(DrawingContext dc)
    {
        _attributePickerTargets.Clear();
        if (Model is null || Model.AttributePickerNodeId is not int nodeId || _catalog is null || !_catalog.Nodes.TryGetValue(nodeId, out var node)) return;
        var choices = Model.AttributeChoices;
        if (choices.Count == 0) return;
        const double buttonWidth = 118, buttonHeight = 40, gap = 6, padding = 9;
        double width = padding * 2 + choices.Count * buttonWidth + Math.Max(0, choices.Count - 1) * gap;
        double height = buttonHeight + padding * 2;
        Point centre = Screen(node.X, node.Y);
        double left = Math.Clamp(centre.X - width / 2, 8, Math.Max(8, ActualWidth - width - 8));
        double top = centre.Y - Radius(node) * Zoom - height - 8;
        if (top < 8) top = Math.Min(centre.Y + Radius(node) * Zoom + 8, ActualHeight - height - 8);
        top = Math.Clamp(top, 8, Math.Max(8, ActualHeight - height - 8));
        var panel = new Rect(left, top, width, height);
        dc.DrawRoundedRectangle(Brush("#F20D1319"), Pen(Bronze, 1.2), panel, 7, 7);
        int selected = Model.SelectedAttribute?.Id ?? 0;
        for (int index = 0; index < choices.Count; index++)
        {
            var choice = choices[index];
            double x = left + padding + index * (buttonWidth + gap);
            var bounds = new Rect(x, top + padding, buttonWidth, buttonHeight);
            bool active = choice.Id == selected;
            Brush accent = choice.Id switch { 26297 => Brush("#C9534B"), 14927 => Brush("#4FAE65"), _ => Brush("#4F86D4") };
            dc.DrawRoundedRectangle(active ? Brush("#333D47") : Brush("#171E26"), Pen(active ? Gold : Brush("#3A4552"), active ? 1.3 : 1), bounds, 4, 4);
            var label = Text(Model.L[choice.Id switch { 26297 => "TreeAttributeStrength", 14927 => "TreeAttributeDexterity", _ => "TreeAttributeIntelligence" }], 15, active ? Gold : accent, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(label, new Point(bounds.X + (bounds.Width - label.Width) / 2, bounds.Y + (bounds.Height - label.Height) / 2));
            _attributePickerTargets.Add((bounds, choice.Id));
        }
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
        var hintText = Text(hint, 14, Bronze, pixels);
        var labels = rows.Select(r => Text(r.Label + (r.Active ? " · " + Model.L["WeaponSetActive"] : ""), 15, TextInk, pixels)).ToArray();
        double width = Math.Max(hintText.Width, labels.Max(t => t.Width) + 26) + 22;
        double height = rows.Length * 24 + 32;
        double left = 12, top = Math.Max(12, ActualHeight - height - 12);
        dc.DrawRoundedRectangle(Brush("#CC0D1319"), Pen(Bronze, 1), new Rect(left, top, width, height), 6, 6);
        double y = top + 8;
        for (int i = 0; i < rows.Length; i++)
        {
            dc.DrawRectangle(rows[i].Swatch, Pen(Bronze, 1), new Rect(left + 9, y + 4, 14, 14));
            dc.DrawText(labels[i], new Point(left + 30, y));
            y += 24;
        }
        dc.DrawText(hintText, new Point(left + 9, y + 4));
    }
    private static FormattedText Text(string value, double size, Brush brush, double pixels) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(AppFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, pixels);
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
        int? hit = Hit(current); Cursor = _attributePickerTargets.Any(option => option.Bounds.Contains(current)) || hit.HasValue ? Cursors.Hand : Cursors.Cross;
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
        Point point = e.GetPosition(this);
        if (!_dragged && e.ChangedButton == MouseButton.Left)
        {
            var option = _attributePickerTargets.FirstOrDefault(item => item.Bounds.Contains(point));
            if (option.VariantId != 0)
            {
                Model?.ChooseAttribute(option.VariantId);
                _dragStart = _lastDrag = null; ReleaseMouseCapture(); e.Handled = true; return;
            }
            if (Model?.AttributePickerNodeId is not null) Model.CloseAttributePicker();
        }
        if (!_dragged && Hit(point) is int id)
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
