# Contributing

Thanks for helping. Two things help most:

1. **Monitor reports.** Run `MonitorControl.exe /diagnose` and open a
   [monitor report](../../issues/new?template=monitor_report.yml) with the result.
   It only reads from your monitors.
2. **Profiles.** Some monitors list supported values wrongly in their capabilities
   string. Fixes go in `Profiles` in [`src/Ddc.cs`](src/Ddc.cs). Please include how you
   confirmed each value against the monitor's own menu.

## Building

```bat
build.cmd
```

This uses the C# 5 compiler that ships with Windows, so stick to C# 5 syntax: no
`$"..."` strings, `?.`, `nameof` or expression-bodied members.

## Ground rules

- Never send a value a monitor hasn't declared, unless a profile documents why.
- Keep monitor I/O off the UI thread. Use the monitor's `Lane`.
- Keep the app portable: no installers, services or admin rights.
