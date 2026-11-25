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
        · 主窗口句柄非 0（说明主窗口真的创建成功，而不是卡在崩溃/单实例对话框里）。
    任一条不满足即返回非 0 退出码，并打印最近的 .NET Runtime 未处理异常，便于定位。
#>
param(
    [string]$ExePath = "bin\Release\net8.0-windows\TrayHider.exe",
    [int]$Seconds = 20
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    Write-Output "[smoke] FAIL 找不到可执行文件：$ExePath（先 dotnet build -c Release）"
    exit 2
}

$full = (Resolve-Path $ExePath).Path
Write-Output "[smoke] exe = $full"
Write-Output ("[smoke] size = {0} B" -f (Get-Item $full).Length)

# 若已有实例在跑会触发单实例对话框，先提示
$existing = Get-Process -Name TrayHider -ErrorAction SilentlyContinue
if ($existing) {
    Write-Output ("[smoke] WARN 已有 TrayHider 在运行（pid=" + ($existing.Id -join ',') + "），可能是产品自身，也可能是残留；请先关闭再测。")
}

$started = Get-Date
$p = Start-Process -FilePath $full -PassThru

$deadline = (Get-Date).AddSeconds($Seconds)
$windowSeen = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if ($p.HasExited) { break }
    $proc = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    if ($proc -and $proc.MainWindowHandle -ne 0) { $windowSeen = $true }
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
