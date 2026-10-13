# Changelog

## 1.0.1

- Works with more monitors: brightness, contrast, sharpness and volume show up even when a monitor leaves them out of its capabilities list.
- Picture mode is back, for monitors that declare its values.
- Removed the red/green/blue gain sliders and the Odyssey G5 *Custom* tone; color tone covers it.
- Reset color now works on the Odyssey G5, which ignores the standard reset command, and reports when any monitor ignores it.
- Screen tiles shrink to fit four or more screens.
- DDC timing adapts: slow monitors get longer pauses automatically.
- Monitors on non-NVIDIA graphics get their name and a stable identity from the registry, so names and presets stick.
- Settings move to `%APPDATA%\MonitorControl` and logs to `%LOCALAPPDATA%\MonitorControl`; nothing is written next to the exe.
- `/diagnose` can run alongside the app safely and opens its report when done.
- No entrance animation on startup.

## 1.0.0

First release.

- Controls brightness, contrast, sharpness, red/green/blue gain, color tone, volume and input over DDC/CI.
- Reaches every monitor inside NVIDIA Surround through NVAPI; uses Windows DDC/CI for other adapters, and both together in mixed setups.
- Only sends values each monitor declares in its capabilities string, with a profile for the Samsung Odyssey G5 (S27/S32CG55) color tone.
- Presets with <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>1</kbd>–<kbd>9</kbd>, brightness hotkeys, tray icon, start with Windows.
- Parallel I/O: one lane per monitor, coalesced slider drags, final values confirmed by read-back.
- `/diagnose` writes a read-only report for troubleshooting.
