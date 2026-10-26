function Show-TappoPanel {
    $script:Window.ShowInTaskbar=$true
    $script:Window.Show()
    $script:Window.WindowState='Normal'
    [void]$script:Window.Activate()
    Write-PanelLifecycle 'restored'
}

function Hide-TappoPanel {
    $script:Window.Hide()
    $script:Window.ShowInTaskbar=$false
    Write-PanelLifecycle 'hidden-to-tray'
    if ($script:TrayIcon -and -not $script:TrayTipShown) {
        $script:TrayIcon.ShowBalloonTip(3000,'Wuwa Tappo 助手','面板已收进托盘，后台状态不变。右键托盘图标可暂停或退出。',[Windows.Forms.ToolTipIcon]::Info)
        $script:TrayTipShown=$true
    }
}

function Complete-PanelExit {
    $script:AllowExit=$true
    if ($script:Application) { $script:Application.Shutdown() } else { $script:Window.Close() }
}

function Request-PanelExit {
    try { $view=Read-View }
    catch { $script:Notice='无法确认后台状态，请核对后再退出。'; Show-TappoPanel; return }
    if ($script:Operation) { $script:Notice='正在处理操作，请等待完成后退出。'; Show-TappoPanel; return }
    if ($view.alive) {
        $script:ExitRequested=$true
        Invoke-PanelAction 'disconnect'
        if (-not $script:Operation) { $script:ExitRequested=$false; Show-TappoPanel }
    } else { Complete-PanelExit }
}

function Update-PanelTray($View) {
    if (-not $script:TrayIcon) { return }
    $label=if (-not $View.alive) {'未运行'} elseif ($View.stale) {'状态未更新'} elseif ($View.data.fault -or $View.pending) {'需处理'} elseif ($View.data.paused) {'已暂停'} else {'运行中'}
    $script:TrayIcon.Text="Wuwa Tappo 助手 · $label"
    $script:TrayStatus.Text="后台：$label"
    $script:TrayPause.Text=if ($View.data.paused) {'继续运行'} else {'暂停自动化'}
    $script:TrayPause.Enabled=$View.alive -and -not $script:Operation -and -not $View.pending
    $script:TrayStop.Enabled=$View.alive -and -not $script:Operation
    $script:TrayExit.Enabled=-not $script:Operation
    $script:TrayExit.Text=if ($View.alive) {'停止自动化并退出助手'} else {'退出助手'}
}

function Initialize-PanelTray {
    $script:Application=[Windows.Application]::new()
    $script:Application.ShutdownMode=[Windows.ShutdownMode]::OnExplicitShutdown
    $script:TrayMenu=[Windows.Forms.ContextMenuStrip]::new()
    $script:TrayStatus=$script:TrayMenu.Items.Add('后台：未运行'); $script:TrayStatus.Enabled=$false
    $open=$script:TrayMenu.Items.Add('打开控制面板'); $open.Add_Click({ Show-TappoPanel })
    $script:TrayPause=$script:TrayMenu.Items.Add('暂停自动化')
    $script:TrayPause.Add_Click({ if ($script:View.data.paused) { Invoke-PanelAction 'resume' } else { Invoke-PanelAction 'pause' } })
    $script:TrayStop=$script:TrayMenu.Items.Add('停止并恢复普通桌宠'); $script:TrayStop.Add_Click({ Invoke-PanelAction 'stop' })
    [void]$script:TrayMenu.Items.Add([Windows.Forms.ToolStripSeparator]::new())
    $script:TrayExit=$script:TrayMenu.Items.Add('退出助手'); $script:TrayExit.Add_Click({ Request-PanelExit })
    $script:TrayIcon=[Windows.Forms.NotifyIcon]::new()
    $script:TrayImage=[Drawing.Icon]::new((Join-Path $script:Root 'assets\helper.ico'))
    $script:TrayIcon.Icon=$script:TrayImage
    $script:TrayIcon.ContextMenuStrip=$script:TrayMenu
    $script:TrayIcon.Text='Wuwa Tappo 助手'
    $script:TrayIcon.Add_DoubleClick({ Show-TappoPanel })
    $script:TrayIcon.Visible=$true
    $script:Window.Add_Closing({ param($sender,$eventArgs)
        if (-not $script:AllowExit) { $eventArgs.Cancel=$true; Hide-TappoPanel }
    })
}

function Dispose-PanelTray {
    if ($script:TrayIcon) { $script:TrayIcon.Visible=$false; $script:TrayIcon.Dispose(); $script:TrayIcon=$null }
    if ($script:TrayMenu) { $script:TrayMenu.Dispose() }
    if ($script:TrayImage) { $script:TrayImage.Dispose() }
}
