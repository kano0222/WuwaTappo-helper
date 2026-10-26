# UI/controller smoke checks with simulated view state and a stubbed process launcher.
# Does not start the automation or write its control/status files.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'Panel.ps1') -RenderPath (Join-Path $root 'runtime\panel-preview.png')
$script:SmokeChecks=0
function Assert-Panel($Condition,[string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:SmokeChecks++
}
$now=[DateTimeOffset]::UtcNow
$script:Fixture=@{
    alive=$false; stale=$false; pending=$false; launcher=$null
    data=[pscustomobject]@{at=$now; running=$false; paused=$false; fault=$null; normalStartupRestored=$true; automationDisconnected=$false; claimsThisRun=2;
        state=[pscustomobject]@{available=1500;cumulative=4500;loggedIn=$true;left=$now.ToUnixTimeMilliseconds()+720000;right=$now.ToUnixTimeMilliseconds()-1000;buttons=[pscustomobject]@{left=$false;right=$true}}}
}
$script:Fixture.data | Add-Member -NotePropertyName chestTimes -NotePropertyValue ([pscustomobject]@{updatedAt=$now.ToUnixTimeMilliseconds();left=$script:Fixture.data.state.left;right=$script:Fixture.data.state.right;error=$null})
function Read-View { return $script:Fixture }
Update-Panel
Assert-Panel ($script:Elements.LeftTime.Text -eq '未连接') 'Stopped data displayed as live cooldown'
Assert-Panel ($script:Elements.LeftHint.Text -eq '') 'Removed startup description still displayed'
Assert-Panel ($script:Elements.FooterHint.Child.HorizontalAlignment -eq 'Left' -and $script:Elements.FooterHint.Child.TextAlignment -eq 'Left') 'Tray hint not left aligned'
$footerBefore=$script:Elements.FooterHint.TranslatePoint([Windows.Point]::new(0,0),$script:Elements.RootSurface)
$script:Elements.MainScroll.ScrollToEnd()
$script:Elements.RootSurface.UpdateLayout()
$footerAfter=$script:Elements.FooterHint.TranslatePoint([Windows.Point]::new(0,0),$script:Elements.RootSurface)
Assert-Panel ($footerBefore.Y -eq $footerAfter.Y -and $script:Elements.FooterHint.Parent -eq $script:Elements.MainScroll.Parent) 'Tray hint moved with scroll content'
$script:Elements.MainScroll.ScrollToTop()
$script:Elements.RootSurface.UpdateLayout()
Assert-Panel (-not $script:Elements.UsageHint.IsExpanded -and $script:Elements.UsageHint.ActualHeight -gt 0) 'Usage guidance should be available and collapsed by default'
Assert-Panel ($script:Elements.CountCaption.Text -eq '上次运行的记录') 'Historical values not identified'
Assert-Panel ($script:Elements.StartButton.IsEnabled -and -not $script:Elements.StopButton.IsEnabled) 'Stopped controls incorrect'
$script:Fixture.alive=$true; $script:Fixture.data.running=$true
Update-Panel
Assert-Panel ($script:Elements.LeftTime.Text -match '^\d{2}:\d{2}$') 'Live countdown not shown'
Assert-Panel ($script:Elements.LeftFill.Width -gt 0 -and $script:Elements.LeftFill.Width -lt $script:Elements.LeftTrack.ActualWidth) 'Countdown progress rounded to empty or full'
Assert-Panel ($script:Elements.RightTime.Text -eq '已就绪') 'Ready slot not shown'
Assert-Panel (-not $script:Elements.StartButton.IsEnabled -and $script:Elements.PauseButton.IsEnabled) 'Live controls incorrect'
$savedChestTime=$script:Fixture.data.chestTimes.left
$script:Fixture.data.chestTimes.left=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()+300000
Update-Panel
Assert-Panel ($script:Elements.LeftFill.Width/$script:Elements.LeftTrack.ActualWidth -gt 0.8) 'Chest display uses local cache instead of server timestamp'
$script:Fixture.data.chestTimes.error='模拟接口超时'
Update-Panel
Assert-Panel ($script:Elements.LeftTime.Text -eq '等待同步' -and $script:Elements.LeftFill.Width -eq 0) 'Failed query displays a fresh countdown'
$script:Fixture.data.chestTimes.error=$null
$script:Fixture.data.chestTimes.updatedAt=$now.ToUnixTimeMilliseconds()-100000
Update-Panel
Assert-Panel ($script:Elements.LeftTime.Text -eq '等待同步') 'Expired timestamp displayed as live'
$script:Fixture.data.chestTimes.updatedAt=$now.ToUnixTimeMilliseconds()
$script:Fixture.data.chestTimes.left=$savedChestTime
$script:Fixture.data.paused=$true
Update-Panel
Assert-Panel ($script:Elements.PauseButton.Content -eq '继续运行') 'Pause/resume label incorrect'
Assert-Panel ($script:Elements.LeftTime.Text -match '^\d{2}:\d{2}$') 'Paused display lost server countdown'
$script:Fixture.pending=$true
Update-Panel
Assert-Panel (-not $script:Elements.PauseButton.IsEnabled -and $script:Elements.StopButton.IsEnabled) 'Uncertain operation permits resume'
$script:Fixture.data.paused=$false
Update-Panel
Assert-Panel ($script:Elements.StatusLabel.Text -eq '处理中') 'In-flight action incorrectly presented as an error'
$script:Fixture.pending=$false; $script:Fixture.stale=$true
Update-Panel
Assert-Panel ($script:Elements.LeftTime.Text -eq '未连接' -and $script:Elements.StatusLabel.Text -eq '状态未更新') 'Stale state appears live'
$script:Fixture.stale=$false
$script:RecordedDispatches=@()
function Start-Process {
    param($FilePath,$ArgumentList,$WorkingDirectory,$WindowStyle,$RedirectStandardOutput,$RedirectStandardError,[switch]$PassThru)
    $script:RecordedDispatches+=,@{file=$FilePath;arguments=$ArgumentList;windowStyle=$WindowStyle}
    return [pscustomobject]@{HasExited=$false;ExitCode=0}
}
function Get-Process {
    param($Id,$Name,$ErrorAction)
    if ($Name) { return $null }
    return (Microsoft.PowerShell.Management\Get-Process -Id $Id)
}
function Resolve-TappoApp { return 'C:\Test\WuwaTappo.exe' }
foreach ($action in @('start','pause','resume','stop','disconnect')) {
    $script:Operation=$null
    $script:Fixture.alive=($action -ne 'start')
    $script:Fixture.data.running=($action -ne 'start')
    $script:Fixture.data.paused=($action -eq 'resume')
    Invoke-PanelAction $action
    $dispatch=$script:RecordedDispatches[-1]
    $target=if ($action -eq 'start') {'Start.ps1'} else {'Control.ps1'}
    Assert-Panel (($dispatch.arguments -join ' ') -match [regex]::Escape($target)) "Wrong target for $action"
    if ($action -ne 'start') { Assert-Panel ($dispatch.arguments[-1] -eq $action) "Wrong action for $action" }
    Assert-Panel ($dispatch.windowStyle -eq 'Hidden') 'Command window not hidden'
    $count=$script:RecordedDispatches.Count
    Invoke-PanelAction 'start'
    Assert-Panel ($script:RecordedDispatches.Count -eq $count) 'Duplicate action was dispatched'
}
$script:Operation=$null
$script:Fixture.alive=$false; $script:Fixture.data.running=$false
$script:Fixture.launcher=[pscustomobject]@{error='请先退出桌宠';failedAt=[DateTimeOffset]::UtcNow}
$script:Operation=@{action='start';started=$now;process=[pscustomobject]@{HasExited=$false;ExitCode=0}}
Update-Panel
Assert-Panel ($script:Operation -eq $null -and $script:Elements.DetailMessage.Text -eq '请先退出桌宠') 'Startup failure not surfaced'
$modeFixture=Join-Path ([IO.Path]::GetTempPath()) ('tappo-mode-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($modeFixture)
$originalRuntime=$script:Runtime
try {
    $script:Runtime=$modeFixture
    # Ensure the first tested selection changes, regardless of local preferences.
    $script:UpdatingModeSelection=$true
    $script:Elements.ModeSelector.SelectedIndex=0
    $script:UpdatingModeSelection=$false
    $commandCount=$script:RecordedDispatches.Count
    foreach ($item in @($script:Elements.ModeSelector.Items[1],$script:Elements.ModeSelector.Items[2],$script:Elements.ModeSelector.Items[0])) {
        $script:Elements.ModeSelector.SelectedItem=$item
        $saved=Get-Content -LiteralPath (Join-Path $modeFixture 'preferences.json') -Raw | ConvertFrom-Json
        Assert-Panel ($saved.mode -eq $item.Tag) 'Mode selection did not persist'
    }
    Assert-Panel ($script:RecordedDispatches.Count -eq $commandCount) 'Changing modes executed a backend command'
    $script:Elements.ModeSelector.SelectedItem=$script:Elements.ModeSelector.Items[2]
    $script:Fixture.alive=$true
    Update-Panel
    Assert-Panel ($script:Elements.ModeHint.Text -like '*需停止并重新启动*') 'Old backend was presented as supporting modes'
    $script:Fixture.data | Add-Member -NotePropertyName mode -NotePropertyValue human -Force
    Update-Panel
    Assert-Panel ($script:Elements.ModeHint.Text -like '已应用：人类模式*') 'Active backend mode not displayed'
    function Move-Item { throw '模拟保存失败' }
    try {
        $script:Elements.ModeSelector.SelectedItem=$script:Elements.ModeSelector.Items[0]
        Assert-Panel ($script:Elements.ModeSelector.SelectedItem.Tag -eq 'human') 'Failed save did not restore selected mode'
        $saved=Get-Content -LiteralPath (Join-Path $modeFixture 'preferences.json') -Raw | ConvertFrom-Json
        Assert-Panel ($saved.mode -eq 'human') 'Failed save changed the stored mode'
    } finally { Remove-Item Function:\Move-Item }
} finally {
    $script:Runtime=$originalRuntime
    Remove-Item -LiteralPath (Join-Path $modeFixture 'preferences.json')
    Remove-Item -LiteralPath $modeFixture
}
$script:Operation=$null
$script:Fixture.alive=$true
$script:Fixture.data.running=$true
$script:Fixture.data.paused=$false
$script:Fixture.launcher=$null
Request-PanelExit
Assert-Panel ($script:ExitRequested -and $script:Operation.action -eq 'disconnect') 'Tray exit did not request backend stop first'
$script:ExitCompleted=$false
$script:ExitShowRequested=$false
function Complete-PanelExit { $script:ExitCompleted=$true }
function Show-TappoPanel { $script:ExitShowRequested=$true }
$script:Fixture.alive=$false
$script:Fixture.data.running=$false
$script:Fixture.data.normalStartupRestored=$false
$script:Fixture.data.automationDisconnected=$true
$script:Fixture.data.at=[DateTimeOffset]::UtcNow
Update-Panel
Assert-Panel ($script:ExitCompleted -and -not $script:ExitRequested) 'Confirmed tray exit did not finish'
$script:ExitCompleted=$false
$script:Fixture.alive=$true
$script:Fixture.data.running=$true
Request-PanelExit
$script:Fixture.alive=$false
$script:Fixture.data.running=$false
$script:Fixture.data.normalStartupRestored=$false
$script:Fixture.data.automationDisconnected=$false
$script:Fixture.data.at=[DateTimeOffset]::UtcNow
$script:Operation.process.HasExited=$true
$script:Operation.process.ExitCode=1
Update-Panel
Assert-Panel (-not $script:ExitCompleted -and $script:ExitShowRequested) 'Unconfirmed disconnect silently exited the helper'
Write-Output "Panel smoke checks passed: $script:SmokeChecks; no backend commands executed."
