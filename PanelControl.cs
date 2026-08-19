using System.Net.Http;

namespace PixPlane;

/// <summary>
///     Talks to the RP2350 firmware's config web server (the same /cmd bridge the panel's web UI uses)
///     so the host can drive panel settings. Currently used to switch the colour/buffer mode, which the
///     firmware applies on reboot:
///       * 14-bit / double-buffer = max colour depth (RX core stalls per frame -> more UDP drops at high fps)
///       * 8-bit  / triple-buffer = min UDP drops + max fps, full brightness, 256 shades
///     After switching, stream with a matching <c>ColorBits</c> (14 or 8).
/// </summary>
public static class PanelControl
{
    /// <summary>Default TCP port of the firmware's config web server (WEB_PORT in the sketch).</summary>
    public const int DefaultWebPort = 5000;

    /// <summary>
    ///     Switch the panel colour/buffer mode and reboot it to apply. <paramref name="colorBits"/> must be
    ///     8 (=> triple-buffer) or 14 (=> double-buffer). Fire-and-forget: the panel reboots (~a few seconds)
    ///     and the caller should then (re)open its stream with the matching ColorBits.
    /// </summary>
    public static async Task SetColorModeAsync(string host, int colorBits, int webPort = DefaultWebPort,
                                               CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host required", nameof(host));
        var mode = colorBits <= 8 ? 8 : 14;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var basePath = $"http://{host}:{webPort}/cmd?c=";

        // Set the mode, persist it, then reboot so the firmware re-allocates its frame buffers.
        await http.GetAsync(basePath + Uri.EscapeDataString($"mode {mode}"), ct).ConfigureAwait(false);
        await http.GetAsync(basePath + Uri.EscapeDataString("save"), ct).ConfigureAwait(false);
        try { await http.GetAsync(basePath + Uri.EscapeDataString("reboot"), ct).ConfigureAwait(false); }
        catch { /* the reboot tears down the socket before replying -> expected */ }
    }
}
