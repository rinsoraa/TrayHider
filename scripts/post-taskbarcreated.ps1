<#
    scripts\post-taskbarcreated.ps1

    用途：向指定进程名的**全部窗口**（顶层窗口 + 消息专用窗口 HWND_MESSAGE 的子窗口 + 顶层窗口的子窗口）
          投递 RegisterWindowMessage("TaskbarCreated")，让这些程序用自己的注册参数重新 NIM_ADD 托盘图标。
          这是 issue#1 修复机制的手工版，便于对任意程序单独验证（不需要 TrayProbe controller）。

    安全：只发这一条消息，不做 NIM_DELETE / NIM_ADD，不写注册表、不改系统设置。
          消息的接收方是目标程序自己，由它决定要不要重加图标。

    例：
        powershell -ExecutionPolicy Bypass -File scripts\post-taskbarcreated.ps1 -ProcessName HipsTray -Rounds 3
        powershell -ExecutionPolicy Bypass -File scripts\post-taskbarcreated.ps1 -ProcessName TrayProbeHost -Verbose
#>
param(
    [Parameter(Mandatory = $true)][string[]]$ProcessName,
    [int]$Rounds = 1,
    [int]$RoundDelayMs = 400
)

$ErrorActionPreference = 'Stop'

Add-Type -Namespace TrayProbe -Name Native -MemberDefinition @'
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
'@

$HWND_MESSAGE = [IntPtr](-3)
$taskbarCreated = [TrayProbe.Native]::RegisterWindowMessage('TaskbarCreated')
if ($taskbarCreated -eq 0) { throw 'RegisterWindowMessage(TaskbarCreated) 失败' }
Write-Host "TaskbarCreated = 0x$('{0:X4}' -f $taskbarCreated)"

function Get-WindowClass([IntPtr]$hwnd) {
    $sb = New-Object System.Text.StringBuilder 256
    [void][TrayProbe.Native]::GetClassName($hwnd, $sb, $sb.Capacity)
    return $sb.ToString()
}

foreach ($name in $ProcessName) {
    $pids = @(Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    if ($pids.Count -eq 0) { Write-Host "[$name] 进程未运行，跳过" -ForegroundColor Yellow; continue }

    $targets = New-Object System.Collections.Generic.List[object]
    $add = {
        param([IntPtr]$hwnd, [string]$kind)
        if ($hwnd -eq [IntPtr]::Zero) { return }
        $owner = 0
        [void][TrayProbe.Native]::GetWindowThreadProcessId($hwnd, [ref]$owner)
        if ($pids -notcontains $owner) { return }
        if ($targets | Where-Object { $_.Hwnd -eq $hwnd }) { return }
        $targets.Add([pscustomobject]@{ Hwnd = $hwnd; Kind = $kind; Class = (Get-WindowClass $hwnd) }) | Out-Null
    }

    $cb = [TrayProbe.Native+EnumProc]{ param($h, $l) & $add $h 'top-level'; return $true }
    [void][TrayProbe.Native]::EnumWindows($cb, [IntPtr]::Zero)

    $child = [TrayProbe.Native]::FindWindowEx($HWND_MESSAGE, [IntPtr]::Zero, $null, $null)
    while ($child -ne [IntPtr]::Zero) {
        & $add $child 'message-only'
        $child = [TrayProbe.Native]::FindWindowEx($HWND_MESSAGE, $child, $null, $null)
    }

    # 顶层窗口的子窗口（少数程序把托盘窗口挂在窗口树里）
    $tops = @($targets | Where-Object { $_.Kind -eq 'top-level' })
    foreach ($t in $tops) {
        $c = [TrayProbe.Native]::FindWindowEx($t.Hwnd, [IntPtr]::Zero, $null, $null)
        while ($c -ne [IntPtr]::Zero) {
            & $add $c 'child'
            $c = [TrayProbe.Native]::FindWindowEx($t.Hwnd, $c, $null, $null)
        }
    }

    Write-Host "[$name] pid=$($pids -join ',') 目标窗口 $($targets.Count) 个：$((($targets | ForEach-Object { "0x{0:X}/$($_.Class)/$($_.Kind)" -f $_.Hwnd.ToInt64() }) -join '、'))"

    for ($round = 1; $round -le $Rounds; $round++) {
        $ok = 0
        foreach ($t in $targets) {
            if ([TrayProbe.Native]::PostMessage($t.Hwnd, $taskbarCreated, [IntPtr]::Zero, [IntPtr]::Zero)) { $ok++ }
        }
        Write-Host ("  round {0}: Post 成功 {1}/{2} 个窗口" -f $round, $ok, $targets.Count)
        if ($round -lt $Rounds) { Start-Sleep -Milliseconds $RoundDelayMs }
    }
}
