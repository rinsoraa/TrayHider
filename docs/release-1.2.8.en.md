# TrayHider 1.2.8 — Release Notes

> This release note summarises **everything changed in 1.2.8 compared with 1.1.0**;
> the 1.2.x intermediate versions are not listed one by one.
> Full documentation: [README.en.md](../README.en.md) · [README.md](../README.md)

---

## 1. Tray-icon hiding reworked (core change)

- **The app's icon registration is no longer deleted.** In 1.1.0 hiding was destructive: restoring meant
  fabricating a registration, which left icons visible but with dead left click, context menu and double click (issue #1).
- Hiding now toggles the shell's **icon visibility state bit** (Shell_NotifyIcon + NIF_STATE/NIS_HIDDEN):
  the icon disappears from the notification area while the registration, callback and context menu still
  belong to the original app. Restoring merely clears the bit.
- As a result, **apps that ignore the "TaskbarCreated" message — e.g. Huorong and WitchDrawer — restore correctly**.
- If an icon cannot be hidden this way, TrayHider says so honestly instead of falling back to deleting the registration.
- Hidden state is persisted and survives a restart of TrayHider.
- Fixed a 1.1.0 side effect that modified the target app's tooltip while probing icons.

## 2. Hide windows to the tray (new)

- Tuck any window into the tray; restore with a single or double click, keeping position, size and maximized state.
- Optional: hide that program's own tray icon together with the window and restore both together.
- Optionally restore all hidden windows when TrayHider exits (can be turned off).
- Independent from the tray-icon features — either can be used alone.

## 3. Global hotkeys (1 binding → 5, fully customisable)

- Five actions: show main window, toggle current window, hide all windows, restore all windows, toggle all tray icons.
- Edit each one, clear a single one with × (0/0 sentinel survives restarts), or reset all with one click.
- "Add Win" capture: you do not have to press Win — tick the box and press Ctrl+F9 to get Ctrl+Win+F9.
  Combinations reserved by Windows are reported explicitly.
- Registration is transactional: two rows may be swapped; an occupied default combination is reported by name
  without corrupting the other bindings.
- Fixed 1.1.0 issues where a changed hotkey needed a restart and status-bar text did not refresh.

## 4. Single instance and elevation

- Launching the exe twice no longer just shows "already running": it **brings up the existing main window**
  (named event + window message, two independent channels; an older instance still falls back to the dialog).
- "Restart as administrator": the new instance waits for the old one with --wait-instance, so two instances
  never fight over the recorded state.

## 5. Non-resident mode (new — hide icons, then exit)

- At logon TrayHider applies the hidden state once and then **exits**, with zero idle memory. It requires
  "Start with Windows"; turning the option on asks first and enables autostart together.
- Sub-option "Periodic polling":
  - Off (default): after applying, TrayHider waits until every tracked icon is hidden and then exits silently;
    no scheduled task is created.
  - On: additionally creates a scheduled task that re-applies every **10 minutes** as a fallback for apps
    started later.
- Every run writes %AppData%\TrayHider\runonce-stats.json with the applied count, pending count, elapsed time
  and both process lists.
- Limitation: this mode **does not hide windows** (hidden windows need a resident process).

## 6. Multi-monitor and the tray menu

- The tray context menu is now a native TrackPopupMenuEx anchored to the **physical pixel that was clicked**,
  fixing menus that appeared on the wrong monitor or offset when the main window sat on a secondary display
  (Per-Monitor V2).
- The cursor is never moved; out-of-bounds menus are pulled back on screen by the system; negative-coordinate
  monitors are supported.

## 7. Bilingual UI (new)

- Simplified Chinese + English, detected from the system on first run and **switchable at runtime** (no restart).
- All window text, dialogs, hotkey descriptions and status messages are localised; missing keys fall back to the
  key name instead of crashing.
- The release check verifies that the Chinese and English key sets match, so one language cannot lag behind.
- The footer version is read from the assembly, so it can no longer disagree with the actual build.

## 8. Performance and memory

- Tray scanning no longer enumerates every process and reads its module path; it queries on demand with a short TTL cache.
- Icon extraction uses a weak-reference cache (32 px), cutting config and memory usage; GDI/HICON leaks fixed.
- When the main window is tucked away, icon caches are released and the working set is trimmed
  (measured: private bytes roughly −4.5 MB).
- Config writes are debounced and atomic, keeping a .bak of the previous save.

## 9. Stability fixes

- Fixed a startup binding exception that terminated the process (0xE0434352).
- Fixed cases where hiding icons/windows could leave a window invisible and unreachable after a crash or exit.
- Fixed the race between the "second instance" dialog and "restart as administrator".
- Config files can no longer be corrupted by concurrent writes; automated tests never touch user configuration.

## 10. Verification

- Two reusable regression suites ship with the source: config/hotkeys (18 checks) and the tray-hiding mechanism
  (25 checks), both under tests/.
- UI: bilingual real-render verification (no Chinese leftovers, no horizontal overflow, hotkey rows not overlapping).
- Non-resident mode verified end to end: hiding takes effect, the process exits, the scheduled task is created and
  removed, and statistics are written.
- A startup smoke test is part of the release checklist (compiling is not the same as starting).

---

## Requirements

- **Windows 11 22H2 / 23H2 / 24H2 / 25H2, x64** (primary validation target)
- Windows 10 22H2 (19045) x64: runs, but not fully validated
- Portable build bundles .NET 8; the Lite build requires the .NET 8 Desktop Runtime
- No administrator rights required by default

## Upgrading

- From 1.1.0: **restart each app once whose icon was hidden by the old version**, then hide/show it with the new
  build (the old mechanism deleted registrations; the new one never fabricates callbacks).
- Configuration is migrated automatically; config.json.bak holds the previous save if you need to roll back.
