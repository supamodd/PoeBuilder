namespace PoeBuilder.Core.Tree;

/// <summary>How a connection is drawn: PoB2 has two art families, and which one applies follows from
/// whether the two nodes sit on a shared orbit around a group centre.</summary>
public enum ConnectionKind
{
    /// <summary>Two nodes whose connecting line is a straight segment (<c>LineConnector</c>).</summary>
    Line,
    /// <summary>Two nodes on the same orbit around one group centre: the connection is an arc
    /// (<c>Orbit&lt;N&gt;</c>).</summary>
    Orbit,
}

/// <summary>PoB2 paints a connector by how much of the connection is allocated
/// (<c>Classes/PassiveTree.lua:723-731</c>: <c>connectionArt .. type .. state</c>).</summary>
public enum ConnectionState
{
    /// <summary>Neither end is allocated.</summary>
    Normal,
    /// <summary>On the path a click would allocate — the highlighted look.</summary>
    Intermediate,
    /// <summary>Both ends allocated.</summary>
    Active,
}

/// <summary>
/// PoB2's connection art. The sprites are plain PNGs in <c>TreeData/0_5</c> (listed in <c>tree.json</c>
/// → <c>assets</c>), and two measured properties make them directly placeable:
/// <list type="bullet">
/// <item>an orbit sprite is a 90° arc whose <b>centre is the image's bottom-right pixel</b> and whose
/// radius equals the orbit radius in world units (measured: on the 1333×1333
/// <c>Character_orbit_normal1.png</c> the opaque pixels sit at R = 1318.4 ± 4, and the catalogue radius
/// of that orbit is 1322);</item>
/// <item>the line sprite is a 1435×29 strip whose visible band is 11 px tall on rows 9-19, so it tiles
/// along a segment exactly like PoB2's <c>endS = distance / art.width</c>.</item>
/// </list>
/// <para>
/// Keeping the numbers here makes placement an affine transform a test can check: rotate about the
/// image's arc centre, move that centre onto the orbit centre, clip to the angular sector. PoB2 instead
/// stretches the art onto a quad, which no affine transform in WPF can reproduce.
/// </para>
/// </summary>
public static class TreeConnectionArt
{
    /// <summary>PoB2's orbit radii in world units (<c>tree.json</c> → <c>constants.orbitRadii</c>,
    /// quoted in catalogue order — it is not sorted: orbit 7 belongs to a side cluster).</summary>
    public static readonly int[] OrbitRadii = [0, 82, 162, 335, 493, 662, 846, 251, 1080, 1322];

    /// <summary>Measured arc radius (pixels) per sprite index, where the index is the file's own
    /// trailing number: <c>Character_orbit_&lt;state&gt;&lt;index&gt;.png</c>. Orbit N uses index
    /// 10 − N (<c>CharacterOrbit9…</c> is index 1, <c>CharacterOrbit1…</c> is index 9). The three states
    /// agree within 0.2 %, which is why one table covers them.</summary>
    private static readonly double[] ArcRadiusPx = [717.4, 1318.4, 1076.9, 838.5, 657.1, 488.3, 333.4, 250.6, 163.2, 81.7];

    /// <summary>The line connector's visible band height in pixels (rows 9-19 of the 1435×29 strip).</summary>
    public const double LineThicknessPx = 11;

    /// <summary>The sprite file PoB2 uses for a straight-line connector in a state.</summary>
    public static string LineFile(ConnectionState state) => "Character_orbit_" + StateWord(state) + "0.png";

    /// <summary>The sprite index PoB2 uses per orbit. It is not a formula: the file is the one whose art
    /// radius matches the orbit radius, which is why the numbering looks irregular (orbit 3 → 6,
    /// orbit 7 → 7, orbit 9 → 1). Measured: index 6 art radius is 333.4 against orbit 3's 335, index 7 is
    /// 250.6 against orbit 7's 251, index 1 is 1318.4 against orbit 9's 1322.</summary>
    private static readonly int[] OrbitSpriteIndex = [0, 9, 8, 6, 5, 4, 3, 7, 2, 1];

    /// <summary>The sprite file PoB2 uses for an orbit connector, or <c>null</c> when that orbit has no
    /// arc art (orbit 0 is the class start ring).</summary>
    public static string? OrbitFile(int orbit, ConnectionState state) =>
        orbit is >= 1 and <= 9 ? "Character_orbit_" + StateWord(state) + OrbitSpriteIndex[orbit] + ".png" : null;

    /// <summary>The sprite file for a connection of this kind, mirroring PoB2's
    /// <c>connectionArt .. (Orbit&lt;N&gt; | LineConnector) .. state</c>.</summary>
    public static string? File(ConnectionKind kind, int orbit, ConnectionState state) =>
        kind == ConnectionKind.Line ? LineFile(state) : OrbitFile(orbit, state);

    /// <summary>The arc radius the sprite was measured at; the viewport scales the art by
    /// <c>orbitRadius / ArcRadius</c> so the drawn arc lands on the catalogue's circle.</summary>
    public static double ArcRadius(string file) => ArcRadiusPx[Index(file)];

    /// <summary>The orbit a node sits on, found by matching its distance to the group centre against
    /// PoB2's own radius table (both ends of an arc share one orbit).</summary>
    public static int OrbitForRadius(double radius)
    {
        int best = 0;
        double bestDelta = double.MaxValue;
        for (int orbit = 1; orbit < OrbitRadii.Length; orbit++)
        {
            double delta = Math.Abs(OrbitRadii[orbit] - radius);
            if (delta < bestDelta) { bestDelta = delta; best = orbit; }
        }
        return best;
    }

    /// <summary>The rotation (degrees, clockwise in screen coordinates where y grows downwards) that
    /// puts the sprite's arc bisector on <paramref name="bisectorAngle"/>: the art's arc opens towards
    /// its top-left corner, which is −135°.</summary>
    public static double ArtRotation(double bisectorAngleRadians) => bisectorAngleRadians * 180 / Math.PI + 135;

    /// <summary>Splits an angular span into pieces of at most 90°, because one sprite covers exactly a
    /// quarter circle. PoB2 does the same for spans above 90° (<c>PassiveTree.lua:659-679</c>), only it
    /// places two known arcs instead of n equal ones.</summary>
    public static IReadOnlyList<(double Start, double Sweep)> SplitArc(double start, double sweep)
    {
        var pieces = new List<(double, double)>();
        int count = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2) - 1e-9));
        double step = sweep / count;
        for (int i = 0; i < count; i++) pieces.Add((start + step * i, step));
        return pieces;
    }

    /// <summary>An affine transform as its six coefficients in WPF's own order
    /// (<c>Matrix(m11, m12, m21, m22, offsetX, offsetY)</c>), so the viewport hands it over unchanged
    /// while the placement stays testable without WPF:
    /// <c>x' = x·M11 + y·M21 + OffsetX</c>, <c>y' = x·M12 + y·M22 + OffsetY</c>.</summary>
    public readonly record struct ArtTransform(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
    {
        public (double X, double Y) Apply(double x, double y) =>
            (x * M11 + y * M21 + OffsetX, x * M12 + y * M22 + OffsetY);
    }

    /// <summary>The transform that lays one orbit sprite onto its circle: scale the art's measured radius
    /// to <paramref name="radius"/>, turn its arc bisector to <paramref name="bisectorAngle"/>, and put the
    /// sprite's arc centre — its bottom-right pixel — on the orbit centre.</summary>
    public static ArtTransform OrbitPlacement(string file, double radius, double bisectorAngle,
        double centreX, double centreY, int artWidth, int artHeight)
    {
        double scale = radius / ArcRadius(file);
        double rotation = ArtRotation(bisectorAngle) * Math.PI / 180;
        double cos = Math.Cos(rotation), sin = Math.Sin(rotation);
        double m11 = scale * cos, m12 = scale * sin, m21 = -scale * sin, m22 = scale * cos;
        double anchorX = artWidth - 1, anchorY = artHeight - 1;
        return new ArtTransform(m11, m12, m21, m22,
            centreX - (m11 * anchorX + m21 * anchorY),
            centreY - (m12 * anchorX + m22 * anchorY));
    }

    /// <summary>The transform that lays the line sprite along a segment: a rotation about the segment's
    /// first endpoint, so a rectangle drawn from that endpoint along the segment lands on the connection.
    /// </summary>
    public static ArtTransform LinePlacement(double startX, double startY, double angle)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        return new ArtTransform(cos, sin, -sin, cos,
            startX - (cos * startX - sin * startY),
            startY - (sin * startX + cos * startY));
    }

    private static string StateWord(ConnectionState state) => state switch
    {
        ConnectionState.Active => "intermediateactive",
        ConnectionState.Intermediate => "intermediate",
        _ => "normal",
    };

    /// <summary>Sprite index parsed from a file name (its trailing number), so the measured radius
    /// table can stay a plain array.</summary>
    private static int Index(string file)
    {
        int end = file.Length - ".png".Length;
        int start = end;
        while (start > 0 && char.IsDigit(file[start - 1])) start--;
        if (!int.TryParse(file[start..end], out int index) || index < 0 || index >= ArcRadiusPx.Length)
            throw new ArgumentException("not a connector sprite: " + file, nameof(file));
        return index;
    }
}
