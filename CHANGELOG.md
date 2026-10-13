# Changelog

## 1.0.0

First release.

- Controls brightness, contrast, sharpness, red/green/blue gain, color tone, volume and input over DDC/CI.
- Reaches every monitor inside NVIDIA Surround through NVAPI; uses Windows DDC/CI for other adapters, and both together in mixed setups.
- Only sends values each monitor declares in its capabilities string, with a profile for the Samsung Odyssey G5 (S27/S32CG55) color tone.
- Presets with <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>1</kbd>–<kbd>9</kbd>, brightness hotkeys, tray icon, start with Windows.
- Parallel I/O: one lane per monitor, coalesced slider drags, final values confirmed by read-back.
- `/diagnose` writes a read-only report for troubleshooting.
