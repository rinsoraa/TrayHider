# 托盘图标管家 / TrayHider

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![Platform](https://img.shields.io/badge/Windows-10%2F11%20x64-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)

> **English**: see [README.en.md](README.en.md)

把 Windows 通知区域（系统托盘）里的图标、以及正在运行的窗口，一键收进本软件的托盘菜单里。
隐藏**不删除**目标程序注册的图标，恢复后左右键、右键菜单、双击行为与程序原生表现一致。

---

## 适用系统

| 项目 | 说明 |
| --- | --- |
| 操作系统 | **Windows 11（22H2 / 23H2 / 24H2 / 25H2）x64** —— 主要开发与测试环境 |
| Windows 10 | 19045（22H2）x64 **可用但未完整验证**，托盘枚举依赖 Win11 的 `NotifyIconSettings`，部分功能可能受限 |
| 架构 | 仅 x64（ARM64 尚无构建） |
| 运行库 | 便携版自带 .NET 8 运行时；精简版需预装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 权限 | **默认不需要管理员权限**，也不安装服务、不注入进程、不改系统文件 |
| 多显示器 / 高 DPI | 支持；Per-Monitor V2，跨不同缩放副屏可用 |
| 多语言 | 简体中文 / English，首次启动按系统语言自动选择，可在设置中切换 |

> 机制说明：本软件通过 `Shell_NotifyIcon`（`NIM_MODIFY` + `NIF_STATE`/`NIS_HIDDEN`）修改图标的**显示状态位**，
> 图标仍然属于原程序，回调与右键菜单保持原样。这是它能做到"恢复后还能点"的原因。
> Windows 10 的通知区域实现不同，欢迎提交适配 PR。

---

## 功能

- **托盘图标隐藏 / 恢复**：单击隐藏，再单击恢复；支持全部隐藏 / 全部恢复、按程序批量操作
- **窗口隐藏到托盘**：把任意窗口收进托盘，单击托盘图标恢复，保留窗口位置与最大化状态
- **全局快捷键**：唤出主窗口、隐藏/恢复当前窗口、隐藏全部窗口、显示全部窗口、全部隐藏/恢复托盘图标；
  支持逐条修改、单条清空、「附加 Win」录入、一键恢复默认
- **不常驻模式（v1.2.3）**：登录时应用一次隐藏后自动退出进程，由计划任务定期补应用，常驻占用为 0
- **空闲内存优化（v1.2.3）**：主窗口收起后释放图标缓存并裁剪工作集
- **中英双语界面（v1.2.3）**：随系统语言自动选择，可在设置中切换并立即生效
- 开机自动启动、关闭窗口最小化到托盘、退出时恢复全部窗口
- 深浅色跟随系统主题

---

## 已知限制（如实说明）

1. **不常驻模式的时效性**：两次应用之间的间隔（默认 3 分钟）内新启动的程序，图标会先显示出来；程序自己重新注册图标（如收到 `TaskbarCreated`）也会复位状态，需等下一次应用。
2. **被隐藏的窗口**需要常驻进程维持，因此不常驻模式下不隐藏窗口。
3. **v1.2.1 及更早版本隐藏过的图标**（旧机制会删除注册）无法仅靠状态位恢复，需**重启对应程序**一次；新版不再使用该破坏性机制。
4. 个别程序（如火绒、部分安全软件）对状态位有自己的处理，若隐藏后行为异常，请重启该程序。
5. 任务栏自动隐藏、全屏独占、提权程序等边缘场景可能与正常情况不同，遇到请附环境说明提交 issue。

---

## 下载与使用

1. 从 Releases 下载 `TrayHider-<版本>-Portable-win-x64.zip`（自带运行时）或 `-Lite-win-x64.zip`（需 .NET 8 桌面运行时）
2. 解压到任意目录，双击 `TrayHider.exe`
3. 主窗口左侧切换「托盘图标 / 窗口」两个分区；右侧可打开设置
4. 首次运行如提示配置目录，配置文件位于 `%AppData%\TrayHider\config.json`（同目录另有 `.bak` 上一版备份）

**不常驻模式**：设置 → 勾选「隐藏图标后退出（不常驻）」→ 程序会注册一个名为 `TrayHider-ApplyHiddenState`
的计划任务（每 N 分钟运行一次 `--apply-once`），并自动把开机自启命令改为只隐藏一次就退出。
关闭该选项会自动删除计划任务。

命令行参数：

| 参数 | 作用 |
| --- | --- |
| （无） | 正常启动（常驻） |
| `--minimized` | 启动后最小化到托盘（开机自启用） |
| `--apply-once` | 只应用一次隐藏状态并退出（计划任务用） |
| `--wait-instance` | 等待已有实例退出后接管（提权重启用） |

---

## 从源码构建

```powershell
git clone https://github.com/rinsoraa/TrayHider.git
cd TrayHider
dotnet build TrayHider.csproj -c Release
# 自包含单文件便携版
dotnet publish TrayHider.csproj -c Release -r win-x64 -p:PublishProfile=portable-win-x64
# 需要 .NET 8 的精简版
dotnet publish TrayHider.csproj -c Release -r win-x64 -p:PublishProfile=lite-win-x64
```

发版前必跑（编译通过 ≠ 能启动）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1 -ExePath <发布产物> -Seconds 20
dotnet run -c Release --project tests/TrayHider.Checks          # 配置/快捷键回归
dotnet run -c Release --project tests/TrayHider.TrayModeChecks  # 托盘隐藏机制回归（需交互式桌面）
```

---

## 参与贡献

**欢迎 Pull Request 和 Issue**，尤其是：

- 🖥 **新系统版本 / 新架构适配**：Windows 10 全版本、Windows Server、ARM64、预览版 Windows
- 🐞 **Bug 报告**：请附 Windows 版本、多显示器与缩放、程序版本、复现步骤；托盘相关问题请说明图标来源程序
- 🌍 **翻译**：目前为中文 + 英文，欢迎补充其他语言（`Services/Localization/Strings.*.json`）
- ⚡ **性能与内存**：任何可复现的测量结果都比猜测有用

提交 PR 前请确认：构建 0 警告、两套回归通过、新增文案同时提供中英两版。

---

## 许可证

[MIT License](LICENSE)

## 致谢

- 托盘枚举思路参考 Windows 11 `NotifyIconSettings` 注册表结构与社区讨论
- 界面风格参考 Windows 11 Fluent Design
