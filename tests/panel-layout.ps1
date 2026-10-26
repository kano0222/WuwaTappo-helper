# Render simulated states only; never dispatch commands or alter account records.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'Panel.ps1') -RenderPath (Join-Path $root 'runtime\panel-preview.png')
$fixtureDirectory=Join-Path ([IO.Path]::GetTempPath()) ('tappo-layout-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixtureDirectory)
$script:Runtime=$fixtureDirectory
$now=[DateTimeOffset]::UtcNow
$script:LayoutView=@{alive=$true;stale=$false;pending=$false;launcher=$null;data=[pscustomobject]@{
    at=$now;running=$true;paused=$false;fault=$null;mode='brute';claimsThisRun=2
    state=[pscustomobject]@{loggedIn=$true;available=1650;cumulative=123456;left=$now.ToUnixTimeMilliseconds()+650000;right=$now.ToUnixTimeMilliseconds()-1000;buttons=@{left=$false;right=$true}}
}}
$script:LayoutView.data | Add-Member -NotePropertyName chestTimes -NotePropertyValue ([pscustomobject]@{updatedAt=$now.ToUnixTimeMilliseconds();left=$script:LayoutView.data.state.left;right=$script:LayoutView.data.state.right;error=$null})
function Read-View { return $script:LayoutView }
function Assert-Layout($Condition,[string]$Message) { if (-not $Condition) { throw $Message } }
function Render-Layout([string]$Name,[int]$Width=960,[int]$Height=900) {
    $surface=$script:Elements.RootSurface
    Update-Panel
    $surface.Measure([Windows.Size]::new($Width,$Height))
    $surface.Arrange([Windows.Rect]::new(0,0,$Width,$Height))
    $surface.UpdateLayout(); Update-Panel; $surface.UpdateLayout()
    $e=$script:Elements
    $start=$e.StartButton.TranslatePoint([Windows.Point]::new(0,0),$surface)
    $pause=$e.PauseButton.TranslatePoint([Windows.Point]::new(0,0),$surface)
    $stop=$e.StopButton.TranslatePoint([Windows.Point]::new(0,0),$surface)
    $stats=$e.StatsArea.TranslatePoint([Windows.Point]::new(0,0),$surface)
    Assert-Layout ($start.Y -eq $pause.Y -and $start.Y -eq $stop.Y -and $stop.Y+$e.StopButton.ActualHeight -lt $stats.Y) 'Controls no longer share a row above statistics'
    Assert-Layout ($stop.X+$e.StopButton.ActualWidth -lt $Width-24) 'Control row overflows'
    Assert-Layout ($e.ChestArea.ActualHeight -lt 100) 'Chest region is too tall'
    foreach ($side in @('Left','Right')) {
        Assert-Layout ($e[$side+'Fill'].Width -ge 0 -and $e[$side+'Fill'].Width -le $e[$side+'Track'].ActualWidth) 'Chest fill overflows'
    }
    if ($Height -eq 900) { Assert-Layout ($e.MainScroll.ScrollableHeight -lt 1) 'Default window requires scrolling' }
    $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($Width,$Height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($surface)
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream=[IO.File]::Create((Join-Path $root "runtime\panel-layout-$Name.png"))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}
try {
    Render-Layout 'running'
    Assert-Layout ($script:Elements.ActivityHeading.Text -eq '最近活动 · 暂无记录' -and $script:Elements.ActivityText.Visibility -eq 'Collapsed') 'Empty activity is not a single line'
    Assert-Layout ($script:Elements.LeftTime.Text -match '^\d{2}:\d{2}$' -and $script:Elements.RightTime.Text -eq '已就绪') 'Connected progress not displayed'
    $script:LayoutView.data.paused=$true
    @(@{at=$now.ToString('o');event='started'},@{at=$now.ToString('o');event='boost-confirmed';delta=100},@{at=$now.ToString('o');event='claim-confirmed';side='left'}) | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content -LiteralPath (Join-Path $fixtureDirectory 'events.jsonl')
    Render-Layout 'paused'
    Assert-Layout ($script:Elements.ActivityText.Visibility -eq 'Visible' -and $script:Elements.ActivityText.Text.Split("`n").Count -eq 3) 'Activity records lost'
    $script:LayoutView.alive=$false; $script:LayoutView.data.running=$false
    Render-Layout 'disconnected'
    Assert-Layout ($script:Elements.LeftTime.Text -eq '未连接' -and $script:Elements.LeftFill.Width -eq 0) 'Disconnected progress not empty'
    $script:Elements.UsageHint.IsExpanded=$true
    Set-PanelAppPath ('C:\Games\'+('Long installation path\'*8)+'WuwaTappo.exe')
    Render-Layout 'minimum-expanded' 820 660
    Assert-Layout ($script:Elements.MainScroll.ScrollableHeight -gt 0) 'Small window cannot scroll expanded guidance'
    $script:Elements.MainScroll.ScrollToEnd(); $script:Elements.RootSurface.UpdateLayout()
    $activity=$script:Elements.ActivityArea.TranslatePoint([Windows.Point]::new(0,$script:Elements.ActivityArea.ActualHeight),$script:Elements.RootSurface)
    $footer=$script:Elements.FooterHint.TranslatePoint([Windows.Point]::new(0,0),$script:Elements.RootSurface)
    Assert-Layout ($activity.Y -le $footer.Y) 'Activity is inaccessible behind footer'
    Write-Output 'Layout checks passed: running, paused, disconnected, countdown, empty/three activities, expanded help, 960x900 and 820x660.'
} finally {
    $log=Join-Path $fixtureDirectory 'events.jsonl'
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log }
    Remove-Item -LiteralPath $fixtureDirectory
}
