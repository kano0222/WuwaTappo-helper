param([switch]$Paused)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$runtime = Join-Path $PSScriptRoot 'runtime'
[void][IO.Directory]::CreateDirectory($runtime)
try {
    . (Join-Path $PSScriptRoot 'scripts\App-Discovery.ps1')
    $appPath = Resolve-TappoApp -Root $PSScriptRoot
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $shell = (Get-Process -Id $PID).Path
        $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File', ('"' + $PSCommandPath + '"'))
        if ($Paused) { $arguments += '-Paused' }
        Start-Process -FilePath $shell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -ErrorAction Stop
        Write-Output '已请求管理员启动。请在控制面板查看结果。'
        exit 0
    }
    $node = Join-Path $PSScriptRoot 'dependencies\node\node.exe'
    if (-not (Test-Path -LiteralPath $node)) { $node = (Get-Command node -ErrorAction Stop).Source }
    $nodeMajor = [int]((& $node --version).TrimStart('v').Split('.')[0])
    if ($nodeMajor -lt 22) { throw '需要 Node.js 22 或更新版本。' }
    if (Test-Path -LiteralPath 'runtime\lock.json') {
        $lock = Get-Content -LiteralPath 'runtime\lock.json' -Raw -Encoding utf8 | ConvertFrom-Json
        if (Get-Process -Id $lock.pid -ErrorAction SilentlyContinue) { throw '自动化已运行，请使用暂停、恢复或停止。' }
    }
    $app = Get-Process -Name WuwaTappo -ErrorAction SilentlyContinue
    if (-not $app) {
        & (Join-Path $PSScriptRoot 'scripts\Start-DebugSession.ps1') -AppPath $appPath
        $launch = Get-Content -LiteralPath 'runtime\debug-policy-session.json' -Raw -Encoding utf8 | ConvertFrom-Json
        if (-not $launch.debugReady -or -not $launch.policyRestored) { throw ('桌宠启动失败或配置未恢复：' + $launch.error + ' ' + $launch.restoreError) }
    }
    $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 19222 -ErrorAction SilentlyContinue)
    if (-not $listeners.Count) { throw '请先从桌宠设置或托盘选择“退出应用”，再启动本脚本。' }
    if (@($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1','::1') }).Count) { throw '调试端口并非仅限本机，已停止。' }
    $command = @{ id=[guid]::NewGuid().ToString(); action=$(if ($Paused) {'pause'} else {'resume'}) }
    [IO.File]::WriteAllText((Join-Path $runtime 'control.json'), ($command | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $runner = Start-Process -FilePath $node -ArgumentList @('automation.mjs','--run') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runtime 'stdout.log') -RedirectStandardError (Join-Path $runtime 'stderr.log') -PassThru
    @{ requestedAt=[DateTimeOffset]::Now.ToString('o'); runnerId=$runner.Id; paused=[bool]$Paused } | ConvertTo-Json | Set-Content -LiteralPath 'runtime\launcher-status.json' -Encoding utf8
    Write-Output '后台脚本已启动，可关闭本窗口。'
} catch {
    @{ failedAt=[DateTimeOffset]::Now.ToString('o'); error=$_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath 'runtime\launcher-status.json' -Encoding utf8
    Write-Error $_.Exception.Message
    exit 1
}
