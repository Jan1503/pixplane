# PixPlane

C# host library for the [verpixeld-panel](https://github.com/Jan1503/verpixeld-panel) firmware (RP2350 + W6300, ICND1065L 256×128). Feed a top-down BGRA frame; PixPlane packs the panel bit-planes and sends paced UDP.

The old working name was `RgbPanel`. The GitHub repository name can be changed later (Settings → General → Rename); old URLs redirect.

Used by [verpixeld](https://github.com/jan1503) and [DeskCast](https://github.com/Jan1503/deskcast).

## What it does

| Type | Role |
|------|------|
| `PanelStreamer` | BGRA 256×128 → ICND bit-planes → paced UDP (default port 7777) |
| `PanelControl` | HTTP `/cmd` on the panel web UI (colour mode 8/14, save, reboot) |
| `PanelDiscovery` | UDP 7778 probe (`VPXD`) plus HTTP `/status` fallback |
| `PanelColorSettings` | Live gamma / contrast / brightness / white-balance LUT |
| `SeamColumn` | Per-column gain/lift for the four scan-home boundaries |

No UI or graphics dependency. Target framework: `net10.0`.

## Protocol

Datagram v2 (firmware 1.5+): `[frameId:1][fragIdx:1][nThis:1][flags:1]` + chunk (max 1440 bytes). Fragment n maps to `buf16` offset `n * 1440`. Flags: `0x01` keyframe (all slots), `0x02` commit (last packet of this frame). Unchanged frames are skipped; otherwise only dirty slots are sent, with a keyframe at least every second. Host and panel must be updated together. Colour depth is 8 / 10 / 13 / 14 bit-planes and must match the firmware.

## Usage

Clone next to the consumer (verpixeld / DeskCast already do this):

```xml
<ProjectReference Include="..\pixplane\PixPlane.csproj" />
```

```csharp
using PixPlane;

using var stream = new PanelStreamer(new PanelStreamOptions
{
    Host = "192.168.10.181",
    Port = 7777,
    ColorBits = 14,
    TargetMbps = 19
});

stream.SendFrameBgra(bgra, strideBytes: 256 * 4);
```

Discover panels on the LAN:

```csharp
var panels = await PanelDiscovery.ScanAsync();
```

## Build

```bash
dotnet build -c Release
```

## Layout

```
PixPlane.csproj          Library
PanelStreamer.cs         Bit-plane pack + paced UDP
PanelControl.cs          HTTP /cmd
PanelDiscovery.cs        UDP 7778 + HTTP /status
PanelStreamOptions.cs    Colour / seam / stream settings
```

## License

MIT. See `LICENSE`.
