using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace PixPlane;

/// <summary>
///     Streams frames to the RP2350 + W6300 "pioq" receiver over UDP. Packs the exact panel bit-plane
///     buffer (buf16, geometry baked in), fragments it and PACES the send to a target bitrate just below
///     the MCU's drain rate. Feed a top-down BGRA frame (256x128); no graphics-library dependency.
///     Datagram v2: [frameId:1][fragIdx:1][nThis:1][flags:1] + chunk(&lt;=1440).
///     flags: 0x01 keyframe (all fragments), 0x02 commit (last packet of this frame).
    ///     Unchanged frames are skipped; otherwise only dirty 1440-byte slots are sent, with a keyframe
    ///     at least every second for loss recovery. High-motion frames stay deltas (a &gt;50% dirty
    ///     key used to double video bitrate). Full (key) frames are paced only by TargetMbps — no extra
    ///     inter-frame gap, that crushed fps on video.
///     Geometry: landscape 256x128 -&gt; stacked 128x256 (rot_cw + flip_x + flip_y), 4 data groups.
///     Colour + seam are live-adjustable (UpdateColor / UpdateSeam).
/// </summary>
public sealed class PanelStreamer : IDisposable
{
    private const int PanelWidth = 128;
    private const int PolDispl = 64;
    private const int HalfH = 128;
    private const int Displ = PanelWidth * PolDispl; // 8192 uint16 per plane
    private const int Chunk = 1440; // UDP payload chunk (one Ethernet frame)

    // 4x4 Bayer matrix (0..15). Spatial ordered dither pattern.
    private static readonly int[] Bayer4 =
    {
        0, 8, 2, 10,
        12, 4, 14, 6,
        3, 11, 1, 9,
        15, 7, 13, 5
    };

    private double _bps;          // current pacing (adapts when the panel acknowledges frames)
    private readonly double _bpsMax;
    private readonly byte[] _buf16;
    private readonly int _buf16Bytes;

    private readonly int[] _cellOff;
    private readonly byte[] _prev;
    private readonly byte[] _dirty;
    private readonly byte[] _dg;
    private bool _havePrev;
    private bool _dirtySinceKey;
    private double _lastKeySec = -1e9;
    private readonly byte[] _gbase;

    private readonly string _host;
    private readonly int _maxVal;
    private readonly int _nFrags;
    private readonly int _nPlanes;
    private readonly int _port;

    private Socket? _sock;
    private readonly Stopwatch _sw = new();
    private readonly Thread _watch;
    private int _disposed;
    private volatile bool _online = true;
    private volatile bool _dither = true;
    private byte _frameId;
    private ushort[] _lutB = new ushort[256];
    private ushort[] _lutG = new ushort[256];

    // Colour LUT (8-bit in -> nPlanes-bit out) rebuilt on UpdateColor.
    private ushort[] _lutR = new ushort[256];

    // Per-column seam gain (Q8) + lift (absolute) + optional 8-bit remap, rebuilt on UpdateSeam.
    private int[] _seamGainR, _seamGainG, _seamGainB;
    private int[] _seamLiftR, _seamLiftG, _seamLiftB;
    private byte[]?[] _seamMapR, _seamMapG, _seamMapB;
    private volatile bool _seamMapsOn;
    private ushort[] _stepB = new ushort[256];

    private ushort[] _stepG = new ushort[256];

    // Per-level gap to the next LUT entry (13-bit units), used as the ordered-dither spread per 8-bit level.
    private ushort[] _stepR = new ushort[256];
    private volatile bool _swapRedBlue;

    public PanelStreamer(PanelStreamOptions opt)
    {
        ArgumentNullException.ThrowIfNull(opt);
        Width = opt.WallWidth;
        Height = opt.WallHeight;
        if (Width != 256 || Height != 128)
            throw new NotSupportedException($"Only a 256x128 wall is supported (got {Width}x{Height}).");

        _host = string.IsNullOrWhiteSpace(opt.Host) ? "192.168.10.181" : opt.Host;
        _port = opt.Port > 0 ? opt.Port : 7777;
        _bps = (opt.TargetMbps > 0 ? opt.TargetMbps : 19.0) * 1_000_000.0;
        _bpsMax = Math.Max(_bps, (opt.MaxMbps > 0 ? opt.MaxMbps : 95.0) * 1_000_000.0);
        _bpsFloor = _bps * 0.5;
        _adaptive = opt.AdaptivePacing;
        _nPlanes = opt.ColorBits switch { 10 => 10, 13 => 13, 14 => 14, _ => 8 };
        _maxVal = (1 << _nPlanes) - 1;

        _buf16Bytes = _nPlanes * Displ * 2;
        _buf16 = new byte[_buf16Bytes];
        _nFrags = (_buf16Bytes + Chunk - 1) / Chunk;
        if (_nFrags > 255)
            throw new InvalidOperationException("too many fragments for a 1-byte count");
        _dg = new byte[4 + Chunk];
        _prev = new byte[_buf16Bytes];
        _dirty = new byte[_nFrags];

        _cellOff = new int[Width * Height];
        _gbase = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var (cell, gb) = MapPixel(x, y);
            _cellOff[y * Width + x] = cell;
            _gbase[y * Width + x] = (byte)gb;
        }

        _seamGainR = NewGain();
        _seamGainG = NewGain();
        _seamGainB = NewGain();
        _seamLiftR = new int[Width];
        _seamLiftG = new int[Width];
        _seamLiftB = new int[Width];
        _seamMapR = new byte[]?[Width];
        _seamMapG = new byte[]?[Width];
        _seamMapB = new byte[]?[Width];

        UpdateColor(opt.Color ?? new PanelColorSettings());
        UpdateSeam(opt.Seam ?? []);

        OpenSocket();
        _sw.Restart();
        _watch = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "pixplane-watch-" + _host
        };
        _watch.Start();
    }

    public int Width { get; }
    public int Height { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _online = false;
        DropSocket();
        try { _watch.Join(2000); } catch (Exception) { }
    }

    /// <summary>
    ///     Video UDP stays blocking and paced. A second socket asks the panel on port 7778.
    ///     Two missed replies close the video socket, so a pulled cable cannot keep filling the
    ///     shared NIC queue. The socket is reopened when the panel answers again.
    /// </summary>
    private void WatchLoop()
    {
        var misses = 0;
        var proven = false;
        while (Volatile.Read(ref _disposed) == 0)
        {
            var ok = false;
            try { ok = ProbeOnce(); }
            catch (Exception) { ok = false; }
            if (Volatile.Read(ref _disposed) != 0) return;

            if (ok)
            {
                misses = 0;
                if (!proven)
                    proven = true;
                if (!_online)
                {
                    try
                    {
                        OpenSocket();
                        _online = true;
                        Console.WriteLine($"[PIX] {_host} answering again, stream resumed");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PIX] {_host} socket reopen failed: {ex.Message}");
                    }
                }

                Thread.Sleep(400);
                continue;
            }

            if (!proven)
            {
                Thread.Sleep(400);
                continue;
            }

            misses++;
            if (_online && misses >= 2)
            {
                _online = false;
                _havePrev = false;
                DropSocket();
                Console.WriteLine($"[PIX] {_host} not answering, this panel is paused");
            }

            Thread.Sleep(_online ? 50 : 400);
        }
    }

    private bool ProbeOnce()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        probe.Blocking = false;
        var dest = new IPEndPoint(IPAddress.Parse(_host), 7778);
        var pkt = new byte[] { (byte)'V', (byte)'P', (byte)'X', (byte)'D', 0x01 };
        try
        {
            probe.SendTo(pkt, dest);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
        {
            if (!probe.Poll(30_000, SelectMode.SelectWrite)) return false;
            try { probe.SendTo(pkt, dest); }
            catch (SocketException) { return false; }
        }
        catch (SocketException)
        {
            return false;
        }

        var buf = new byte[128];
        var until = Environment.TickCount64 + 350;
        while (Environment.TickCount64 < until && Volatile.Read(ref _disposed) == 0)
        {
            if (!probe.Poll(20_000, SelectMode.SelectRead)) continue;
            try
            {
                var n = probe.Receive(buf);
                if (n >= 5 && buf[0] == (byte)'V' && buf[1] == (byte)'P' && buf[2] == (byte)'X' && buf[3] == (byte)'D' && buf[4] == 0x02)
                    return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
            {
            }
            catch (SocketException)
            {
                return false;
            }
        }

        return false;
    }

    private void OpenSocket()
    {
        DropSocket();
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { sock.SendBufferSize = 32 * 1024; }
        catch (SocketException) { }
        sock.Connect(new IPEndPoint(IPAddress.Parse(_host), _port));
        _sock = sock;
    }

    private void DropSocket()
    {
        var sock = Interlocked.Exchange(ref _sock, null);
        if (sock == null) return;
        try { sock.Dispose(); }
        catch (Exception) { }
    }

    private int[] NewGain()
    {
        var a = new int[Width];
        Array.Fill(a, 256);
        return a;
    }

    // landscape 256x128 -> stacked 128x256 with rot_cw + flip_x + flip_y (matches the firmware mapPixel).
    private static (int cellOff, int gbase) MapPixel(int x, int y)
    {
        var px = 127 - y; // rot_cw
        var py = x;
        var col = 127 - px; // flip_x (== y)
        var bottom = py >= HalfH ? 1 : 0;
        var within = bottom == 1 ? py - HalfH : py;
        within = 127 - within; // flip_y
        var lower = within >= PolDispl ? 1 : 0;
        var rowaddr = lower == 1 ? within - PolDispl : within;
        var gbase = (bottom == 1 ? 0 : 6) + (lower == 1 ? 3 : 0);
        return (rowaddr * PanelWidth + col, gbase);
    }

    /// <summary>Rebuild the colour LUTs from live settings (gamma / contrast / brightness / white balance).</summary>
    public void UpdateColor(PanelColorSettings c)
    {
        ArgumentNullException.ThrowIfNull(c);
        _swapRedBlue = c.SwapRedBlue;
        _dither = c.Dither;
        _lutR = BuildLut(c, c.GainR, _maxVal);
        _lutG = BuildLut(c, c.GainG, _maxVal);
        _lutB = BuildLut(c, c.GainB, _maxVal);
        _stepR = BuildSteps(_lutR);
        _stepG = BuildSteps(_lutG);
        _stepB = BuildSteps(_lutB);
    }

    // Gap between each level and the next (13-bit units) = the dither spread that exactly tiles the band.
    private static ushort[] BuildSteps(ushort[] lut)
    {
        var s = new ushort[256];
        for (var i = 0; i < 255; i++) s[i] = (ushort)(lut[i + 1] - lut[i]);
        s[255] = s[254];
        return s;
    }

    /// <summary>Rebuild the per-column seam correction from live settings.</summary>
    public void UpdateSeam(IEnumerable<SeamColumn> seam)
    {
        var gr = NewGain();
        var gg = NewGain();
        var gb = NewGain();
        var lr = new int[Width];
        var lg = new int[Width];
        var lb = new int[Width];
        var mr = new byte[]?[Width];
        var mg = new byte[]?[Width];
        var mb = new byte[]?[Width];
        var mapsOn = false;
        foreach (var s in seam)
        {
            if (s.X < 0 || s.X >= Width) continue;
            gr[s.X] = GainQ8(s.GainR);
            gg[s.X] = GainQ8(s.GainG);
            gb[s.X] = GainQ8(s.GainB);
            lr[s.X] = LiftAbs(s.LiftR);
            lg[s.X] = LiftAbs(s.LiftG);
            lb[s.X] = LiftAbs(s.LiftB);
            var maps = s.BuildMaps();
            mr[s.X] = maps.R;
            mg[s.X] = maps.G;
            mb[s.X] = maps.B;
            if (maps.R != null || maps.G != null || maps.B != null) mapsOn = true;
        }

        _seamGainR = gr;
        _seamGainG = gg;
        _seamGainB = gb;
        _seamLiftR = lr;
        _seamLiftG = lg;
        _seamLiftB = lb;
        _seamMapR = mr;
        _seamMapG = mg;
        _seamMapB = mb;
        _seamMapsOn = mapsOn;
    }

    private static int GainQ8(double gain)
    {
        return (int)Math.Clamp(Math.Round(gain * 256.0), 0, 4096);
    }

    private int LiftAbs(double lift)
    {
        return (int)Math.Clamp(Math.Round(lift * _maxVal), 0, _maxVal);
    }

    private static ushort[] BuildLut(PanelColorSettings c, double gain, int maxVal)
    {
        var curve = (c.Curve ?? "none").Trim().ToLowerInvariant();
        var gamma = c.Gamma <= 0 ? 2.2 : c.Gamma;
        var contrast = c.Contrast <= 0 ? 1.0 : c.Contrast;
        var scale = gain * Math.Clamp(c.Brightness <= 0 ? 1.0 : c.Brightness, 0.0, 4.0);
        var lut = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var y = curve switch
            {
                "gamma" => Math.Pow(i / 255.0, gamma),
                "none" => i / 255.0,
                _ => CieLuminance(i / 255.0 * 100.0) // cie1931
            };
            y = (y - 0.5) * contrast + 0.5; // contrast around mid-grey
            if (y < 0) y = 0;
            else if (y > 1) y = 1;
            lut[i] = (ushort)Math.Clamp(Math.Round(y * scale * maxVal), 0, maxVal);
        }

        return lut;
    }

    private static double CieLuminance(double l)
    {
        return l <= 8.0 ? l / 903.3 : Math.Pow((l + 16.0) / 116.0, 3.0);
    }

    /// <summary>Pack a top-down BGRA frame (256x128, 4 bytes/pixel) and stream it, paced.</summary>
    public void SendFrameBgra(ReadOnlySpan<byte> bgra, int strideBytes)
    {
        if (!_online || _sock == null) return;
        if (strideBytes < Width * 4) throw new ArgumentException("stride too small", nameof(strideBytes));
        WaitForAck();
        if (bgra.Length < strideBytes * (Height - 1) + Width * 4)
            throw new ArgumentException("frame buffer too small", nameof(bgra));

        Array.Clear(_buf16, 0, _buf16Bytes);
        var buf = _buf16;
        var cellOffs = _cellOff;
        var gbases = _gbase;
        var lutR = _lutR;
        var lutG = _lutG;
        var lutB = _lutB;
        var stepR = _stepR;
        var stepG = _stepG;
        var stepB = _stepB;
        var gainR = _seamGainR;
        var gainG = _seamGainG;
        var gainB = _seamGainB;
        var liftR = _seamLiftR;
        var liftG = _seamLiftG;
        var liftB = _seamLiftB;
        var mapR = _seamMapR;
        var mapG = _seamMapG;
        var mapB = _seamMapB;
        var mapsOn = _seamMapsOn;
        var swap = _swapRedBlue;
        var dither = _dither;
        var nPlanes = _nPlanes;
        var maxVal = _maxVal;
        const int planeStrideBytes = Displ * 2;

        for (var y = 0; y < Height; y++)
        {
            var row = y * strideBytes;
            var pixRow = y * Width;
            var bayerRow = (y & 3) * 4;
            for (var x = 0; x < Width; x++)
            {
                var o = row + x * 4;
                int b8 = bgra[o];
                int g8 = bgra[o + 1];
                int r8 = bgra[o + 2];
                if (swap) (r8, b8) = (b8, r8);

                if (mapsOn)
                {
                    var rm = mapR[x];
                    if (rm != null) r8 = rm[r8];
                    var gm = mapG[x];
                    if (gm != null) g8 = gm[g8];
                    var bm = mapB[x];
                    if (bm != null) b8 = bm[b8];
                }

                int r = lutR[r8], g = lutG[g8], b = lutB[b8];

                if (dither)
                {
                    // Spread each 8-bit level across its local 13-bit gap (t in -8..7) so band edges blend.
                    var t = Bayer4[bayerRow + (x & 3)] - 8;
                    r += (t * stepR[r8]) >> 4;
                    if (r < 0) r = 0;
                    g += (t * stepG[g8]) >> 4;
                    if (g < 0) g = 0;
                    b += (t * stepB[b8]) >> 4;
                    if (b < 0) b = 0;
                }

                r = ((r * gainR[x]) >> 8) + liftR[x];
                if (r > maxVal) r = maxVal;
                g = ((g * gainG[x]) >> 8) + liftG[x];
                if (g > maxVal) g = maxVal;
                b = ((b * gainB[x]) >> 8) + liftB[x];
                if (b > maxVal) b = maxVal;

                if ((r | g | b) == 0) continue;

                var pix = pixRow + x;
                var cellByte = cellOffs[pix] * 2;
                var gbase = gbases[pix];
                for (var p = 0; p < nPlanes; p++)
                {
                    var bit = ((r >> p) & 1) | (((g >> p) & 1) << 1) | (((b >> p) & 1) << 2);
                    if (bit == 0) continue;
                    var val = bit << gbase;
                    var idx = cellByte + p * planeStrideBytes;
                    buf[idx] |= (byte)(val & 0xFF);
                    buf[idx + 1] |= (byte)((val >> 8) & 0xFF);
                }
            }
        }

        SendPacked();
    }

    private const byte FlagKey = 0x01;
    private const byte FlagCommit = 0x02;
    private const double KeyframeSec = 1.0;

    private void SendPacked()
    {
        var now = _sw.Elapsed.TotalSeconds;
        var nChanged = _nFrags;
        if (_havePrev)
        {
            nChanged = 0;
            for (var fi = 0; fi < _nFrags; fi++)
            {
                var off = fi * Chunk;
                var len = Math.Min(Chunk, _buf16Bytes - off);
                var changed = !_buf16.AsSpan(off, len).SequenceEqual(_prev.AsSpan(off, len));
                _dirty[fi] = (byte)(changed ? 1 : 0);
                if (changed) nChanged++;
            }
        }

        // The panel asks for a key (VPXD 0x03) when it lost a delta: until a key arrives, the slots
        // that delta changed would keep showing old pixels.
        PollFeedback(0);
        var keyRequested = _keyPending;
        _keyPending = false;
        if (keyRequested) SlowDown();
        var dueKey = !_havePrev || keyRequested || now - _lastKeySec >= KeyframeSec;
        bool key;
        if (keyRequested)
            key = true;
        else if (!PackedSendPolicy.TrySend(_havePrev, dueKey, nChanged, _dirtySinceKey, out key))
            return;

        var nThis = key ? _nFrags : nChanged;

        _awaitId = SendPaced(key, nThis);
        Buffer.BlockCopy(_buf16, 0, _prev, 0, _buf16Bytes);
        _havePrev = true;
        if (key)
        {
            _lastKeySec = _sw.Elapsed.TotalSeconds;
            _dirtySinceKey = false;
        }
        else
        {
            _dirtySinceKey = true;
        }
    }

    private readonly byte[] _rx = new byte[64];

    /// <summary>Key-frame requests received from the panel (each one answered with a key).</summary>
    public long KeyRequests { get; private set; }

    /// <summary>Frames the panel acknowledged (firmware 1.11+ flow control).</summary>
    public long Acks { get; private set; }

    /// <summary>Frames sent without an acknowledgement within <see cref="AckTimeoutMs"/>.</summary>
    public long AckTimeouts { get; private set; }

    /// <summary>Current pacing in Mbit/s (starts at TargetMbps, adapts between 10 and MaxMbps).</summary>
    public double CurrentMbps => _bps / 1_000_000.0;

    /// <summary>True while the panel acknowledges frames (flow control active).</summary>
    public bool FlowControl => _ackSeen;

    // After the last datagram the panel needs at most ~2.5 ms correction + ~8.4 ms upload wait before it
    // acks; 30 ms covers that with room for the network and keeps a lost frame from stalling the video.
    private const int AckTimeoutMs = 30;
    private const double CeilProbeSec = 60.0;   // time without a cut before the learned ceiling may rise
    private const double LossWindowSec = 10.0;  // a cut needs LossesForCut losses within this window
    private const int LossesForCut = 3;
    private double _bpsCeil = double.MaxValue;  // learned pacing ceiling
    private double _lastCutSec;
    private readonly Queue<double> _losses = new();
    private double _bpsFloor;
    private readonly bool _adaptive;
    private const double MinBps = 10_000_000.0;
    private int _awaitId = -1;     // frame id waiting for its ack, -1 = none
    private int _lastAckId = -1;
    private bool _ackSeen;
    private int _ackMisses;
    private bool _keyPending;

    /// <summary>
    ///     Flow control: firmware 1.11+ acknowledges every frame it has taken over (VPXD 0x04 id). The
    ///     next frame is only sent after that, so it never arrives while the panel still waits for an
    ///     upload (which overflowed its 16 KB socket buffer and dropped the frame). Each panel has its own
    ///     streamer and pump thread, so a slow panel only skips its own frames. Until the first ack is
    ///     seen (older firmware) nothing waits.
    /// </summary>
    /// <summary>
    ///     Wait until the panel acknowledged the previous frame. Call it before taking the next frame
    ///     from the source, so the freshest frame goes out (SendFrameBgra calls it too).
    /// </summary>
    public void WaitReady() => WaitForAck();

    private void WaitForAck()
    {
        if (_awaitId < 0) return;
        if (!_ackSeen)
        {
            PollFeedback(0);
            if (!_ackSeen) return;
        }

        var until = _sw.Elapsed.TotalMilliseconds + AckTimeoutMs;
        while (_lastAckId != _awaitId)
        {
            var left = until - _sw.Elapsed.TotalMilliseconds;
            if (left <= 0)
            {
                // no ack: the frame may be lost, so the next one goes out as a key (even if nothing changed)
                AckTimeouts++;
                _keyPending = true;
                SlowDown();
                if (++_ackMisses >= 10)
                {
                    _ackSeen = false;   // panel stopped acknowledging (older firmware, test mode): stop waiting
                    _ackMisses = 0;
                }

                break;
            }

            PollFeedback((int)Math.Ceiling(left));
        }

        if (_lastAckId == _awaitId)
        {
            _ackMisses = 0;
            SpeedUp();
        }

        _awaitId = -1;
    }

    // Pacing: rise towards the ceiling; lower the ceiling only when losses pile up. Measured with a
    // 24 fps source (all slots changing): up to ~75 Mbit/s the panel loses a frame every 15-30 s no
    // matter the rate, above ~80 the losses rise steeply. Lowering the rate on every single loss
    // ratcheted it down to ~44 Mbit/s, too slow for 24 fps; probing up again after each loss cost a
    // frame every ~2 s (visible micro stutter). So: cut by 8 % after 3 losses within 10 s, and let the
    // ceiling rise by 1 Mbit/s only after 60 s without a cut.
    private void SpeedUp()
    {
        if (!_adaptive) return;
        var now = _sw.Elapsed.TotalSeconds;
        if (_bpsCeil < _bpsMax && now - _lastCutSec >= CeilProbeSec)
        {
            _bpsCeil = Math.Min(_bpsMax, _bpsCeil + 1_000_000.0);
            _lastCutSec = now;
        }

        _bps = Math.Min(Math.Min(_bpsMax, _bpsCeil), _bps + 500_000.0);
    }

    private void SlowDown()
    {
        if (!_ackSeen || !_adaptive) return;
        var now = _sw.Elapsed.TotalSeconds;
        _losses.Enqueue(now);
        while (_losses.Count > 0 && now - _losses.Peek() > LossWindowSec) _losses.Dequeue();
        if (_losses.Count < LossesForCut) return;
        _losses.Clear();
        _bpsCeil = Math.Max(Math.Max(MinBps, _bpsFloor), Math.Min(_bpsCeil, _bps) * 0.92);
        _bps = _bpsCeil;
        _lastCutSec = now;
    }

    /// <summary>Read panel feedback (acks, key requests); waits up to <paramref name="waitMs"/> for the first datagram.</summary>
    private void PollFeedback(int waitMs)
    {
        var sock = _sock;
        if (sock == null) return;
        try
        {
            if (sock.Available == 0 && (waitMs <= 0 || !sock.Poll(waitMs * 1000, SelectMode.SelectRead)))
                return;
            while (sock.Available > 0)
            {
                var n = sock.Receive(_rx);
                if (n < 5 || _rx[0] != (byte)'V' || _rx[1] != (byte)'P' || _rx[2] != (byte)'X' || _rx[3] != (byte)'D')
                    continue;
                if (_rx[4] == 0x03)
                {
                    _keyPending = true;
                    KeyRequests++;
                }
                else if (_rx[4] == 0x04 && n >= 6)
                {
                    _lastAckId = _rx[5];
                    _ackSeen = true;
                    Acks++;
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
            // e.g. ICMP port unreachable reported on a connected UDP socket
        }
    }

    private int SendPaced(bool key, int nThis)
    {
        var id = _frameId++;
        var frameStart = _sw.Elapsed.TotalSeconds;
        long frameBytes = 0;
        var dg = _dg;
        var sent = 0;

        for (var fi = 0; fi < _nFrags; fi++)
        {
            if (!key && _dirty[fi] == 0) continue;
            sent++;
            var off = fi * Chunk;
            var len = Math.Min(Chunk, _buf16Bytes - off);
            dg[0] = id;
            dg[1] = (byte)fi;
            dg[2] = (byte)nThis;
            dg[3] = (byte)((key ? FlagKey : 0) | (sent == nThis ? FlagCommit : 0));
            Buffer.BlockCopy(_buf16, off, dg, 4, len);
            var sock = _sock;
            if (!_online || sock == null) return -1;
            try
            {
                sock.Send(dg, 0, 4 + len, SocketFlags.None);
            }
            catch (ObjectDisposedException)
            {
                return -1;
            }
            catch (SocketException)
            {
                return -1;
            }

            frameBytes += 4 + len;
            var deadline = frameStart + frameBytes * 8.0 / _bps;
            var wait = deadline - _sw.Elapsed.TotalSeconds;
            if (wait < -0.0005)
            {
                // Behind schedule (thread preempted, GC, ...): do not catch up. Catching up sent the
                // following datagrams back to back at line rate, which overflowed the panel's 16 KB
                // socket buffer (W6300) and lost the frame. Shift the schedule instead.
                frameStart -= wait;
                wait = 0;
            }
            if (wait > 0.0005)
                Thread.Sleep((int)((wait - 0.0003) * 1000)); // coarse
            while (_sw.Elapsed.TotalSeconds < deadline)
            {
            } // fine busy-wait
        }

        // High-motion dirty sends used to finish in 20ms then the next took 90ms — that cadence
        // stutter is worse than a steady ~full-keyframe rate. Pad to a full buf16 slot whenever
        // this frame already used at least half the link budget (video). Sparse UI frames stay snappy.
        // With flow control the panel's ack sets the cadence; padding would only cost frames.
        if (!_ackSeen && frameBytes * 2 >= _buf16Bytes)
        {
            var slot = frameStart + _buf16Bytes * 8.0 / _bps;
            var pad = slot - _sw.Elapsed.TotalSeconds;
            if (pad > 0.0005)
                Thread.Sleep((int)((pad - 0.0003) * 1000));
            while (_sw.Elapsed.TotalSeconds < slot)
            {
            }
        }

        return id;
    }
}