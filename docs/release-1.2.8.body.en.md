## TrayHider v1.2.8

### Download

- [TrayHider-1.2.8-Portable-win-x64.zip](https://github.com/rinsoraa/TrayHider/releases/download/v1.2.8/TrayHider-1.2.8-Portable-win-x64.zip) — portable build with bundled .NET 8 runtime
- [TrayHider-1.2.8-Lite-win-x64.zip](https://github.com/rinsoraa/TrayHider/releases/download/v1.2.8/TrayHider-1.2.8-Lite-win-x64.zip) — lite build, requires the .NET 8 Desktop Runtime

### What's new

- Hiding tray icons no longer deletes the app's registration; left click, context menu and double click keep working after restore
- Fixed icons of apps such as Huorong and WitchDrawer that became unclickable after hiding and restoring
- New: hide ordinary windows to the tray, optionally restore them on exit
- Global hotkeys expanded from 1 to 5 actions; edit, clear or reset each one, with an "Add Win" capture mode
- Launching the exe again now brings up the running instance's main window instead of only showing a dialog
- New "Restart as administrator"
- Fixed the tray menu appearing at the wrong position when the main window is on a secondary display
- New non-resident mode: hide icons at logon then exit, with an optional 10-minute polling fallback and runonce-stats.json statistics
- New Chinese/English UI that follows the system language and can be switched in Settings
- Icon caches are released and the working set is trimmed while idle
- Fixed TrayHider's own entries appearing in the list, hotkey changes not taking effect, and clipped UI text

### System requirements

- Windows 11 22H2 / 23H2 / 24H2 / 25H2 (x64)
- Windows 10 22H2 (19045) x64: runs, not fully validated
- Portable build needs no runtime; the lite build requires the .NET 8 Desktop Runtime
- No administrator rights required by default
