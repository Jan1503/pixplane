# PixPlane

C# host library for the [verpixeld-panel](https://github.com/Jan1503/verpixeld-panel) firmware (RP2350 + W6300, ICND1065L 256×128). Feed a top-down BGRA frame; PixPlane packs the panel bit-planes and sends paced UDP.

The old working name was `RgbPanel`. The GitHub repository name can be changed later (Settings → General → Rename); old URLs redirect.

Used by [verpixeld](https://github.com/Jan1503/verpixeld) and [DeskCast](https://github.com/Jan1503/deskcast).

## What it does

| Type | Role |
|------|------|
| `PanelStreamer` | BGRA 256×128 → ICND bit-planes → paced UDP (default port 7777) |
| `PanelControl` | HTTP `/cmd` on the panel web UI (`livemode` live 8/14, or save+reboot) |
| `PanelDiscovery` | UDP 7778 probe (`VPXD`) plus HTTP `/status` fallback |
| `PanelColorSettings` | Live gamma / contrast / brightness / white-balance LUT |
| `SeamColumn` | Per-column 8-bit remap (knots or 256-entry LUT) plus optional gain/lift for the four scan-home columns |

No UI or graphics dependency. Target framework: `net10.0`.

## What's new

Dated from the public GitHub history. Newest first.

### 2026-08-24 — High-motion stays dirty-delta

A frame with more than half the 1440-byte slots dirty used to force a **full keyframe**, which doubled 14-bit video bitrate and crushed FPS. Keys are now the **first frame** and then **at most once per second** for UDP loss recovery. High-motion video stays deltas. Large dirty bursts are padded to a full `buf16` slot so send cadence stays even (20 ms then 90 ms gaps were worse than a steady full-slot rate).

Decision logic lives in `PackedSendPolicy` (unit-tested from verpixeld).

## Protocol

Datagram v2 (firmware 1.5+): `[frameId:1][fragIdx:1][nThis:1][flags:1]` + chunk (max 1440 bytes). Fragment n maps to `buf16` offset `n * 1440`. Flags: `0x01` keyframe (all slots), `0x02` commit (last packet of this frame). Unchanged frames are skipped; otherwise only dirty slots are sent, with a keyframe at least every second for loss recovery (high-motion frames stay deltas). Host and panel must be updated together. Colour depth is 8 / 10 / 13 / 14 bit-planes and must match the firmware. Firmware 1.7+ can switch 8/14 live via `livemode` (no reboot).

## Scan-home seam correction

This is a **host-side workaround for a panel/chip artifact**, not a protocol feature and not something the firmware can make physically correct.

On this 256×128 wall (two stacked 128×128 modules, 64-scan, landscape via `cfg_landscape + cfg_rot_cw + cfg_flip_x + cfg_flip_y`) the first line of each 1/64 scan group maps to **logical columns 63, 127, 191 and 255**. The same physical phenomenon appears four times — those are not four module joints. That first line has a **nonlinear** brightness offset: too dark in the shadows **and** too bright in the highlights. Affine `out = in * Gain + Lift` cannot invert an S-curve. Driver registers (REG03 and friends) and scan-timing / OE experiments were exhausted; extra 595 slots or OE blanks desync the mux from the ICND and add ghosts.

PixPlane therefore remaps the 8-bit source **on those columns only**, before the global colour LUT:

```
8-bit remap (per-column knots → 256-entry LUT)
        → gamma / contrast / brightness LUT
        → optional Bayer dither
        → out = in * Gain + Lift * fullScale
```

`SeamColumn` fields:

| Field | Meaning |
|-------|---------|
| `X` | Logical column (0..255). Defaults in the hosts: 63 / 127 / 191 / 255 |
| `Knots` | 8-bit in→out points (typical UI: 9 knots at 0, 32, …, 255). Linearly interpolated to 256 |
| `Lut` / `LutR` / `LutG` / `LutB` | Optional explicit 256-entry arrays (JSON numbers 0..255, not base64). Per-channel arrays override `Lut` / `Knots` |
| `GainR/G/B`, `LiftR/G/B` | Optional affine term **after** the remap. Old JSON with only gain/lift still works (remap is identity) |

`PanelStreamer.UpdateSeam(...)` rebuilds the maps live. Identity remaps take the fast path (no per-pixel lookup). The four columns usually share one curve because they are the same scan-home line.

Matching is done with a **host-generated grey fill** (verpixeld Settings → Seam → Wall grey). Firmware test patterns (`t f`) bypass this packer, so they cannot calibrate the curve.

<!-- PHOTO seam-uncorrected: grey ramp, four mismatched columns at 63/127/191/255 -->
<!-- PHOTO seam-corrected: same frame after the 9-point curve -->

UI and JSON live in the hosts: [verpixeld](https://github.com/Jan1503/verpixeld) (`seam_correction.json` with separate 8- and 14-bit profiles, `/api/settings/seam`) and [DeskCast](https://github.com/Jan1503/deskcast) (single-curve Seam window).

## Live 8/14-bit colour depth

The RP2350 has 520 KB SRAM. 14-bit double-buffer is ~448 KB; 8-bit triple-buffer is ~384 KB. **Both modes cannot be resident at once**, so a depth change has to reallocate the panel framebuffers.

Firmware **1.7** adds `livemode 8|14`: reallocates RAM, does not write flash, does not reboot. There is a brief hitch (UDP is paused) while the new buffers come up.

```csharp
await PanelControl.SetColorModeLiveAsync(host, colorBits: 8);   // firmware 1.7+
// then reopen PanelStreamer with ColorBits = 8

await PanelControl.SetColorModeAsync(host, colorBits: 14);      // persist + reboot (any 1.x)
```

`SetColorModeLiveAsync` waits until HTTP `/status` reports the new `bits` before returning. `SetColorModeAsync` still does `mode` + `save` + `reboot` for a persistent boot default.

verpixeld drives this from **visible canvases** (`ICanvas.PanelColorBits`, max of non-hidden layers). HDMI / SPI / GPIO / simulation ignore it. DeskCast still picks a fixed `--bits` for the session.

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
    TargetMbps = 19,
    Seam =
    [
        new SeamColumn { X = 63, Knots = SeamColumn.IdentityKnots() },
        new SeamColumn { X = 127, Knots = SeamColumn.IdentityKnots() },
        new SeamColumn { X = 191, Knots = SeamColumn.IdentityKnots() },
        new SeamColumn { X = 255, Knots = SeamColumn.IdentityKnots() }
    ]
});

stream.SendFrameBgra(bgra, strideBytes: 256 * 4);
stream.UpdateSeam(updatedColumns); // live
```

Discover panels on the LAN:

```csharp
var panels = await PanelDiscovery.ScanAsync();
```

## Build

```bash
dotnet build -c Release
```

VS project references resolve `obj\Debug\net10.0\ref\PixPlane.dll` — build **Debug** as well when iterating from the host solutions.

## Layout

```
PixPlane.csproj          Library
PackedSendPolicy.cs      Key vs dirty-delta decision (first frame + 1s recovery keys)
PanelStreamer.cs         Bit-plane pack + paced UDP + seam remap
PanelControl.cs          HTTP /cmd (livemode / mode+reboot)
PanelDiscovery.cs        UDP 7778 + HTTP /status
PanelStreamOptions.cs    Colour / seam / stream settings
```

## License

MIT. See `LICENSE`.
