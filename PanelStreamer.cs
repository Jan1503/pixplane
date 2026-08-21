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
///     at least every second (or when more than half the slots changed). Full (key) frames are paced
///     only by TargetMbps — no extra inter-frame gap, that crushed fps on video.
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

    private readonly double _bps;
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

    private readonly Socket _sock;
    private readonly Stopwatch _sw = new();
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

        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sock.Connect(new IPEndPoint(IPAddress.Parse(_host), _port)); // "connect" UDP -> Send()
        _sw.Restart();
    }

    public int Width { get; }
    public int Height { get; }

    public void Dispose()
    {
        _sock.Dispose();
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
        if (strideBytes < Width * 4) throw new ArgumentException("stride too small", nameof(strideBytes));
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

        var dueKey = !_havePrev || now - _lastKeySec >= KeyframeSec;
        if (nChanged == 0)
        {
            if (!dueKey || !_dirtySinceKey) return;
        }

        var key = !_havePrev || dueKey || nChanged * 2 >= _nFrags || nChanged == 0;
        var nThis = key ? _nFrags : nChanged;

        SendPaced(key, nThis);
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

    private void SendPaced(bool key, int nThis)
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
            try
            {
                _sock.Send(dg, 0, 4 + len, SocketFlags.None);
            }
            catch (SocketException)
            {
                return;
            }

            frameBytes += 4 + len;
            var deadline = frameStart + frameBytes * 8.0 / _bps;
            var wait = deadline - _sw.Elapsed.TotalSeconds;
            if (wait > 0.0005)
                Thread.Sleep((int)((wait - 0.0003) * 1000)); // coarse
            while (_sw.Elapsed.TotalSeconds < deadline)
            {
            } // fine busy-wait
        }
    }
}