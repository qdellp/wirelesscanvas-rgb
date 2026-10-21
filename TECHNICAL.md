# Technical notes

How WirelessCanvas RGB talks to LIAN LI wireless hardware, what it deliberately does not do, and what is verified. User documentation is in [README.md](README.md).

## Basis

- Protocol semantics come from [lian-li-linux](https://github.com/sgtaziz/lian-li-linux) **v1.2.2** (commit `73653a10c41318568df79e821f82a856cfcfadeb`, MIT, sgtaziz), kept unmodified as the `upstream/` submodule. Upstream is Rust/Tauri for Linux; this project is a separate C# port, not a Windows build of the upstream client.
- C# / .NET 10 / WinForms, x64, self-contained single-file publish. USB goes through WinUSB directly (`windows/Usb.cs`), with no extra drivers.
- tinyuz compression (`tinyuz.dll`, `tuz-dec.dll`) is built from the unmodified tinyuz/HDiffPatch sources in `upstream/vendor` with Zig (`scripts/build-native.ps1`). Self-tests check the round trip byte for byte against upstream `.yuz` fixtures.

## Source layout

| File | Purpose |
|---|---|
| `Program.cs` | Entry point, command-line modes (`--self-test`, `--render-*`, read-only probes), data folder and log |
| `MainForm.cs`, `Ui.cs`, `*Panel.cs` | Window, tabs and controls |
| `Controller.cs`, `Protocol.cs`, `RgbTransfer.cs` | L-Wireless controller: discovery, RGB upload, confirmation and retry, speed commands, radio slot recovery |
| `FanModels.cs` | Wireless fan families, LED layouts and PWM limits |
| `Gradient.cs`, `Effects.cs`, `CanvasField.cs`, `CaseLayout.cs` | Profile, canvas color function, shader effects, reference case layout |
| `HydroProtocol.cs`, `LcdProtocol.cs`, `Screen*.cs`, `Dashboard.cs` | HydroShift II pump and SL LCD screens |
| `OpenRgb*.cs`, `Keyboard*.cs` | OpenRGB SDK client, SDK server bridge and keyboard lighting |
| `Cooling*.cs` | Sensors, fan curves and pump speed |
| `Startup.cs` | One-time elevation and start with Windows |
| `Localization.cs`, `Translations.json` | UI strings: English source, catalog for ru, zh-Hans, es, pt |
| `SelfTests.cs` | 80 groups of hardware-free self-tests |

## Wireless protocol (from upstream)

| Part | Upstream file | Here |
|---|---|---|
| VID/PID | `crates/lianli-devices/src/wireless/mod.rs` | V1 TX `0416:8040`, RX `0416:8041`; V2 TX `1A86:E304`, RX `1A86:E305` |
| USB transfers | `crates/lianli-transport/src/usb.rs` | WinUSB, endpoints `0x01/0x81`, 64-byte packets |
| Master discovery | `wireless/controller.rs::discover_master_mac` | `0x11` on channels 1–39, MAC and clock checks |
| Group list | `wireless/discovery.rs` | RX GetDev `0x10`, up to 26 pages of 42-byte records with marker `0x1C` |
| Fan types | `wireless/fan_type.rs` | Type bytes 20–26 SL V3, 27–35 TL, 36–39 SL-INF, 40–42 CL, 43–50 SL-INF V3, 51–58 TL V3, 59–62 SL V4, 63 P28, 126–127 CL V2 |
| RGB cycle | `wireless/rgb.rs` | RF `0x12/0x20`, 220-byte chunks, at most 12,288 compressed bytes, up to 4096 frames |
| Keep-alive | `controller.rs::master_clock_rf_data` | RF `0x14` once per second, sentinel `0x14` in bytes 14–63, no sensor or PWM data |
| Speed | `wireless/fan_speed.rs` | RF `0x12/0x10`, repeated every second; without it fans return to hardware speed |
| Motherboard sync | `wireless/mb_sync.rs` | RF `0x27`, only to turn motherboard RGB sync off |
| Pump | `controllers/aio.rs`, `h2_aio.rs` | Wireless theme `0x12/0x19`, 32-byte parameter block `0x12/0x21` |
| OpenRGB | `lianli-daemon/src/openrgb_server.rs` | SDK server, protocol 0–4, Direct, controller/zone/single LED |

Each 240-byte RF packet goes out as four USB packets: `[0x10, part, channel, slot] + 60 bytes`. Only groups bound to the current master, with a valid radio slot (1–13) seen three times in a row, are addressed. An ambiguous TX/RX pair blocks the connection.

The RGB header carries device and master MAC, an FNV effect id, compressed length, frame count, LED count and the frame interval in RF ticks (1 tick = 0.625 ms). The header is sent four times 20 ms apart. A group that has not confirmed the new effect index within 1.8 s, or that falls back to its built-in rainbow, gets the cycle again: only that group, up to 6 times. A partial upload is never resumed from the middle.

## Canvas and effects

Each LED has a world position: the fan position, rotation, scale and viewing elevation, applied to the model's local LED layout. Color is one function of position and time:

- Gradient: `phase = (x·cos a + y·sin a) / wavelength − t / period`, interpolated in RGB or along the short HSV arc.
- Effects (`Effects.cs`): each shader returns a palette position U and a brightness V over the background: `color = background + (palette(U) − background) · V`.

Devices loop the uploaded frames, so every effect must be seamless over the cycle: speed is an integer number of repeats per cycle, particles move on closed paths, and fire blends the end of the cycle into its start. Self-tests check that the color at the wrap changes by no more than 3/255.

Device frames get LED gamma (default 2.2; LEDs are linear, screens are not) and a channel-sum limit of 600, as in upstream `sl_wireless/engine.rs`. Global brightness applies to devices only; the preview shows full colors.

All groups and the pump ring share one frame count and interval, and the frame is chosen from the shared master clock, so groups stay in phase. If any group's compressed cycle exceeds the memory limit, the frame count of the whole scene is reduced while the duration is kept.

SL V3 LED map (verified on hardware with a contrast-stripe video): two chamfered light bars per fan, banks of 12 inner + 8 outer LEDs, twice; all four tracks run in the +X direction. Layouts of the other families come from upstream `lianli-media` and are not calibrated. `LedOverrides` in a profile can replace the local LED coordinates of one fan (`MAC/fan` → list of `{X, Y}`).

## Pump and LCD screens

- HydroShift II LCD-C (`1CBE:A021`) bound to L-Wireless: the ring is driven over radio in the same upload as the fans. USB is used only for its screen and for an unbound pump. The pump speed is a fixed RPM (1600–2500) sent in the parameter block; it never follows temperature.
- SL LCD fans (`1CBE:0005`) and the pump screen use the LCD protocol: a 512-byte header DES-CBC encrypted with the key from upstream `crypto.rs`. Only version, brightness, frame rate, clock, stop and JPEG frame commands are built; boot image, desktop mode and firmware commands are never built. Self-tests compare headers with OpenSSL output (`scripts/lcd_vectors.py`).

## OpenRGB

- Client (`OpenRgbClient.cs`) to an OpenRGB SDK server on `127.0.0.1:6742`: reads descriptions and sets Direct or a per-LED mode (`Static` for some RAM that ignores Direct). It never sends profile-save or rescan commands.
- Server (`OpenRgb.cs`, port 6743) exposes the fan groups to other OpenRGB clients, limited to 5 live frames per second for all groups together.

## Data and process

- Data folder: `%LOCALAPPDATA%\WirelessCanvasRGB` (profile, settings, `application.log`). Releases up to 0.13 used `%LOCALAPPDATA%\LianLi.Windows`; it is copied once on first launch.
- One UAC prompt at launch. The CPU sensor runs as an elevated helper (`--cpu-sensor`) with LibreHardwareMonitor and PawnIO (no WinRing0).
- Start with Windows: a Task Scheduler task `WirelessCanvasRGB` (highest privileges, no time limit) when elevated, otherwise the HKCU Run key. Older tasks named `LianLi.Windows` are replaced.
- Closing the app stops speed commands, the pump command, screen threads and the OpenRGB processes the app started, then releases USB.

## Deliberately not implemented

- Pairing, unpairing or binding new groups, firmware updates, USB resets.
- Changing drivers or Windows services. The app refuses to connect while L-Connect 3 is running and never stops its services itself.
- Restoring the previous L-Connect effect on exit.
- Lighting or speed for unknown or mixed fan types.

## Verified

- On hardware (author's system): SL Wireless 120 RGB and SL Wireless 120 LCD on an L-Wireless controller, four groups (2 + 1 + 3 + 3 fans); RGB upload and confirmation, LED map, shared phase, pump ring over radio, OpenRGB output to GPU and RAM, CPU and GPU temperature sensors.
- Software only: everything else, through the self-tests (80 groups) and window renders. Fan speed control, pump speed, LCD output, other fan families and V2 controllers need more hardware reports.

## Self-tests and renders

```powershell
dotnet windows/bin/Release/net10.0-windows/WirelessCanvasRGB.dll --self-test
```

Tests never touch hardware (`MainForm.HardwareOutput` is false outside a normal run) and write samples to `artifacts/`. Render modes such as `--render-preview out.png profile.json tabIndex` draw the window to a PNG; put `--language ru|en|zh-Hans|es|pt` first to pick the language.

## Sources

- [lian-li-linux v1.2.2](https://github.com/sgtaziz/lian-li-linux/tree/v1.2.2)
- [Microsoft: WinUSB API](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/using-winusb-api-to-communicate-with-a-usb-device)
- [tinyuz](https://github.com/sisong/tinyuz), [HDiffPatch](https://github.com/sisong/HDiffPatch)
- [OpenRGB SDK protocol](https://gitlab.com/CalcProgrammer1/OpenRGB/-/wikis/OpenRGB-SDK-Documentation)
