# Changelog

## 0.13.1 alpha — services keep running

- Services no longer stop after long idle periods. Incomplete replies from the L-Wireless receiver are treated as interference and skipped (as upstream does) instead of closing USB after three in a row; fan speed commands, cooling and lighting keep running. The link closes only on a USB error or after about 2 minutes without a valid reply, and reconnection is retried without a limit (every 5 s for the first minute, then every 15 s).
- Connecting tolerates single incomplete replies (three good polls out of up to eight).
- The pump coolant temperature is no longer treated as part of the pump address, so the pump is not briefly blocked every time the temperature changes.

## 0.13.0 alpha — first public release

Alpha build for early testing: expect bugs. All feedback is welcome.

Renamed from the private "LianLi.Windows" builds to **WirelessCanvas RGB** and published as open source.

- Case canvas with a shared gradient and 10 seamless shader effects (waves, fire, starfall, twinkle, aurora, plasma, rain, ripple, radar, lava), 52 gradient presets, inner and outer LED layers.
- Effects are pre-rendered into a seamless loop and uploaded to fan memory; groups and the pump ring play in phase from the controller clock.
- Confirmation and per-group retry of RGB uploads; automatic recovery of radio slot conflicts on verified models.
- Wireless fan families from upstream (SL, TL, SL-INF, CL, SL V4, P28). Only SL Wireless 120 RGB and SL Wireless 120 LCD are verified on hardware.
- Per-element lighting: follow mapping, custom color or off.
- OpenRGB: SDK client for RAM, GPU and keyboards in phase with the fans; SDK server exposing the fan groups.
- HydroShift II pump: ring over radio, fixed pump RPM, pump and SL LCD screens with images, GIFs, temperature dashboards or the live mapping.
- Cooling: fan curves by CPU, GPU or coolant temperature.
- One UAC prompt at launch, start with Windows through Task Scheduler.
- UI in English (source language), Russian, Simplified Chinese, Spanish and Portuguese.

Upgrading from a private LianLi.Windows build: the profile and settings are copied from `%LOCALAPPDATA%\LianLi.Windows` on first launch, and an existing start-with-Windows task is replaced by the new one the first time the app runs as administrator.
