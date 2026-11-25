# TrayHider

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/Windows-10%2F11%20x64-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)

> **中文文档**: see [README.zh-CN.md](README.zh-CN.md)

Hide notification-area (system tray) icons and running windows into a single tray menu.
Hiding **never deletes** the icon registration owned by the target app, so after restoring it,
left click, right-click menu and double-click behave exactly like the app's own icon.

---

## Supported systems

| Item | Details |
| --- | --- |
| OS | **Windows 11 (22H2 / 23H2 / 24H2 / 25H2) x64** — primary development and test target |
| Windows 10 | 19045 (22H2) x64: **runs, but not fully validated**. Icon enumeration relies on the Windows 11 `NotifyIconSettings` store, so some features may be limited |
| Architecture | x64 only (no ARM64 build yet) |
| Runtime | Portable build bundles .NET 8; Lite build requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Privileges | **No administrator rights required**; no service, no injection, no system file changes |
| Multi-monitor / high DPI | Supported; Per-Monitor V2, works on secondary displays with different scaling |
| Languages | Simplified Chinese / English; auto-detected on first run, switchable in Settings |

> How it works: TrayHider calls `Shell_NotifyIcon` with `NIM_MODIFY` + `NIF_STATE`/`NIS_HIDDEN` to change the icon's
> **visibility state** only. The registration, callback and context menu stay owned by the original app — that is why
> a restored icon is still clickable. Windows 10 implements the notification area differently; PRs are welcome.

---

## Features

- **Hide / restore tray icons** — click to hide, click again to restore; hide-all / restore-all and per-program batches
- **Hide windows to tray** — tuck any window into the tray, restore with one click, window placement and maximized state preserved
- **Global hotkeys** — show main window, toggle current window, hide all windows, restore all windows, toggle all tray icons;
  each one editable, clearable (`×`), with an "Add Win" modifier helper and one-click reset to defaults
- **Non-resident mode (v1.2.3)** — apply the hidden state once at logon and exit; a scheduled task re-applies it periodically,
  so idle memory usage is zero
- **Idle memory trimming (v1.2.3)** — releases icon caches and trims the working set once the main window is tucked away
- **Bilingual UI (v1.2.3)** — Chinese / English, auto-detected from the system and switchable at runtime
- Start with Windows, minimize to tray on close, restore windows on exit
- Follows the system light/dark theme

---

## Known limitations

1. **Timing in non-resident mode** — apps started between two scheduled runs (default every 3 minutes) show their icon until
   the next run; apps that re-register their icon (e.g. on `TaskbarCreated`) also reset the state.
2. **Hidden windows require a resident process**, so non-resident mode does not hide windows.
3. **Icons hidden by v1.2.1 or earlier** (the old build used destructive registration) cannot be restored by state alone —
   restart that app once. Current versions never use the destructive path.
4. A few apps (some security suites) handle the state bit themselves; if behaviour looks wrong after hiding, restart the app.
5. Edge cases — auto-hidden taskbar, exclusive fullscreen, elevated apps — may differ; please include environment details in an issue.

---

## Download and usage

1. Download `TrayHider-<version>-Portable-win-x64.zip` (bundled runtime) or `-Lite-win-x64.zip` (.NET 8 Desktop Runtime required)
2. Extract anywhere and run `TrayHider.exe`
3. Switch between the "Tray icons" and "Windows" sections on the left; open Settings on the right
4. Settings live in `%AppData%\TrayHider\config.json` (a `.bak` copy of the previous save is kept next to it)

**Non-resident mode**: Settings → enable "Exit after hiding icons" — TrayHider registers a scheduled task named
`TrayHider-ApplyHiddenState` (runs `--apply-once` every N minutes) and rewrites its autostart command to
"hide once and exit". Disabling the option removes the task.

Command-line arguments:

| Argument | Purpose |
| --- | --- |
| (none) | Normal resident start |
| `--minimized` | Start minimized to tray (used by autostart) |
| `--apply-once` | Apply the recorded hidden state once and exit (used by the scheduled task) |
| `--wait-instance` | Wait for the running instance to exit, then take over (used by "restart as administrator") |

---

## Build from source

```powershell
git clone https://github.com/rinsoraa/TrayHider.git
cd TrayHider
dotnet build TrayHider.csproj -c Release
dotnet publish TrayHider.csproj -c Release -r win-x64 -p:PublishProfile=portable-win-x64
dotnet publish TrayHider.csproj -c Release -r win-x64 -p:PublishProfile=lite-win-x64
```

Always run before releasing (compiling is not the same as starting):

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1 -ExePath <published exe> -Seconds 20
dotnet run -c Release --project tests/TrayHider.Checks          # config / hotkey regressions
dotnet run -c Release --project tests/TrayHider.TrayModeChecks  # tray hiding mechanism (needs an interactive desktop)
```

---

## Contributing

**Pull requests and issues are welcome**, especially:

- 🖥 **New OS / architecture support** — any Windows 10 edition, Windows Server, ARM64, Insider builds
- 🐞 **Bug reports** — include Windows version, monitor layout and scaling, app version, reproduction steps;
  for tray issues please name the app that owns the icon
- 🌍 **Translations** — Chinese and English today; more languages live in `Services/Localization/Strings.*.json`
- ⚡ **Performance and memory** — reproducible measurements beat guesses

Before opening a PR: build with 0 warnings, both regression suites pass, and every new string ships in Chinese **and** English.

---

## License

[MIT License](LICENSE)
