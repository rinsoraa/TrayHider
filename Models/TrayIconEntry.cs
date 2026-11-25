using System.IO;

namespace TrayHider.Interop;

/// <summary>
/// 一个托盘图标的隐藏方式。持久化在 <see cref="TrayIconEntry.HideMode"/> 里，
/// 决定"恢复"时该走哪条路径：
///   * <see cref="State"/>  —— NIM_MODIFY + NIF_STATE/NIS_HIDDEN 的非破坏性隐藏：
///                            注册与程序自己的回调都还在，恢复只要清除状态位即可，
///                            右键菜单/双击完全不受影响（v1.2.2 起的首选方式）。
///   * <see cref="Delete"/> —— 旧版的 NIM_DELETE 破坏性隐藏：注册被 Shell 删除，
///                            恢复必须让程序自己重新注册（TaskbarCreated）。
///                            旧配置里没有该字段 → 反序列化后为 0，正好对应 Delete。
/// </summary>
public enum TrayHideMode
{
    /// <summary>破坏性隐藏（NIM_DELETE）。旧版本留下的记录都是这一种。</summary>
    Delete = 0,

    /// <summary>非破坏性隐藏（NIS_HIDDEN），保留注册与回调。</summary>
    State = 1
}

/// <summary>托盘图标条目（来自注册表的原始数据 / 隐藏时的备份）。</summary>
public sealed class TrayIconEntry
{
    public string SubKeyName { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public uint? Uid { get; set; }
    public string ToolTip { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string IconGuid { get; set; } = string.Empty;
    public byte[]? IconSnapshot { get; set; }

    /// <summary>注册图标所用窗口的类名（隐藏时记录，供恢复时按类名重新定位窗口）。</summary>
    public string WindowClassName { get; set; } = string.Empty;

    /// <summary>
    /// 隐藏这个图标时用的方式。旧配置没有该字段 → 默认 <see cref="TrayHideMode.Delete"/>，
    /// 因此升级后仍能正确恢复历史隐藏项。
    /// </summary>
    public TrayHideMode HideMode { get; set; } = TrayHideMode.Delete;

    /// <summary>
    /// 用户已经点过"显示"，但该程序不响应托盘重建（只能等它自己重启）。
    ///
    /// 持久化这个意图的意义：重启本软件后仍要记得"别再把它藏回去"，
    /// 否则守护逻辑会在程序重启后又一次把图标藏掉，用户永远等不到它回来。
    /// 程序重新注册图标后，<see cref="MainViewModel"/> 会自动解除这条记录。
    /// </summary>
    public bool RestorePending { get; set; }

    /// <summary>null 表示未设置（跟随系统默认）。仅用于展示图标所在面板。</summary>
    public bool? IsPromoted { get; set; }

    /// <summary>显示名：优先提示文本，其次文件名。</summary>
    public string DisplayName
    {
        get
        {
            var tip = ToolTip.Trim();
            if (!string.IsNullOrEmpty(tip))
            {
                var line = tip.Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrWhiteSpace(line)) return line.Trim();
            }

            try
            {
                return Path.GetFileNameWithoutExtension(ExecutablePath);
            }
            catch
            {
                return ExecutablePath;
            }
        }
    }
}
