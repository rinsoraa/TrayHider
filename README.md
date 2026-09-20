# 托盘图标管家 (TrayHider)

Windows 11 桌面工具，用于把系统托盘中的图标隐藏起来 —— **只隐藏图标，不结束进程**。
被隐藏的程序继续在后台正常运行、功能完全不受影响，而它的图标既不出现在托盘可见区，
也不出现在点击"小箭头"后展开的溢出面板中。

---

## 一、技术原理（经实测确认）

### 为什么不用传统方案

网上流传的做法是找到 `Shell_TrayWnd → TrayNotifyWnd → ToolbarWindow32`，
然后用 `TB_GETBUTTON` + `ReadProcessMemory` 读出托盘按钮，再用 `TB_DELETEBUTTON` 删除。

**这个方法在 Windows 11 上已经失效。**

本机实测结果：

```
Shell_TrayWnd [0x205DC] (vis)
  Windows.UI.Core.CoreWindow                    ← XAML 宿主
  Start
  TrayDummySearchControl
  TrayNotifyWnd                                 ← 空的，没有子窗口
  ReBarWindow32
    MSTaskSwWClass / MSTaskListWClass
  Windows.UI.Composition.DesktopWindowContentBridge
```

`TrayNotifyWnd` 下已经没有任何 `ToolbarWindow32`，全系统只有一个 `Shell_TrayWnd`。
`TB_BUTTONCOUNT` 返回 -1，扫描到 **0 个图标**。

原因：Windows 11 的任务栏改用 XAML / Windows.UI.Composition 实现，
微软移除了承载托盘图标的工具栏窗口，相关消息机制一并作废。

### 实际采用的方案

Windows 11 把每个托盘图标的可见性记录在注册表：

```
HKEY_CURRENT_USER\Control Panel\NotifyIconSettings
```

该键下有若干子键，**每个子键代表一个曾在托盘中注册过的图标**：

| 值名 | 类型 | 含义 |
|---|---|---|
| `ExecutablePath` | String | 图标所属可执行文件（可能是已知文件夹 GUID 形式） |
| `UID` | DWord | 图标在其所属进程内的标识 |
| `InitialTooltip` | String | 初始提示文本（即鼠标悬停看到的名字） |
| `IconSnapshot` | Binary | 图标位图快照 |
| `Publisher` | String | 发布者（部分图标才有） |
| `IconGuid` | String | 图标 GUID（部分图标才有） |
| **`IsPromoted`** | DWord | **仅决定图标显示在哪：`1` = 任务栏托盘，`0` = 溢出面板（小箭头）** |

根键下还有一个 `UIOrderList`（二进制），保存图标排列顺序。

> 关键认知（实测确认）：`IsPromoted` **只能**在"托盘"和"溢出面板"两个位置之间切换，
> 它**无法让图标彻底消失**。Windows 11 官方并未提供"彻底隐藏"的开关
> （ExplorerPatcher 维护者原话：微软没有做这件事的代码）。
>
> 要让图标从托盘与溢出面板**同时消失**，正确做法是让 Shell 删除该图标 ——
> 也就是跨进程调用 `Shell_NotifyIcon(NIM_DELETE, {hWnd, uID})`。
> 这与 gohide、以及本软件"隐藏自身托盘图标"选项（`NotifyIcon.Visible=false`）
> 是同一个底层 API：图标立即从托盘消失，进程照常运行。
> 其中 `hWnd` 来自枚举目标进程窗口，`uID`/`guidItem` 来自注册表的 `UID`/`IconGuid` 字段。

### 本软件做的事

- **隐藏**：调用 `Shell_NotifyIcon(NIM_DELETE)` 让 Shell 删除目标图标，
  图标立即从任务栏托盘与溢出面板同时消失，进程不受影响
- **显示**：调用 `Shell_NotifyIcon(NIM_ADD)` 用备份的图标字节重建图标
- 列表只显示**当前正在运行**的图标，历史残留条目自动过滤
- 刷新时对已隐藏图标重新执行 NIM_DELETE，应对程序周期性重新注册图标

整个过程**只让 Shell 增删图标，绝不删除注册表、绝不触碰目标进程**。
因此被隐藏的程序进程照常运行、消息循环照常响应、功能完全正常。

---

## 二、功能

### 主窗口

- 实时列出托盘中正在运行的软件图标与名称，**列表随系统变化自动刷新**（默认 3 秒）
- 每个条目显示：图标、名称（取自提示文本）、进程名、运行状态
- 单击行 = 勾选 / 取消勾选；双击行 = 立即隐藏 / 显示该图标
- 快捷操作：
  - `全部勾选`、`反选`、`取消勾选`
  - `全部隐藏` —— 一键隐藏所有图标
  - `全部显示` —— 一键恢复所有图标
  - `隐藏所选` —— 隐藏所有勾选项
- 隐藏与显示状态可随时切换，隐藏项在列表中带高亮状态徽标

### 设置页

- **开机自动启动** —— 写入 `HKCU\...\Run`，登录后自动后台运行并恢复上次隐藏状态
- **隐藏本软件的托盘图标** —— 隐藏后程序仍正常运行；保留**全局快捷键** `Ctrl + Win + T` 唤出主窗口
- **关闭窗口时最小化到托盘** —— 避免误关导致守护中断
- **恢复所有被隐藏的图标** —— 清空隐藏记录
- **打开配置文件目录**
- **退出程序**

### Windows 11 原生外观

- 圆角窗口（`DWMWA_WINDOW_CORNER_PREFERENCE`）
- 云母材质（`DWMWA_SYSTEMBACKDROP_TYPE` = Mica），旧版本回退到 `DWMWA_MICA_EFFECT`
- 跟随系统深 / 浅色主题，并响应运行时主题切换
- 自绘标题栏与 Win11 风格控件（ToggleSwitch、CheckBox、滚动条）
- 字体 `Segoe UI Variable Text`

### 持久化

所有勾选状态与设置项保存在：

```
%AppData%\TrayHider\config.json
```

软件或系统重启后自动恢复上次状态。

---

## 三、构建

需要 .NET 8 SDK（含 Windows Desktop 组件）。

```bash
cd src/TrayHider

# 调试构建
dotnet build -c Release

# 发布为独立的单文件 exe（无需目标机器安装 .NET）
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o ../../dist
```

产物：`dist/TrayHider.exe`

生成应用图标（可选，需要 Pillow）：

```bash
python build/make_icon.py
```

---

## 四、项目结构

```
TrayHider/
├── src/TrayHider/
│   ├── App.xaml(.cs)              入口：单实例、托盘图标、全局热键、主题跟随
│   ├── MainWindow.xaml(.cs)       主窗口：图标列表与快捷操作
│   ├── SettingsWindow.xaml(.cs)   设置页
│   ├── app.manifest               声明支持 Win10/Win11
│   ├── Interop/
│   │   ├── NativeMethods.cs       Win32 声明（DWM / 热键 / Shell 通知）
│   │   └── TrayIconReader.cs      ★ 注册表读写：枚举图标与切换可见性
│   ├── Models/
│   │   └── TrayIconItem.cs        列表条目模型
│   ├── Services/
│   │   ├── AppConfig.cs           配置持久化
│   │   ├── StartupManager.cs      开机自启
│   │   ├── WindowChrome.cs        圆角 / 云母 / 主题
│   │   └── IconExtractor.cs       图标提取（exe 资源 + 注册表快照）
│   ├── ViewModels/
│   │   └── MainViewModel.cs       列表刷新、隐藏/显示、状态持久化
│   └── Themes/Win11.xaml          Win11 风格控件样式
├── build/
│   ├── make_icon.py               生成多尺寸 .ico
│   └── ScanProbe/                 诊断工具（探查托盘数据结构）
└── dist/TrayHider.exe             发布产物
```

---

## 五、已知限制

1. **系统图标不可完全隐藏**。音量、网络、电源等由系统直接管理的图标，
   需要走「设置 → 个性化 → 任务栏」中的系统托盘图标开关，本软件不接管这部分。

2. **仅列出真实驻留托盘的图标**。`NotifyIconSettings` 会保留大量历史残留子键
   （即使程序早已卸载）。本软件通过 `Shell_NotifyIcon(NIM_MODIFY)` 探测过滤，
   只显示「图标确实在托盘」或「已被本软件隐藏」的条目，避免多余显示。

3. **`UIOrderList` 未做重排**。本软件只改可见性，不改变图标排列顺序，
   以免与 Shell 的内部状态冲突。

4. **需要与 explorer 同权限级别运行**。本程序以 `asInvoker` 运行（即普通用户权限）。
   如果目标程序以管理员身份运行，其图标可能无法被本软件修改。
   此时可用管理员身份启动本软件。

---

## 六、兼容性

- Windows 11（21H2 及以上）—— 主要目标平台
- Windows 10 —— 注册表结构相同，理论可用；但未在 Win10 上实测
- 需要 .NET 8（发布为 self-contained 时不需要）
