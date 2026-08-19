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
///     Per-column boundary/seam correction. out = in * Gain + Lift * fullScale (per channel). Gain &lt; 1
///     lowers bright tones, Lift &gt; 0 raises dark tones =&gt; contrast compression for a driver-cascade /
///     scan-home boundary column (e.g. 63/127/191/255 on the 256x128 wall).
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
}

/// <summary>Everything needed to open a panel stream. Colour + seam can also be changed live afterwards.</summary>
public sealed class PanelStreamOptions
{
    public string Host { get; set; } = "192.168.10.181";
    public int Port { get; set; } = 7777;

    /// <summary>Send pacing target in Mbit/s (kept just below the MCU's drain rate to avoid RX overflow).</summary>
    public double TargetMbps { get; set; } = 19.0;

    /// <summary>Greyscale bit-planes sent: 8, 10, 13 or 14. Must match the firmware's HUB_NPLANES / gclk bits.</summary>
    public int ColorBits { get; set; } = 14;

    /// <summary>Logical wall size. Currently the packing geometry supports 256x128.</summary>
    public int WallWidth { get; set; } = 256;

    public int WallHeight { get; set; } = 128;

    public PanelColorSettings Color { get; set; } = new();
    public List<SeamColumn> Seam { get; set; } = [];
}