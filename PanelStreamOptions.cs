namespace PixPlane;

/// <summary>
///     Live-adjustable colour pipeline for the panel. Applied per pixel as an 8-bit-in / colorBits-out
///     lookup table so it can be rebuilt at runtime (live gamma/contrast/brightness/white-balance).
/// </summary>
public sealed class PanelColorSettings
{
    /// <summary>Tone curve: "none" (linear), "gamma" (power law), or "cie1931" (perceptual L*).</summary>
    public string Curve { get; set; } = "none";

    /// <summary>Gamma exponent for Curve == "gamma".</summary>
    public double Gamma { get; set; } = 2.2;

    /// <summary>Contrast around mid-grey: 1.0 = unchanged, &gt;1 steeper, &lt;1 flatter.</summary>
    public double Contrast { get; set; } = 1.0;

    /// <summary>Master brightness multiplier (0..4).</summary>
    public double Brightness { get; set; } = 1.0;

    /// <summary>Per-channel white-balance gain.</summary>
    public double GainR { get; set; } = 1.0;

    public double GainG { get; set; } = 1.0;
    public double GainB { get; set; } = 1.0;

    /// <summary>Swap R and B (panel colour-order wiring).</summary>
    public bool SwapRedBlue { get; set; }

    /// <summary>
    ///     Ordered (Bayer) dithering when expanding the 8-bit source to the panel's bit depth. Each 8-bit
    ///     level is spread across its local gap in the higher-depth output, so hard band edges in gradients
    ///     blend instead of stepping. Cheap and effective de-banding; leave on unless you want the raw steps.
    /// </summary>
    public bool Dither { get; set; } = true;

    public PanelColorSettings Clone()
    {
        return (PanelColorSettings)MemberwiseClone();
    }
}

/// <summary>
///     One point on a per-column 8-bit remap curve. <see cref="In"/> is the source 8-bit level (0..255);
///     <see cref="Out"/> is the remapped 8-bit value sent into the global colour LUT. Missing levels are
///     linearly interpolated. Used to invert a nonlinear scan-home curve that affine gain/lift cannot.
/// </summary>
public sealed class SeamKnot
{
    public int In { get; set; }
    public int Out { get; set; }
}

/// <summary>
///     Per-column boundary/seam correction for driver-cascade / scan-home columns
///     (e.g. 63/127/191/255 on the 256×128 wall).
///     Pipeline per channel: 8-bit remap (Knots / Lut*) → global colour LUT → optional dither →
///     <c>out = in * Gain + Lift * fullScale</c>. Old JSON with only Gain/Lift still works (remap is identity).
/// </summary>
public sealed class SeamColumn
{
    public int X { get; set; } = -1;
    public double GainR { get; set; } = 1.0;
    public double GainG { get; set; } = 1.0;
    public double GainB { get; set; } = 1.0;
    public double LiftR { get; set; }
    public double LiftG { get; set; }
    public double LiftB { get; set; }

    /// <summary>
    ///     Shared 8-bit remap knots (R=G=B). Typical UI is 9 points at 0,32,…,224,255.
    ///     Ignored for a channel that also has a 256-entry <see cref="LutR"/> / <see cref="LutG"/> / <see cref="LutB"/>.
    /// </summary>
    public List<SeamKnot>? Knots { get; set; }

    /// <summary>Optional explicit 256-entry 8→8 remap (0..255), linked RGB. JSON number array, not base64.</summary>
    public int[]? Lut { get; set; }

    /// <summary>Optional per-channel 256-entry 8→8 remaps. Override <see cref="Lut"/> / <see cref="Knots"/>.</summary>
    public int[]? LutR { get; set; }
    public int[]? LutG { get; set; }
    public int[]? LutB { get; set; }

    /// <summary>Knot input levels used by the settings UI (0, 32, …, 224, 255).</summary>
    public static readonly int[] UiKnotIns = [0, 32, 64, 96, 128, 160, 192, 224, 255];

    public static List<SeamKnot> IdentityKnots()
    {
        var list = new List<SeamKnot>(UiKnotIns.Length);
        foreach (var i in UiKnotIns)
            list.Add(new SeamKnot { In = i, Out = i });
        return list;
    }

    /// <summary>9 UI knots sampled from this column's R map (identity if none).</summary>
    public List<SeamKnot> SampleUiKnots()
    {
        var map = BuildMaps().R ?? IdentityMap();
        var list = new List<SeamKnot>(UiKnotIns.Length);
        foreach (var i in UiKnotIns)
            list.Add(new SeamKnot { In = i, Out = map[i] });
        return list;
    }

    /// <summary>
    ///     Expand knots / Lut* to per-channel 256-entry maps. Null means identity (fast path).
    /// </summary>
    public (byte[]? R, byte[]? G, byte[]? B) BuildMaps()
    {
        var fromLut = Normalize(Lut);
        var fromKnots = ExpandKnots(Knots);
        var shared = fromLut ?? fromKnots;
        var r = NullIfIdentity(Normalize(LutR) ?? shared);
        var g = NullIfIdentity(Normalize(LutG) ?? shared);
        var b = NullIfIdentity(Normalize(LutB) ?? shared);
        return (r, g, b);
    }

    public static byte[] ExpandKnots(IReadOnlyList<SeamKnot>? knots)
    {
        var map = IdentityMap();
        if (knots == null || knots.Count == 0) return map;

        var pts = new List<(int In, int Out)>(knots.Count);
        foreach (var k in knots)
            pts.Add((Math.Clamp(k.In, 0, 255), Math.Clamp(k.Out, 0, 255)));
        pts.Sort((a, b) => a.In.CompareTo(b.In));

        // Collapse duplicate In to the last Out.
        var compact = new List<(int In, int Out)>(pts.Count);
        foreach (var p in pts)
        {
            if (compact.Count > 0 && compact[^1].In == p.In) compact[^1] = p;
            else compact.Add(p);
        }

        if (compact[0].In != 0) compact.Insert(0, (0, compact[0].Out));
        if (compact[^1].In != 255) compact.Add((255, compact[^1].Out));

        for (var s = 0; s < compact.Count - 1; s++)
        {
            var a = compact[s];
            var b = compact[s + 1];
            var span = Math.Max(1, b.In - a.In);
            for (var x = a.In; x <= b.In; x++)
            {
                var t = (x - a.In) / (double)span;
                map[x] = (byte)Math.Clamp((int)Math.Round(a.Out + t * (b.Out - a.Out)), 0, 255);
            }
        }

        return map;
    }

    private static byte[] IdentityMap()
    {
        var map = new byte[256];
        for (var i = 0; i < 256; i++) map[i] = (byte)i;
        return map;
    }

    private static byte[]? Normalize(int[]? lut)
    {
        if (lut == null || lut.Length != 256) return null;
        var map = new byte[256];
        for (var i = 0; i < 256; i++)
            map[i] = (byte)Math.Clamp(lut[i], 0, 255);
        return map;
    }

    private static byte[]? NullIfIdentity(byte[]? map)
    {
        if (map == null) return null;
        for (var i = 0; i < 256; i++)
            if (map[i] != i) return map;
        return null;
    }
}

/// <summary>Everything needed to open a panel stream. Colour + seam can also be changed live afterwards.</summary>
public sealed class PanelStreamOptions
{
    public string Host { get; set; } = "192.168.10.181";
    public int Port { get; set; } = 7777;

    /// <summary>Send pacing target in Mbit/s (kept just below the MCU's drain rate to avoid RX overflow).</summary>
    public double TargetMbps { get; set; } = 19.0;

    /// <summary>
    ///     Upper limit for the adaptive pacing. With firmware 1.11+ the panel acknowledges every frame and
    ///     PixPlane raises the pacing from TargetMbps towards this until frames get lost, then backs off.
    /// </summary>
    public double MaxMbps { get; set; } = 95.0;

    /// <summary>Adapt the pacing to the panel's acks (default). False keeps TargetMbps fixed.</summary>
    public bool AdaptivePacing { get; set; } = true;

    /// <summary>Greyscale bit-planes sent: 8, 10, 13 or 14. Must match the firmware's HUB_NPLANES / gclk bits.</summary>
    public int ColorBits { get; set; } = 14;

    /// <summary>Logical wall size. Currently the packing geometry supports 256x128.</summary>
    public int WallWidth { get; set; } = 256;

    public int WallHeight { get; set; } = 128;

    public PanelColorSettings Color { get; set; } = new();
    public List<SeamColumn> Seam { get; set; } = [];
}