<#
    scripts\smoke-test.ps1

    启动冒烟测试：**编译通过不等于能启动**。

    WPF 的绑定错误、XAML 资源缺失等问题只在窗口 Show 之后才暴露，而且往往延迟几秒才在
    数据绑定引擎的 OnLayoutUpdated 阶段抛出（表现为进程无提示退出，退出码 0xE0434352）。
    v1.2.0 开发期间就出现过一次：MainWindow.xaml 里 <Run Text="{Binding HotkeyText}"/>
    绑定只读属性（Run.Text 默认 TwoWay），程序启动约 10 秒后崩溃。

    用法：
        powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
        powershell -ExecutionPolicy Bypass -File scripts\smoke-test.ps1 -ExePath bin\Release\net8.0-windows\TrayHider.exe -Seconds 20

    判定标准：
        · 进程在观察期内始终存活；
        · 出现被测进程的 WPF 主窗口（仅有单实例提示框不算通过）。
    任一条不满足即返回非 0 退出码，并打印最近的 .NET Runtime 未处理异常，便于定位。
#>
param(
    [string]$ExePath = "bin\Release\net8.0-windows\TrayHider.exe",
    [int]$Seconds = 20,
    [string]$InstanceMutex = "Global\TrayHider.SingleInstance.v1"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    Write-Output "[smoke] FAIL 找不到可执行文件：$ExePath（先 dotnet build -c Release）"
    exit 2
}

$full = (Resolve-Path $ExePath).Path
Write-Output "[smoke] exe = $full"
Write-Output ("[smoke] size = {0} B" -f (Get-Item $full).Length)

# 不启动第二实例，否则可能唤起用户窗口，或将单实例提示框误判为启动成功。
$existing = $null
if ([Threading.Mutex]::TryOpenExisting($InstanceMutex, [ref]$existing)) {
    $existing.Dispose()
    Write-Output "[smoke] SKIP 已有实例在运行；未启动、未结束任何进程。请在干净会话中测试。"
    exit 3
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SmokeWindow {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int length);
    public static bool IsWpf(IntPtr hwnd) {
        var buf = new StringBuilder(256);
        return GetClassName(hwnd, buf, buf.Capacity) > 0 && buf.ToString().StartsWith("HwndWrapper[", StringComparison.Ordinal);
    }
}
'@

$started = Get-Date
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $full
$start.UseShellExecute = $false  # 被测进程继承当前测试环境，不让 Shell 在别的环境中启动它
$p = [Diagnostics.Process]::Start($start)

$deadline = (Get-Date).AddSeconds($Seconds)
$windowSeen = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if ($p.HasExited) { break }
    $proc = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    if ($proc -and $proc.MainWindowTitle -eq '托盘图标管家' -and [SmokeWindow]::IsWpf($proc.MainWindowHandle)) { $windowSeen = $true }
}

if ($p.HasExited) {
    Write-Output ("[smoke] FAIL 进程在 {0:N1} 秒后退出，退出码 = {1}" -f ((Get-Date) - $started).TotalSeconds, $p.ExitCode)
    Write-Output "[smoke] 最近的 .NET 未处理异常："
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = '.NET Runtime'; StartTime = $started } -MaxEvents 2 -ErrorAction SilentlyContinue |
        ForEach-Object { ($_.Message -split "`n" | Select-Object -First 10) -join "`n" }
    exit 1
}

$alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
$ws = if ($alive) { [math]::Round($alive.WorkingSet64 / 1MB, 1) } else { 0 }
Write-Output ("[smoke] alive after {0}s, working set = {1} MB, main window handle seen = {2}" -f $Seconds, $ws, $windowSeen)

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue

if (-not $windowSeen) {
    Write-Output "[smoke] FAIL 进程活着但没有出现主窗口（可能卡在单实例提示或 XAML 初始化阶段）"
    exit 1
}

Write-Output "[smoke] PASS"
exit 0
