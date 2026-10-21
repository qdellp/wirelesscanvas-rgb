# Third-party notices

WirelessCanvas RGB (MIT, Copyright (c) 2026 Sterax) includes or links the components below. Each keeps its own license. Full license texts are in the [LICENSES](LICENSES) folder and are shipped in every release archive.

## Source code this project is based on

| Component | Version | License | Source |
|---|---|---|---|
| lian-li-linux | v1.2.2, commit `73653a10c41318568df79e821f82a856cfcfadeb` | MIT, Copyright (c) 2026 sgtaziz | https://github.com/sgtaziz/lian-li-linux/tree/v1.2.2 — [LICENSES/lian-li-linux-MIT.txt](LICENSES/lian-li-linux-MIT.txt) |

The protocol logic was ported from Rust to C#. The upstream snapshot is a git submodule in `upstream/` and is not modified.

## Native libraries in the release (`tinyuz.dll`, `tuz-dec.dll`)

Built from unmodified sources with Zig (`scripts/build-native.ps1`).

| Component | Version | License | Source |
|---|---|---|---|
| tinyuz | commit `1d74ffa4d453796df352df470733f45dfa099bb1` | MIT, Copyright (c) sisong | https://github.com/sisong/tinyuz — [LICENSES/tinyuz-MIT.txt](LICENSES/tinyuz-MIT.txt) |
| HDiffPatch (libdivsufsort part) | commit `3b9dca715ca492873bf2c49e22e5d5b7d2a78620` | MIT | https://github.com/sisong/HDiffPatch — [LICENSES/HDiffPatch-MIT.txt](LICENSES/HDiffPatch-MIT.txt), [LICENSES/divsufsort-MIT.txt](LICENSES/divsufsort-MIT.txt) |
| Zig runtime / LLVM libc++, libc++abi, libunwind, MinGW-w64 runtime | Zig 0.15.2 | MIT; Apache-2.0 WITH LLVM-exception; ZPL-2.1 and notices in the file (MinGW-w64) | [LICENSES/Zig-MIT.txt](LICENSES/Zig-MIT.txt), [LICENSES/LLVM-libcxx.txt](LICENSES/LLVM-libcxx.txt), [LICENSES/LLVM-libcxxabi.txt](LICENSES/LLVM-libcxxabi.txt), [LICENSES/LLVM-libunwind.txt](LICENSES/LLVM-libunwind.txt), [LICENSES/mingw-runtime.txt](LICENSES/mingw-runtime.txt) |

## NuGet packages in the release

Used unmodified. The Mozilla Public License 2.0 applies to the MPL packages only, not to WirelessCanvas RGB. Their source code for the exact versions is available at the links below. License text: https://mozilla.org/MPL/2.0/

| Package | Version | License | Source code of this version |
|---|---|---|---|
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701 |
| DiskInfoToolkit (dependency of LibreHardwareMonitorLib) | 1.1.2 | MPL-2.0 | https://github.com/Blacktempel/DiskInfoToolkit/tree/25319eae5781e75bcf141e844ceab2afe94d40ea |
| RAMSPDToolkit-NDD (dependency of LibreHardwareMonitorLib) | 1.4.2 | MPL-2.0 | https://github.com/Blacktempel/RAMSPDToolkit/tree/3b47b960e0830fef344624ad5e389675d5f0a1ce |
| BlackSharp.Core (dependency of the toolkits above) | 1.0.7 | MPL-2.0 | https://github.com/Blacktempel/BlackSharp/tree/c70b735c6cec123ee8a046ac4a0bc6c606f52cf0 |
| HidSharp (dependency of LibreHardwareMonitorLib) | 2.6.4 | Apache-2.0, Copyright 2010-2025 James F. Bellinger | https://software.seekye.com/hidsharp — [LICENSES/HidSharp-LICENSE.txt](LICENSES/HidSharp-LICENSE.txt) |
| System.Management, System.IO.Ports, System.IO.FileSystem.AccessControl | 10.0.2, 10.0.3, 5.0.0 | MIT, .NET Foundation | https://github.com/dotnet/runtime |

`Mono.Posix.NETStandard` (a Linux-only dependency of LibreHardwareMonitorLib) is excluded from the Windows build and is not shipped.

## .NET runtime

Releases are self-contained and include the .NET 10 runtime and Windows Desktop runtime (MIT, .NET Foundation and Contributors), with their third-party notices: [LICENSES/dotnet-runtime.txt](LICENSES/dotnet-runtime.txt), [LICENSES/dotnet-windowsdesktop.txt](LICENSES/dotnet-windowsdesktop.txt), [LICENSES/dotnet-third-party-notices.txt](LICENSES/dotnet-third-party-notices.txt).

## Not included

- No files, images, firmware or binaries from LIAN LI or L-Connect are included. OpenRGB is not shipped: the app uses an OpenRGB installed by the user, or the copy installed with L-Connect 3.
- Drivers (WinUSB, PawnIO, NVIDIA NVML) are provided by the system or by the user and are not shipped.
- The NVIDIA driver library `nvml.dll` is loaded from the installed driver.
