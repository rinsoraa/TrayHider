# 托盘图标管家 / TrayHider

![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)
![Platform](https://img.shields.io/badge/Windows-10%2F11%20x64-0078D4)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![Version](https://img.shields.io/badge/version-1.2.8-brightgreen)

[English](README.en.md) · **简体中文** · [1.2.8 发布说明](docs/release-1.2.8.zh-CN.md)

把 Windows 通知区域（系统托盘）里的图标、以及正在运行的窗口，收进一个托盘菜单里管理。
隐藏**不会删除**目标程序注册的图标，恢复后左键、右键菜单、双击与程序原生行为一致。

---

## 适用系统

| 项目 | 说明 |
| --- | --- |
| 操作系统 | **Windows 11 22H2 / 23H2 / 24H2 / 25H2（x64）** —— 主要开发与验证环境 |
| Windows 10 | 22H2（19045）x64：**可运行但未完整验证**，通知区域实现与 Win11 不同，部分功能可能受限 |
| 架构 | 仅 x64（暂无 ARM64 构建） |
| 运行时 | 便携版自带 .NET 8 运行时；精简版需预装 .NET 8 桌面运行时 |
| 权限 | 默认**不需要管理员权限**；不装服务、不注入进程、不改系统文件。管理提权程序时可「以管理员身份重启」 |
| 多显示器 / 高 DPI | 支持，Per-Monitor V2；副屏不同缩放比例下菜单按点击位置的物理像素定位 |
| 界面语言 | 简体中文 / English，首次启动按系统语言自动选择，可在设置中即时切换 |

---

## 功能

### 托盘图标
- 单击隐藏、再单击恢复；支持全部隐藏 / 全部恢复、按程序批量、按勾选批量
- 隐藏与已隐藏状态**持久化**，重启本软件后仍然记得
- 不会把本软件自己创建的占位图标混进列表（同一 exe 的多份副本也一并排除）

### 窗口
- 把任意窗口收进托盘，单击/双击托盘图标恢复，保留窗口位置、大小与最大化状态
- 可选联动：隐藏窗口时把该程序原本的托盘图标一起收起来
- 退出本软件时可自动恢复全部被隐藏的窗口

### 全局快捷键
- 五组动作：唤出主窗口、隐藏/恢复当前窗口、隐藏全部窗口、显示全部窗口、全部隐藏/恢复托盘图标
- 逐条修改、单条 × 清空（写 0/0 哨兵，重启不会被默认值补回）、一键「仅重置快捷键」
- 「附加 Win」录入：不必按 Win 键，勾上后按 Ctrl+F9 即得到 Ctrl+Win+F9
- 注册是事务性的：允许两行互换；某个默认组合被占用时会点名提示，不写坏其它绑定

### 不常驻模式（隐藏完图标就退出）
- 依赖「开机自动启动」：打开该选项时会先征询，同意后同时开启开机自启
- 登录后应用一遍隐藏，然后进程退出，**常驻内存为 0**
- 子选项「周期轮询」：
  - **关闭（默认）**：登录后统计已记录的图标，等它们**全部启动并被隐藏**后静默退出，不创建计划任务
  - **开启**：额外创建一个计划任务，每 **10 分钟**补应用一次，作为之后才启动的程序的保底
- 每次运行会写 \`%AppData%\TrayHider\runonce-stats.json\`：本次隐藏数、未就绪数、耗时，以及已隐藏 / 未就绪的进程清单

### 其它
- 二次启动不再只弹「已在运行中」：直接把已运行实例的主窗口唤出来（命名事件 + 窗口消息双通道）
- 「以管理员身份重启」：派生新实例等待旧实例退出后接管（--wait-instance）
- 中英双语，运行期切换；页脚版本号取自程序集，不会与实际版本对不上
- 主窗口收起后释放图标缓存并裁剪工作集，空闲时把内存立即让出
- 深色/浅色跟随系统主题；配置原子写入并在同目录保留 config.json.bak

---

## 已知限制（如实说明）

1. **非破坏性隐藏依赖系统的图标状态位**：个别程序会自己重新注册图标（例如收到 TaskbarCreated），此时图标会重新出现；本软件下次刷新/轮询时会再次隐藏。
2. **用 v1.1.0 / 1.2.1 及更早版本隐藏过的图标**（旧机制会删除注册）无法只靠状态位恢复：请先**重启对应程序**一次，之后再用新版本隐藏/显示；新版不再使用该破坏性机制。
3. **不常驻模式不隐藏窗口**：被隐藏的窗口需要常驻进程维持。
4. **统计等待上限 10 分钟**：超时后仍会退出，还没启动的程序要等下一次计划任务（若开启轮询）或下次登录。
5. **内存**：空闲裁剪主要让出的是工作集，私有字节实测约减少 4.5MB；WPF/原生基线仍占主要部分。
6. **Windows 10 与 ARM64** 未完整验证；RDP、动态切换缩放等极端场景未穷尽测试。

---

## 下载与使用

1. 在 Releases 下载 TrayHider-1.2.8-Portable-win-x64.zip（自带运行时）或 -Lite-win-x64.zip（需 .NET 8 桌面运行时）
2. 解压到任意目录，运行 TrayHider.exe
3. 左侧切换「托盘图标 / 窗口」分区；右上角进入设置
4. 配置位于 \`%AppData%\TrayHider\config.json\`（同目录有 .bak 上一版备份）

| 命令行参数 | 作用 |
| --- | --- |
| （无） | 常驻启动 |
| --minimized | 启动后最小化到托盘（开机自启使用） |
| --apply-once | 只应用一次隐藏状态后退出（不常驻模式 / 计划任务使用） |
| --wait-instance | 等待已有实例退出后接管（提权重启使用） |

---

## 从源码构建

~~~powershell
git clone https://github.com/rinsoraa/TrayHider.git
cd TrayHider
dotnet build TrayHider.csproj -c Release
dotnet publish TrayHider.csproj -c Release -r win-x64 -p:PublishProfile=portable-win-x64
dotnet publish TrayHider.csproj -c Release -r win-x64 -p:PublishProfile=lite-win-x64
~~~

发版前必跑（编译通过不等于能启动）：

~~~powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1 -ExePath <发布产物> -Seconds 20
dotnet run -c Release --project tests/TrayHider.Checks          # 配置 / 快捷键（18 项）
dotnet run -c Release --project tests/TrayHider.TrayModeChecks  # 托盘隐藏机制（25 项，需交互式桌面）
~~~

---

## 参与贡献

**欢迎 Pull Request 与 Issue**，尤其是：

- 🖥 **新系统 / 新架构适配**：Windows 10 各版本、Windows Server、ARM64、Windows Insider
- 🐞 **Bug 报告**：请附 Windows 版本、显示器数量与缩放、程序版本、复现步骤；托盘问题请说明图标属于哪个程序
- 🌍 **翻译**：目前中英双语，文案在 Services/Localization/Strings.<语言>.<区域>.json
- ⚡ **性能实测**：可复现的测量结果比猜测有价值得多

提交 PR 前请确认：构建 0 警告、两套回归通过、新增文案中英各一份。

## 许可证

[MIT License](LICENSE)
