using TrayHider.Services;

// 不读写用户配置，不注册系统热键。用可控的注册表替身复现占用、交换、排队消息及回滚。
var n = 0;
void Check(bool ok, string name)
{
    if (!ok) throw new Exception(name);
    Console.WriteLine($"PASS {++n}: {name}");
}
var config = AppConfig.FromJson("""{"HotkeyModifiers":6,"HotkeyVirtualKey":81,"RunAtStartup":true,"HideSelfTrayIcon":true,"RebuildUnsupported":["LegacyProbe.exe"]}""");
const string show = HotkeyActions.ShowMain, hide = HotkeyActions.ToggleHideWindow;
Check(config.GetHotkey(show).Modifiers == 6 && config.GetHotkey(show).VirtualKey == 81, "custom legacy hotkey is migrated");
Check(config.RebuildUnsupported.SequenceEqual(new[] { "LegacyProbe", "WitchDrawer" }), "upgrade keeps learned programs and adds WitchDrawer");
config.RebuildUnsupported.Remove("WitchDrawer");
Check(!AppConfig.FromJson(config.ToJson()).RebuildUnsupported.Contains("WitchDrawer"), "migration does not re-add an unlearned program");
var slots = new Dictionary<int, (int mods, int key)>();
(int, int)? occupied = null;
var manager = new GlobalHotkeyManager((id, b) => {
    var combo = (b.Modifiers, b.VirtualKey);
    return occupied != combo && !slots.ContainsValue(combo) && slots.TryAdd(id, combo);
}, slots.Remove);
Check(manager.RegisterConfigured(config).Count == 0 && slots.Count == 5, "register five configured hotkeys");
int showId = slots.Single(x => x.Value == (6, 81)).Key;
bool Change(string action, int mods = 0, int key = 0) => manager.TryApply(config,
    new Dictionary<string, HotkeyBinding> { [action] = new() { Modifiers = mods, VirtualKey = key } }, out _);
Check(Change(show, 8, 0x86) && manager.TryMatch(showId, 8, 0x86, out var action) && action == show, "accept Win + F23");
Check(!Change(hide, 8, 0x86) && slots.Count == 5, "reject a duplicate without changing existing registrations");
Check(Change(show) && slots.Count == 4 && !manager.TryMatch(showId, 8, 0x86, out _), "clear and ignore an already queued old hotkey");
var reloaded = AppConfig.FromJson(config.ToJson());
Check(reloaded.GetHotkey(show).IsUnassigned && reloaded.HotkeyVirtualKey == 0 && reloaded.GetHotkey(show).IsUnassigned && reloaded.GetHotkey(show).ToString() == HotkeyCatalog.UnassignedText, "clear survives serialization and legacy fields");
manager.UnregisterAll(); slots.Clear();
Check(manager.RegisterConfigured(reloaded).Count == 0 && slots.Count == 4, "startup skips disabled actions without warning");
config = reloaded;
Check(Change(hide, 9, 0x87), "set a custom hotkey after reload");
var before = config.ToJson();
var registrations = new Dictionary<int, (int, int)>(slots);
occupied = (10, 0x48);
Check(!manager.TryApply(config, HotkeyBinding.CreateDefaults(), out var error) && error.Contains(HotkeyCatalog.Title(hide)) &&
    config.ToJson() == before && registrations.Count == slots.Count && registrations.All(x => slots.GetValueOrDefault(x.Key) == x.Value), "conflicting reset rolls back config, earlier successful registrations and disabled actions");
var unavailable = new List<string>();
Check(manager.TryApply(config, HotkeyBinding.CreateDefaults(), out _, unavailable) && unavailable.Count == 1 && unavailable[0].Contains(HotkeyCatalog.Title(hide)) && slots.Count == 4 && config.GetHotkey(hide).VirtualKey == 0x48 && config.GetHotkey(show).VirtualKey == 0x54,
    "explicit reset saves all defaults, reports only occupied action and activates other four");
occupied = null;
Check(manager.TryApply(config, HotkeyBinding.CreateDefaults(), out _) && slots.Count == 5 && config.GetHotkey(show).Modifiers == 10 && config.GetHotkey(show).VirtualKey == 0x54, "reset all five defaults and activate immediately");
Check(config.RunAtStartup && config.HideSelfTrayIcon && config.RebuildUnsupported.SequenceEqual(new[] { "LegacyProbe" }), "reset leaves non-hotkey settings untouched");
var swapped = new Dictionary<string, HotkeyBinding> { [show] = config.GetHotkey(hide).Clone(), [hide] = config.GetHotkey(show).Clone() };
Check(manager.TryApply(config, swapped, out _) && slots[showId] == (10, 0x48), "transaction permits swapping occupied actions");
Check(!Change(show, 8, 0x5B) && !Change(show, 0, 0x54) && !Change(show, 2, -1), "reject modifier-only and malformed combinations");
var scratch = System.IO.Directory.CreateTempSubdirectory("TrayHider-ConfigCheck-");
try
{
    var path = System.IO.Path.Combine(scratch.FullName, "config.json");
    var stored = AppConfig.Load(path);
    stored.HideSelfTrayIcon = true;
    stored.Save();
    stored = AppConfig.Load(path);
    Check(stored.HideSelfTrayIcon, "explicit profile persists and reloads");
    var original = System.IO.File.ReadAllText(path);
    stored.HideSelfTrayIcon = false;
    stored.Save();
    Check(System.IO.File.ReadAllText(path + ".bak") == original && !AppConfig.Load(path).HideSelfTrayIcon, "atomic save preserves the previous settings file");
}
finally { scratch.Delete(true); }
Console.WriteLine($"{n}/{n} checks passed");
