# Trinketed Desktop

Companion desktop app for the [Trinketed](https://github.com/Trinketed/addon) arena addon suite for WoW TBC Anniversary.

## Features

- **Auto-detect** WoW AddOns folder (Anniversary, Retail, Classic Era, Classic)
- **One-click install/update** of the Trinketed addon suite from GitHub releases
- **System tray** with update notifications every 30 minutes
- **Self-updating** — automatically detects and applies new versions of itself
- **Start with Windows** toggle

## Download

Grab the latest `TrinketedDesktop.exe` from [Releases](https://github.com/Trinketed/desktop/releases).

## Building

Requires .NET 8 SDK.

```
dotnet publish -c Release -r win-x64 --self-contained -o ./publish
```

## License

MIT
