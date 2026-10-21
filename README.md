# WirelessCanvas RGB

<img width="1258" height="847" alt="image" src="https://github.com/user-attachments/assets/651bc6ba-d1b8-4c58-8123-1fc14d970664" />

**Your whole case. One animated canvas.**

WirelessCanvas RGB is an open-source Windows app for LIAN LI **wireless** fans. Place your fans on a canvas that matches your case and run one animation across all of them at once: gradients and shader effects such as radar, fire, waves, plasma, lava and starfall. The same animation can also drive the pump ring, OpenRGB devices (RAM, GPU, keyboard) and the LCD screens.

> **Unofficial project.** It is not affiliated with, endorsed by or supported by LIAN LI. LIAN LI, L-Connect, UNI FAN and SL Wireless are trademarks of their owners and are used here only to describe compatible hardware.

Русская версия: [README.ru.md](README.ru.md)

> **Alpha build for early testing.** Expect bugs, rough edges and changes between versions. Any feedback is welcome and will be considered: bug reports, compatibility reports, ideas and criticism. Open an [issue](../../issues/new/choose).

## Status: alpha

| | |
|---|---|
| Tested fans | **UNI FAN SL Wireless 120 (RGB)** and **UNI FAN SL Wireless 120 LCD** on an L-Wireless controller |
| Fan types | **Wireless fans only.** Wired fans and wired controllers are not supported |
| Other wireless families | TL, SL-INF, CL, SL V4 and P28 Wireless are recognized from upstream data, but **not tested** on hardware |
| OS | Windows 11 x64 (tested). Windows 10 x64 should work but is untested |
| Version | 0.13.0 alpha |

Pump, OpenRGB, keyboard, cooling and LCD features worked on the author's system, but they are experimental. Check your temperatures after you change anything related to cooling.

## L-Connect 3 is still needed for pairing

WirelessCanvas RGB **cannot pair fans to the controller yet.** Before you use it:

1. Install L-Connect 3 and pair (bind) your wireless fan groups to your L-Wireless controller there.
2. Check that the fans work in L-Connect.
3. Fully quit L-Connect 3: exit it from the tray and make sure `L-Connect-Service` is not running. Only one program can drive the controller at a time, and WirelessCanvas RGB refuses to connect while L-Connect is running.

Once your groups are paired, you can control lighting without L-Connect running. Keep L-Connect installed: you need it again to pair new fans, update firmware or go back to the official software.

## What it does

- **Case canvas.** Drag your fans, pump and OpenRGB devices into place, set rotation and viewing angle. Every LED gets its color from one shared function of position and time, so an animation flows from one group into the next.
- **Gradients and 10 shader effects.** Waves, fire, starfall, twinkle, aurora, plasma, rain, ripple, radar and lava, each with its own palette and parameters. 52 gradient presets.
- **Loops on the fans.** Animations are pre-rendered and uploaded into fan memory as a seamless loop, so playback is smooth and does not depend on radio bandwidth.
- **Per-element lighting.** Right-click any element to make it follow the mapping, use a fixed color, or turn off.
- **OpenRGB devices.** RAM, GPU and keyboards are streamed through the OpenRGB SDK in phase with the fans.
- **LCD screens.** Pictures, GIFs, temperature dashboards or the live mapping on the pump and SL LCD fans.
- **Cooling.** Fan curves based on CPU, GPU or coolant temperature, and a fixed pump speed.
- **Languages.** English, Russian, Simplified Chinese, Spanish and Portuguese.

## Getting started

1. Download `WirelessCanvasRGB-<version>-win-x64.zip` from [Releases](../../releases) and check its SHA-256 against the release notes.
2. Unzip it to a permanent folder, for example `D:\Apps\WirelessCanvasRGB`. No installation and no .NET runtime are needed.
3. Pair your fans in L-Connect 3, then fully quit L-Connect (see above).
4. Run `WirelessCanvasRGB.exe`. It asks for administrator rights once: the CPU temperature sensor and OpenRGB RAM access need them. You can decline, and lighting still works.
5. Click **Connect USB**. The app lists the paired wireless groups it found.
6. Arrange the fans on the canvas, pick a gradient or an effect and click **Apply gradient**. For a first test, use 20% brightness.

### Going back to L-Connect

Close the WirelessCanvas RGB window, or choose **Exit** in its tray menu. This stops speed commands, so the fans return to hardware speed, and releases USB. Then start L-Connect 3 again. The last uploaded animation may keep playing until L-Connect or a power cycle replaces it.

## Requirements and limits

- The L-Wireless receiver must use its standard **WinUSB** driver (L-Connect installs it). The app does not install or change drivers.
- Fans must already be paired to your controller. The app never pairs or unpairs groups, and never flashes firmware.
- Unknown or mixed fan types are left alone: the app sends them no lighting or speed commands.
- Live streaming over radio is limited to about 5 frames per second for all groups together. Smooth animation comes from uploaded loops, not from live frames.
- A case has a few hundred LEDs, not a screen: fine details such as thin rain look coarser on fans than on the canvas.
- OpenRGB features need OpenRGB with its SDK server enabled. The OpenRGB that ships with L-Connect 3 also works.
- CPU temperature needs administrator rights and the PawnIO driver. GPU temperature is read through the NVIDIA driver (NVML).

## Privacy

The app has no telemetry and makes no internet connections. It only talks to USB devices and to OpenRGB on `127.0.0.1`. Settings, profiles and logs stay in `%LOCALAPPDATA%\WirelessCanvasRGB`.

## Safety

This software controls fan and pump speeds. It is provided **as is, without warranty** (see [LICENSE](LICENSE)). Watch your temperatures after you change cooling settings, and do not test under heavy load. If anything looks wrong, close the app: the fans return to hardware speed within seconds.

## Building from source

Requirements: Windows x64, .NET SDK 10, Git.

```powershell
git clone --recurse-submodules https://github.com/qdellp/wirelesscanvas-rgb.git
cd wirelesscanvas-rgb
./scripts/bootstrap.ps1          # checks the upstream snapshot and downloads Zig for the native DLLs
./scripts/build.ps1              # builds, runs the self-tests, publishes dist/WirelessCanvasRGB
```

`./scripts/build.ps1 -SkipNative` reuses the prebuilt `windows/native/*.dll`. To run only the self-tests, with no hardware access: `dotnet windows/bin/Release/net10.0-windows/WirelessCanvasRGB.dll --self-test`.

See [TECHNICAL.md](TECHNICAL.md) for protocol and architecture notes, and [CONTRIBUTING.md](CONTRIBUTING.md) for how to report hardware and send changes.

## Reporting compatibility

If you try it, please open a [compatibility report](../../issues/new/choose) with your exact fan model, controller, firmware and what worked. Reports for untested wireless families are especially useful.

## Credits and license

- Protocol work is based on [lian-li-linux](https://github.com/sgtaziz/lian-li-linux) v1.2.2 by **sgtaziz** (MIT).
- Compression: [tinyuz](https://github.com/sisong/tinyuz) and [HDiffPatch](https://github.com/sisong/HDiffPatch) by sisong (MIT).
- Sensors: [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) and its dependencies (MPL-2.0), HidSharp (Apache-2.0).

WirelessCanvas RGB is released under the [MIT License](LICENSE). Copyright (c) 2026 Sterax. Third-party components keep their own licenses: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the [LICENSES](LICENSES) folder.
