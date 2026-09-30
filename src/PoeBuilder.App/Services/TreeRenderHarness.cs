using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PoeBuilder.App.Controls;
using PoeBuilder.App.ViewModels;

namespace PoeBuilder.App.Services;

/// <summary>
/// Hidden developer switch (<c>PoeBuilder.exe --render-tree out\</c>): draws the passive tree into an
/// off-screen bitmap. Two things need it that a unit test cannot do — the test suite runs on plain
/// .NET without WPF, so nothing there can exercise the renderer, and a screenshot of a real window
/// cannot be compared region by region.
/// <list type="bullet">
/// <item><c>--render-tree &lt;dir&gt;</c> writes <c>tree-fit.png</c> (the whole tree) and one
/// <c>tree-at-&lt;x&gt;-&lt;y&gt;-&lt;zoom&gt;.png</c> per <c>--at x,y --zoom z</c> pair, so the artwork
/// can be put beside Path of Building 2's screenshot of the same region;</item>
/// <item><c>--build &lt;file&gt;</c> loads a Path of Building share code first, and <c>--asc</c> renders
/// the ascendancy graph that build chose (the view an ascendancy's own portrait shows in) instead of
/// the main tree;</item>
/// <item><c>--frames N</c> renders the viewport N times while walking the view across the tree and
/// prints the per-frame cost — the number the pan and zoom work is measured with. <c>--zoom z</c>
/// chooses the zoom that walk happens at (0.13, the default, is the whole-tree overview).</item>
/// </list>
/// </summary>
public static class TreeRenderHarness
{
    public const string Switch = "--render-tree";

    public static bool Requested(string[] args) => args.Contains(Switch);

    /// <summary>Draws the shots the command line asked for and returns the process exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        string outDir = Value(args, "--out") ?? Path.Combine(AppContext.BaseDirectory, "tree-shots");
        Directory.CreateDirectory(outDir);
        int width = Number(args, "--width", 1640), height = Number(args, "--height", 918);

        var shell = new MainViewModel();
        await shell.InitializeAsync();
        if (shell.Tree.Catalog is null)
            throw new InvalidOperationException("the pinned tree did not load: " + shell.Status);

        // A build gives the tree its real state (allocated nodes, weapon sets, the chosen class), which
        // is what makes a shot comparable with a reference screenshot.
        if (Value(args, "--build") is string file)
        {
            var parsed = BuildInterop.ParsePobCode(File.ReadAllText(file).Trim(), shell.Catalog!, shell.Tree.Catalog!);
            shell.Tree.BindEditor(new BuildEditor(parsed.Document));
        }

        var surface = new TreeViewport { Model = shell.Tree.DisplayedTree, Width = width, Height = height };
        // --asc renders the ascendancy graph the build chose (the view the class art of an ascendancy shows in)
        // instead of the main tree.
        if (args.Contains("--asc") && shell.Tree.ShowAscendancyCommand.CanExecute(null))
        {
            shell.Tree.ShowAscendancyCommand.Execute(null);
            surface = new TreeViewport { Model = shell.Tree.DisplayedTree, Width = width, Height = height };
        }
        surface.Measure(new Size(width, height));
        surface.Arrange(new Rect(0, 0, width, height));
        surface.UpdateLayout();
        var (frames, connectors, backdrops, effects) = surface.ArtInventory;
        Console.WriteLine($"tree art in memory: {frames} frames, {connectors} connectors, {backdrops} backdrops, {effects} effects");

        if (!args.Contains("--no-fit"))
        {
            surface.Reset();
            Shot(surface, Path.Combine(outDir, "tree-fit.png"), width, height);
        }

        // One shot per requested view: --at 1234,-567 --zoom 0.35 (the zoom defaults to 0.3).
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] != "--at" || !TryPoint(args[i + 1], out double x, out double y)) continue;
            double zoom = i + 3 < args.Length && args[i + 2] == "--zoom" && TryDouble(args[i + 3], out double given) ? given : 0.3;
            surface.SetView(x, y, zoom);
            Shot(surface, Path.Combine(outDir, $"tree-at-{x}-{y}-{zoom}.png"), width, height);
        }

        // --hover <nodeId> --pointer <x>,<y> shoots the hover tooltip of one node (a harness has no mouse).
        if (Number(args, "--hover", 0) is int hoverId and > 0)
        {
            Point pointer = TryPoint(Value(args, "--pointer") ?? "", out double px, out double py) ? new Point(px, py) : new Point(width * 0.4, height * 0.4);
            surface.HoverAt(hoverId, pointer.X, pointer.Y);
            Shot(surface, Path.Combine(outDir, $"tree-hover-{hoverId}.png"), width, height);
            surface.HoverAt(null, pointer.X, pointer.Y);
        }

        if (Number(args, "--frames", 0) is int count and > 0)
        {
            double frameZoom = TryDouble(Value(args, "--zoom") ?? "", out double probeZoom) ? probeZoom : 0.13;
            var samples = new List<long>();
            surface.SetView(0, 0, frameZoom);
            for (int i = 0; i < count; i++)
            {
                // Walk the view the way a drag does: a fresh centre and zoom every frame, then one render.
                surface.SetView(i * 40, i * 15, frameZoom + i * 0.0005);
                var watch = Stopwatch.StartNew();
                Shot(surface, null, width, height);
                watch.Stop();
                samples.Add(watch.ElapsedTicks * 1000 / Stopwatch.Frequency);
            }
            var ordered = samples.OrderBy(ms => ms).ToArray();
            Console.WriteLine($"frame ms at zoom {frameZoom}: first {samples[0]}, median {ordered[ordered.Length / 2]}, " +
                $"95th {ordered[(int)(ordered.Length * 0.95)]}, worst {ordered[^1]} ({samples.Count} frames at {width}x{height})");
        }
        return 0;
    }

    /// <summary>Renders one frame, optionally to a file. A null path keeps the work — the point of the
    /// <c>--frames</c> run — without touching the disk.</summary>
    private static void Shot(FrameworkElement element, string? path, int width, int height)
    {
        element.UpdateLayout();
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(element);
        if (path is null) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine(path);
    }

    private static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int Number(string[] args, string name, int fallback) =>
        int.TryParse(Value(args, name), out int value) ? value : fallback;

    private static bool TryDouble(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryPoint(string text, out double x, out double y)
    {
        string[] parts = text.Split(',');
        x = y = 0;
        return parts.Length == 2 && TryDouble(parts[0], out x) && TryDouble(parts[1], out y);
    }
}

