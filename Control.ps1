param([ValidateSet('pause','resume','stop','disconnect','status')][string]$Action='status')
$ErrorActionPreference = 'Stop'
$runtime = Join-Path $PSScriptRoot 'runtime'
$statusPath = Join-Path $runtime 'status.json'
function Read-ControlStatus {
    $stream=[IO.File]::Open($statusPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$true)
    try { $text=$reader.ReadToEnd() } finally { $reader.Dispose() }
    return ($text | ConvertFrom-Json)
}
if (-not (Test-Path -LiteralPath $statusPath)) {
    if (Test-Path -LiteralPath (Join-Path $runtime 'launcher-status.json')) { Get-Content -LiteralPath (Join-Path $runtime 'launcher-status.json') -Encoding utf8 }
    Write-Output '尚无运行状态，请先启动。'
    exit 0
}
if ($Action -ne 'status') {
    $status = Read-ControlStatus
    if (-not $status.running -or -not (Get-Process -Id $status.pid -ErrorAction SilentlyContinue)) { throw '后台脚本未运行。' }
    if ($Action -eq 'resume' -and (Test-Path -LiteralPath (Join-Path $runtime 'pending.json'))) { throw '存在结果未确认的操作，已阻止恢复；请查看 pending.json 与日志。' }
    $command = @{ id=[guid]::NewGuid().ToString(); action=$Action }
    $temporary = Join-Path $runtime ($command.id + '.tmp')
    [IO.File]::WriteAllText($temporary, ($command | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination (Join-Path $runtime 'control.json') -Force
    $deadline = [DateTime]::UtcNow.AddSeconds($(if ($Action -in @('stop','disconnect')) {40} else {10}))
    do {
        Start-Sleep -Milliseconds 300
        $status = Read-ControlStatus
        if (($Action -in @('stop','disconnect') -and -not $status.running) -or ($Action -eq 'pause' -and $status.paused) -or ($Action -eq 'resume' -and -not $status.paused)) { break }
    } while ([DateTime]::UtcNow -lt $deadline)
}
$status = Read-ControlStatus
$alive = $status.running -and [bool](Get-Process -Id $status.pid -ErrorAction SilentlyContinue)
Write-Output ('运行：' + $alive + '；暂停：' + $status.paused)
Write-Output ('状态：' + $status.message)
if ($status.fault) { Write-Output ('异常：' + $status.fault) }
if ($status.state) {
    Write-Output ('可用次数：' + $status.state.available + '；累计次数：' + $status.state.cumulative)
    foreach ($side in @('left','right')) {
        $remaining = [Math]::Max(0,[Math]::Ceiling(($status.state.$side - [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())/1000))
        Write-Output ($side + ' 槽位剩余秒数：' + $remaining)
    }
}
if (-not $status.running) { Write-Output ('已恢复普通启动：' + $status.normalStartupRestored) }
$localTime = ([DateTimeOffset]$status.at).ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd HH:mm:ss zzz')
Write-Output ('记录时间：' + $localTime)
