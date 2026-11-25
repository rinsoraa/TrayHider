namespace TrayHider.Services;

/// <summary>维护实际注册成功的组合。批量修改分两阶段，允许交换两行的键，注册失败则回滚。</summary>
internal sealed class GlobalHotkeyManager(Func<int, HotkeyBinding, bool> register, Func<int, bool> unregister)
{
    private const int IdBase = 0x5452;
    private readonly Dictionary<int, HotkeyBinding> _active = new();
    private static int IdOf(string action) => IdBase + Array.IndexOf(HotkeyActions.All, action);
    private static bool Equal(HotkeyBinding a, HotkeyBinding b) => a.Modifiers == b.Modifiers && a.VirtualKey == b.VirtualKey;

    public bool TryMatch(int id, int modifiers, int key, out string action)
    {
        action = string.Empty;
        if (!_active.TryGetValue(id, out var binding) || binding.Modifiers != modifiers || binding.VirtualKey != key) return false;
        action = HotkeyActions.All[id - IdBase];
        return true;
    }

    public List<string> RegisterConfigured(AppConfig config)
    {
        var failed = new List<string>();
        foreach (var action in HotkeyActions.All)
        {
            var binding = config.GetHotkey(action);
            if (binding.IsValid && !Add(IdOf(action), binding)) failed.Add(HotkeyCatalog.Title(action));
        }
        return failed;
    }

    public void UnregisterAll()
    {
        foreach (int id in _active.Keys.ToArray()) Remove(id);
    }

    private bool Add(int id, HotkeyBinding binding)
    {
        if (!register(id, binding)) return false;
        _active.Add(id, binding);
        return true;
    }

    private bool Remove(int id)
    {
        if (!unregister(id)) return false;
        _active.Remove(id);
        return true;
    }

    /// <summary>成功后才提交配置；冲突不会覆盖旧配置，也不会把用户主动清空的项目重新注册。</summary>
    public bool TryApply(AppConfig config, IReadOnlyDictionary<string, HotkeyBinding> changes, out string error,
        List<string>? unavailableOnReset = null)
    {
        error = string.Empty;
        foreach (var (action, candidate) in changes)
        {
            if (!HotkeyActions.All.Contains(action) || !(candidate.IsValid || candidate.IsUnassigned))
            {
                error = "无效的动作或组合键。";
                return false;
            }
            if (candidate.IsUnassigned) continue;
            foreach (var other in HotkeyActions.All)
            {
                if (other == action || !Equal(candidate, changes.GetValueOrDefault(other) ?? config.GetHotkey(other))) continue;
                error = $"{candidate} 已用于「{HotkeyCatalog.Title(other)}」。";
                return false;
            }
        }

        var affected = changes.Where(pair => _active.TryGetValue(IdOf(pair.Key), out var old)
            ? !Equal(old, pair.Value) : pair.Value.IsValid).ToArray();
        var removed = new Dictionary<int, HotkeyBinding>();
        var added = new List<int>();
        foreach (var (action, _) in affected)
        {
            var id = IdOf(action);
            if (!_active.TryGetValue(id, out var old)) continue;
            if (!Remove(id)) { error = $"无法注销「{HotkeyCatalog.Title(action)}」的旧组合。"; break; }
            removed.Add(id, old);
        }
        if (error.Length == 0)
        {
            foreach (var (action, candidate) in affected)
            {
                if (candidate.IsUnassigned) continue;
                var id = IdOf(action);
                if (!Add(id, candidate))
                {
                    // 显式重置时应保存全部默认值：一个默认键冲突不应阻止其余四项恢复。
                    // 普通录入没有这条特例，仍回滚到之前可用的组合。
                    if (unavailableOnReset != null)
                    {
                        unavailableOnReset.Add($"{HotkeyCatalog.Title(action)}（{candidate}）");
                        continue;
                    }
                    error = $"「{HotkeyCatalog.Title(action)}」的 {candidate} 已被系统保留或被其他程序占用。";
                    break;
                }
                added.Add(id);
            }
        }
        if (error.Length != 0)
        {
            var rollbackFailed = false;
            foreach (int id in added) rollbackFailed |= !Remove(id);
            foreach (var (id, old) in removed) rollbackFailed |= _active.ContainsKey(id) || !Add(id, old);
            if (rollbackFailed) error += " 部分原组合未能重新注册，请在设置中重新指定。";
            return false;
        }
        foreach (var (action, binding) in changes) config.Hotkeys[action] = binding;
        return true;
    }
}
