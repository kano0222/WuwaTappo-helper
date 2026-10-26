$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Tappo reopen ' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$shell = Join-Path $root 'dependencies\powershell\pwsh.exe'
if (-not (Test-Path -LiteralPath $shell)) { $shell = Join-Path $PSHOME 'pwsh.exe' }
$children = @()
function Start-Check([string]$Script) {
    $info = [Diagnostics.ProcessStartInfo]::new($shell, ('-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File "' + $Script + '"'))
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    return [Diagnostics.Process]::Start($info)
}
function Wait-Check($Process) {
    if (-not $Process.WaitForExit(15000)) { $Process.Kill($true); throw '面板关闭后进程没有退出。' }
    if ($Process.ExitCode -ne 0) { throw ('面板进程异常退出：' + $Process.ExitCode) }
}
try {
    $panel = Join-Path $fixture 'Panel.ps1'
    $worker = Join-Path $fixture 'worker.ps1'
    Copy-Item -LiteralPath (Join-Path $root 'Panel.ps1') -Destination $panel
    foreach ($directory in @('scripts','assets')) { [void][IO.Directory]::CreateDirectory((Join-Path $fixture $directory)) }
    foreach ($name in @('App-Discovery.ps1','Panel-Tray.ps1')) { Copy-Item -LiteralPath (Join-Path $root "scripts\$name") -Destination (Join-Path $fixture 'scripts') }
    Copy-Item -LiteralPath (Join-Path $root 'assets\helper.ico') -Destination (Join-Path $fixture 'assets')
    '{"profiles":[{"executableSha256":"not-a-real-app"}]}' | Set-Content -LiteralPath (Join-Path $fixture 'app-profile.json')
    # Close this isolated test window from its own dispatcher, without account operations.
    @'
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$script:AutoClose = [Windows.Threading.DispatcherTimer]::new()
$script:AutoClose.Interval = [TimeSpan]::FromSeconds(1)
$script:TestStep=0
$script:TrayTipShown=$true
$script:AutoClose.Add_Tick({
    switch ($script:TestStep) {
        0 {
            $script:Window.Close()
            if ($script:Window.IsVisible -or -not $script:TrayIcon.Visible) { throw '关闭没有收进托盘。' }
        }
        1 { [void]$script:ShowEvent.Set() }
        2 {
            if (-not $script:Window.IsVisible) { throw '单实例唤起没有恢复窗口。' }
            $script:AutoClose.Stop()
            Request-PanelExit
        }
    }
    $script:TestStep++
})
$script:AutoClose.Start()
. (Join-Path $PSScriptRoot 'Panel.ps1')
if ($script:TrayIcon) { throw '退出后托盘图标未释放。' }
'@ | Set-Content -LiteralPath $worker -Encoding utf8
    $first = Start-Check $worker; $children += $first
    $log = Join-Path $fixture 'runtime\panel-lifecycle.log'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $visible = (Test-Path -LiteralPath $log) -and ((Get-Content -LiteralPath $log -Raw) -match ' visible')
    } while (-not $visible -and -not $first.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if (-not $visible) { throw '面板未显示。' }
    $duplicate = Start-Check $panel; $children += $duplicate
    Wait-Check $duplicate
    Wait-Check $first
    $second = Start-Check $worker; $children += $second
    Wait-Check $second
    $events = Get-Content -LiteralPath $log
    if (@($events | Where-Object { $_ -match ' hidden-to-tray$' }).Count -ne 2 -or @($events | Where-Object { $_ -match ' restored$' }).Count -lt 2 -or @($events | Where-Object { $_ -match ' closed$' }).Count -ne 2 -or @($events | Where-Object { $_ -match ' cleanup$' }).Count -ne 2) {
        throw '显示、关闭或锁释放记录不完整。'
    }
    Write-Output '通过：首次显示、重复打开、关闭进托盘、唤起、退出清理及再次启动；未操作真实后台。'
} finally {
    foreach ($child in $children) { if (-not $child.HasExited) { $child.Kill($true); $child.WaitForExit() }; $child.Dispose() }
    foreach ($relative in @('Panel.ps1','worker.ps1','app-profile.json','scripts\App-Discovery.ps1','scripts\Panel-Tray.ps1','assets\helper.ico','runtime\panel-lifecycle.log')) {
        $file = Join-Path $fixture $relative
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
    if (Test-Path -LiteralPath (Join-Path $fixture 'runtime')) { Remove-Item -LiteralPath (Join-Path $fixture 'runtime') }
    foreach ($directory in @('scripts','assets')) { Remove-Item -LiteralPath (Join-Path $fixture $directory) }
    Remove-Item -LiteralPath $fixture
}
