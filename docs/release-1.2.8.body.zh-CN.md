## 托盘图标管家 v1.2.8

### 下载

- [TrayHider-1.2.8-Portable-win-x64.zip](https://github.com/rinsoraa/TrayHider/releases/download/v1.2.8/TrayHider-1.2.8-Portable-win-x64.zip) —— 便携版，自带 .NET 8 运行时
- [TrayHider-1.2.8-Lite-win-x64.zip](https://github.com/rinsoraa/TrayHider/releases/download/v1.2.8/TrayHider-1.2.8-Lite-win-x64.zip) —— 精简版，需已安装 .NET 8 桌面运行时

### 更新内容

- 托盘图标隐藏不再删除程序注册，恢复后左键、右键菜单、双击均正常
- 修复火绒、WitchDrawer 等程序隐藏后再显示、图标点不动的问题
- 新增：把普通窗口隐藏到托盘，可选退出时自动恢复
- 全局快捷键从 1 组扩展为 5 组，可逐条修改、单条清空、一键重置，支持「附加 Win」录入
- 再次双击 exe 直接唤出已运行实例的主窗口，不再只弹提示框
- 新增「以管理员身份重启」
- 修复主窗口位于副屏时托盘右键菜单弹错位置
- 新增不常驻模式：开机隐藏图标后自动退出，可选每 10 分钟轮询兜底，并输出 runonce-stats.json 统计
- 新增中英双语界面，跟随系统语言，可在设置中即时切换
- 空闲时释放图标缓存并裁剪工作集
- 修复列表中混入本软件自身条目、快捷键修改不生效、界面文案被遮挡等问题

### 系统要求

- Windows 11 22H2 / 23H2 / 24H2 / 25H2（x64）
- Windows 10 22H2（19045）x64：可运行，未完整验证
- 便携版无需安装运行时；精简版需 .NET 8 桌面运行时
- 默认不需要管理员权限
