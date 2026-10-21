# Contributing

Thanks for helping. The most useful contribution right now is **testing on hardware other than SL Wireless 120 / SL Wireless 120 LCD**.

## Compatibility reports

Open an issue with the *Compatibility report* template and include:

- exact fan models and how many fans are in each group;
- controller (L-Wireless V1 `0416:8040/8041` or V2 `1A86:E304/E305`) and firmware versions shown in L-Connect;
- app version and Windows version;
- what worked and what did not (lighting, LED order, effects, speed, pump, LCD);
- `diagnostics.json` from `diagnose.cmd` (read-only, it sends nothing that changes your devices). It contains your device MAC addresses: remove them if you do not want to publish them.

Short videos of the LED order help a lot when a layout looks wrong.

## Bugs

Use the *Bug report* template. Attach `%LOCALAPPDATA%\WirelessCanvasRGB\application.log` if you can, and check it for anything you prefer not to share first.

## Code changes

1. Build with `./scripts/build.ps1 -SkipNative` (see README). All self-tests must pass.
2. Keep the safety rules: no pairing/unpairing, firmware, driver or service changes; nothing is sent to unknown or mixed fan types; tests never touch hardware.
3. UI strings are written in English inside `L10n.T(...)` / `L10n.F(...)`. Add every new string to `windows/Translations.json` with Russian, Simplified Chinese, Spanish and Portuguese translations, and update the catalog count in the localization self-test.
4. Describe in the pull request what you tested on hardware and what only in self-tests.

By contributing you agree that your contribution is licensed under the MIT License of this project.
