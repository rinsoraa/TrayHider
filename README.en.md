# TrayHider

![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)
![Platform](https://img.shields.io/badge/Windows-10%2F11%20x64-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![Version](https://img.shields.io/badge/version-1.2.8-brightgreen)

**English** · [简体中文](README.md) · [1.2.8 release notes](docs/release-1.2.8.en.md)

Hide notification-area (system tray) icons and running windows into a single tray menu.
Hiding **never deletes** the icon registration owned by the target app, so after restoring it,
left click, right-click menu and double-click behave exactly like the app's own icon.

---

## Supported systems

| Item | Details |
| --- | --- |
| OS | **Windows 11 22H2 / 23H2 / 24H2 / 25H2 (x64)** — primary development and validation target |
| Windows 10 | 22H2 (19045) x64: **runs, but not fully validated**; the notification area works differently, some features may be limited |
| Architecture | x64 only (no ARM64 build yet) |
| Runtime | Portable build bundles .NET 8; Lite build requires the .NET 8 Desktop Runtime |
| Privileges | **No administrator rights required**; no service, no injection, no system file changes. Use "Restart as administrator" to manage elevated apps |
| Multi-monitor / high DPI | Supported, Per-Monitor V2; on secondary displays the menu anchors to the physical pixel where you clicked |
| Languages | Simplified Chinese / English, auto-detected on first run and switchable at runtime |

---

## Features

### Tray icons
- Click to hide, click again to restore; hide-all / restore-all, per-program batches, per-selection batches
- Hidden state is **persisted** and survives restarts of TrayHider
- TrayHider's own placeholder icons never appear in the list (other copies of the same exe are excluded too)

### Windows
- Tuck any window into the tray, restore with one click/double-click, keeping position, size and maximized state
- Optional: hide that program's own tray icon together with its window
- Optionally restore all hidden windows when TrayHider exits

### Global hotkeys
- Five actions: show main window, toggle current window, hide all windows, restore all windows, toggle all tray icons
- Edit individually, clear one with × (writes a 0/0 sentinel that survives restart), or reset all at once
- "Add Win" capture: no need to press Win — tick it and press Ctrl+F9 to get Ctrl+Win+F9
- Registration is transactional: two rows can be swapped; if a default combination is already taken, that row is reported by name without breaking the others

### Non-resident mode (hide icons, then exit)
- Requires "Start with Windows": turning it on asks first, then enables autostart together
- At logon TrayHider applies the hidden state once and exits — **zero idle memory**
- Sub-option "Periodic polling":
  - **Off (default)**: after applying, TrayHider waits until every tracked icon is hidden, then exits silently; no scheduled task is created
  - **On**: additionally creates a scheduled task that re-applies every **10 minutes** as a fallback for apps started later
- Each run writes \`%AppData%\TrayHider\runonce-stats.json\`: applied count, pending count, elapsed time, and the lists of hidden / pending processes

### Other
- Launching a second instance no longer just shows "already running": it brings up the existing main window (named event + window message, two channels)
- "Restart as administrator" spawns a new instance that waits for the old one to exit (--wait-instance)
- Chinese and English UI with live switching; the footer version is read from the assembly so it cannot drift
- When the main window is tucked away, icon caches are released and the working set is trimmed
- Follows the system light/dark theme; configuration is written atomically with a config.json.bak of the previous save

---

## Known limitations

1. **Non-destructive hiding relies on the shell's icon state bit.** Some apps re-register their icon (e.g. on TaskbarCreated) and the icon reappears; TrayHider will hide it again on the next refresh/poll.
2. **Icons hidden by v1.1.0 / 1.2.1 or earlier** (the old mechanism deleted the registration) cannot be restored by state alone — restart that app once, then hide/show it with the current version. Recent versions never use the destructive path.
3. **Non-resident mode does not hide windows**: hidden windows need a resident process.
4. **The statistics wait is capped at 10 minutes**; after that TrayHider still exits, and apps not yet started are handled by the next scheduled run (if polling is on) or the next logon.
5. **Memory**: idle trimming mostly gives back working set; private bytes drop by about 4.5 MB in measurement — the WPF/native baseline is the larger part.
6. **Windows 10 and ARM64** are not fully validated; edge cases such as RDP sessions or live display-scaling changes are not exhaustively tested.

---

## Download and usage

1. Download TrayHider-1.2.8-Portable-win-x64.zip (bundled runtime) or -Lite-win-x64.zip (.NET 8 Desktop Runtime required) from Releases
2. Extract anywhere and run TrayHider.exe
3. Switch between "Tray icons" and "Windows" on the left; open Settings at the top right
4. Settings live in \`%AppData%\TrayHider\config.json\` (with a .bak of the previous save next to it)

| Command-line argument | Purpose |
| --- | --- |
| (none) | Normal resident start |
| --minimized | Start minimized to tray (used by autostart) |
| --apply-once | Apply the recorded hidden state once and exit (non-resident mode / scheduled task) |
| --wait-instance | Wait for the running instance to exit, then take over (restart as administrator) |

---

## Build from source

~~~powershell
git clone https://github.com/rinsoraa/TrayHider.git
cd TrayHider
dotnet build TrayHider.csproj -c Release
dotnet publish TrayHider.csproj -c Release -r win-x64 -p:PublishProfile=portable-win-x64
dotnet publish TrayHider.csproj -c Release -r win-x64 -p:PublishProfile=lite-win-x64
~~~

Always run before a release (compiling is not the same as starting):

~~~powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1 -ExePath <published exe> -Seconds 20
dotnet run -c Release --project tests/TrayHider.Checks          # config / hotkeys (18 checks)
dotnet run -c Release --project tests/TrayHider.TrayModeChecks  # tray hiding mechanism (25 checks, needs an interactive desktop)
~~~

---

## Contributing

**Pull requests and issues are welcome**, especially:

- 🖥 **New OS / architecture support** — any Windows 10 edition, Windows Server, ARM64, Windows Insider builds
- 🐞 **Bug reports** — include the Windows version, monitor layout and scaling, app version and reproduction steps; for tray issues please name the app that owns the icon
- 🌍 **Translations** — strings live in Services/Localization/Strings.<language>.<area>.json
- ⚡ **Performance measurements** — reproducible numbers beat guesses

Before opening a PR: 0 build warnings, both regression suites green, and every new string in Chinese *and* English.

## License

[MIT License](LICENSE)
