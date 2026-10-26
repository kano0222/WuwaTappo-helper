param([string]$RenderPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing
. (Join-Path $PSScriptRoot 'scripts\App-Discovery.ps1')
. (Join-Path $PSScriptRoot 'scripts\Panel-Tray.ps1')
$script:Root = $PSScriptRoot
$script:Runtime = Join-Path $PSScriptRoot 'runtime'
$script:Operation = $null
$script:Notice = $null
$script:View = $null
$script:Timer = $null
$script:PanelMutex = $null
$script:ShowEvent = $null
$script:TrayIcon = $null
$script:Application = $null
$script:AllowExit = $false
$script:ExitRequested = $false
[void][IO.Directory]::CreateDirectory($script:Runtime)
function Write-PanelLifecycle([string]$Phase) {
    if ($RenderPath) { return }
    try { [IO.File]::AppendAllText((Join-Path $script:Runtime 'panel-lifecycle.log'), ([DateTimeOffset]::Now.ToString('o') + " pid=$PID $Phase`n")) } catch { }
}

if (-not $RenderPath) {
    Write-PanelLifecycle 'starting'
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $suffix = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$PSScriptRoot|$userSid"))).Substring(0,16)
    $script:PanelMutex = [Threading.Mutex]::new($false, "Local\TappoPanel-$suffix")
    $script:ShowEvent = [Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::AutoReset,"Local\TappoPanelShow-$suffix")
    try { $ownsPanel=$script:PanelMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsPanel=$true }
    if (-not $ownsPanel) { [void]$script:ShowEvent.Set(); exit 0 }
}

[xml]$markup = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Wuwa Tappo · 后台助手" Width="960" Height="900" MinWidth="820" MinHeight="660"
        WindowStartupLocation="CenterScreen" WindowStyle="None" AllowsTransparency="True"
        Background="Transparent" ResizeMode="CanResizeWithGrip" FontFamily="Microsoft YaHei UI" FontSize="13">
  <Window.Resources>
    <SolidColorBrush x:Key="Ink" Color="#302C28"/>
    <SolidColorBrush x:Key="Muted" Color="#81796F"/>
    <Style TargetType="ScrollBar">
      <Setter Property="Width" Value="8"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
        <Track x:Name="PART_Track" Orientation="Vertical" IsDirectionReversed="True" Margin="1,5">
          <Track.DecreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageUpCommand}" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
          <Track.Thumb><Thumb Background="#D9D1C5"><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="{TemplateBinding Background}" CornerRadius="3"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
          <Track.IncreaseRepeatButton><RepeatButton Command="{x:Static ScrollBar.PageDownCommand}" Focusable="False"><RepeatButton.Template><ControlTemplate TargetType="RepeatButton"><Border Background="Transparent"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
        </Track>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType="Button" x:Key="ButtonBase">
      <Setter Property="Height" Value="44"/><Setter Property="Padding" Value="20,0"/>
      <Setter Property="Background" Value="White"/><Setter Property="Foreground" Value="#4D443B"/>
      <Setter Property="BorderBrush" Value="#E6E0D8"/><Setter Property="BorderThickness" Value="1"/>
      <Setter Property="Cursor" Value="Hand"/><Setter Property="FontWeight" Value="SemiBold"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
        <Border x:Name="Surface" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="11" Padding="{TemplateBinding Padding}">
          <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#F5F1EB"/></Trigger>
          <Trigger Property="IsPressed" Value="True"><Setter TargetName="Surface" Property="Opacity" Value="0.75"/></Trigger>
          <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.38"/><Setter Property="Cursor" Value="Arrow"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType="Button" BasedOn="{StaticResource ButtonBase}"/>
    <Style TargetType="Button" x:Key="Primary" BasedOn="{StaticResource ButtonBase}">
      <Setter Property="Background" Value="#EDB65E"/><Setter Property="BorderBrush" Value="#EDB65E"/>
      <Setter Property="Foreground" Value="#382917"/>
    </Style>
    <Style x:Key="ModeOption" TargetType="ListBoxItem">
      <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
      <Setter Property="Margin" Value="0,0,8,0"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ListBoxItem">
        <Border x:Name="OptionSurface" Background="#F8F7F4" BorderBrush="#EAE5DE" BorderThickness="1" CornerRadius="10" Padding="13,10">
          <Grid>
            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="18"/></Grid.ColumnDefinitions>
            <ContentPresenter VerticalAlignment="Center"/>
            <Grid Grid.Column="1" Width="15" Height="15" VerticalAlignment="Top" Margin="0,2,0,0">
              <Ellipse x:Name="OptionRing" Stroke="#D5CEC3" StrokeThickness="1"/>
              <Ellipse x:Name="OptionDot" Fill="#A5702E" Width="7" Height="7" Opacity="0"/>
            </Grid>
          </Grid>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="OptionSurface" Property="Background" Value="#F1EDE6"/></Trigger>
          <Trigger Property="IsSelected" Value="True">
            <Setter TargetName="OptionSurface" Property="Background" Value="#FFF3DC"/>
            <Setter TargetName="OptionSurface" Property="BorderBrush" Value="#DEB46D"/>
            <Setter TargetName="OptionRing" Property="Stroke" Value="#A5702E"/>
            <Setter TargetName="OptionDot" Property="Opacity" Value="1"/>
          </Trigger>
          <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="OptionSurface" Property="BorderBrush" Value="#A5702E"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style TargetType="TextBlock"><Setter Property="Foreground" Value="{StaticResource Ink}"/></Style>
    <Style TargetType="Border" x:Key="Card">
      <Setter Property="CornerRadius" Value="16"/><Setter Property="Background" Value="White"/>
      <Setter Property="BorderBrush" Value="#EAE5DE"/><Setter Property="BorderThickness" Value="1"/>
      <Setter Property="Padding" Value="22"/>
    </Style>
  </Window.Resources>
  <Border x:Name="RootSurface" Background="#F7F6F2" BorderBrush="#DDD7CE" BorderThickness="1" CornerRadius="20">
    <Grid>
      <Grid.RowDefinitions><RowDefinition Height="46"/><RowDefinition Height="*"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
      <Grid Margin="20,7,12,0">
        <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="38"/><ColumnDefinition Width="38"/></Grid.ColumnDefinitions>
        <Grid x:Name="TitleGrip" Background="Transparent">
          <TextBlock Text="WUWA TAPPO  /  后台助手" FontSize="10" FontWeight="SemiBold" Foreground="#918678" VerticalAlignment="Center"/>
        </Grid>
        <Button x:Name="MinimizeButton" Grid.Column="1" Content="—" Height="30" Padding="0" BorderThickness="0" Background="Transparent" ToolTip="最小化"/>
        <Button x:Name="CloseButton" Grid.Column="2" Content="×" FontSize="20" Height="30" Padding="0" BorderThickness="0" Background="Transparent" ToolTip="收进系统托盘，后台状态不变；右键托盘图标可退出助手"/>
      </Grid>
      <ScrollViewer x:Name="MainScroll" Grid.Row="1" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled" Padding="24,6,24,16">
        <StackPanel>
          <Grid Margin="0,0,0,12">
            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
            <StackPanel>
              <TextBlock Text="桌宠控制台" FontSize="25" FontWeight="Bold"/>
            </StackPanel>
            <Border x:Name="StatusPill" Grid.Column="1" VerticalAlignment="Top" Background="#EAE7E1" CornerRadius="14" Padding="12,5" Margin="0,2,0,0">
              <TextBlock x:Name="StatusLabel" Text="未运行" FontSize="12" FontWeight="SemiBold"/>
            </Border>
          </Grid>
          <Border x:Name="StatusArea" Style="{StaticResource Card}" Padding="16,12" Margin="0,0,0,12" Background="#F0EBE2" BorderThickness="0">
            <StackPanel>
              <TextBlock x:Name="MainMessage" FontWeight="SemiBold" TextWrapping="Wrap"/>
              <TextBlock x:Name="DetailMessage" Foreground="#807565" FontSize="12" TextWrapping="Wrap" Margin="0,5,0,0" LineHeight="21"/>
              <Grid Margin="0,6,0,0">
                <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="12"/><ColumnDefinition Width="90"/></Grid.ColumnDefinitions>
                <TextBlock x:Name="AppPathText" Text="桌宠：启动时自动查找，也可手动选择" FontSize="11" Foreground="#9B9186" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                <Button x:Name="BrowseAppButton" Grid.Column="2" Content="选择程序" Height="28" Padding="8,0" FontSize="11"/>
              </Grid>
          <Grid Margin="0,10,0,0">
            <Grid.ColumnDefinitions><ColumnDefinition Width="150"/><ColumnDefinition Width="12"/><ColumnDefinition Width="140"/><ColumnDefinition Width="12"/><ColumnDefinition Width="180"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
            <Button x:Name="StartButton" Content="启动后台" Style="{StaticResource Primary}"/>
            <Button x:Name="PauseButton" Grid.Column="2" Content="暂停"/>
            <Button x:Name="StopButton" Grid.Column="4" Content="停止并恢复桌宠"/>
            <TextBlock x:Name="ActionHint" Grid.Column="5" Foreground="#9B9186" FontSize="11" Text="每秒刷新" VerticalAlignment="Center" HorizontalAlignment="Right"/>
          </Grid>
              <Expander x:Name="UsageHint" Header="使用说明" IsExpanded="False" FontSize="12" Foreground="#856B46" Margin="0,8,0,0">
                <TextBlock Text="暂停／继续无需重启桌宠；停止并恢复会重启为普通桌宠。退出助手不启动或关闭桌宠，调试入口保留至桌宠退出。首次启用前先从桌宠菜单退出，再启动后台，并按需允许管理员提示。" TextWrapping="Wrap" Foreground="#807565" LineHeight="20" Margin="22,6,0,0"/>
              </Expander>
            </StackPanel>
          </Border>
          <Grid x:Name="StatsArea" Margin="0,0,0,12">
            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="*"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
            <Border Style="{StaticResource Card}" Padding="16,12" Background="#FFF4DF" BorderBrush="#F0DEC0" Margin="0,0,12,0">
              <StackPanel><TextBlock Text="可用次数" Foreground="#9A784A"/><TextBlock x:Name="AvailableValue" Text="—" FontSize="32" FontFamily="Bahnschrift" Margin="0,4,0,2"/><TextBlock Text="每箱消耗 1,000 · 储备目标 2,000" FontSize="11" Foreground="#A38A67"/></StackPanel>
            </Border>
            <Border Grid.Column="1" Style="{StaticResource Card}" Padding="16,12" Margin="0,0,12,0">
              <StackPanel><TextBlock Text="累计次数" Foreground="{StaticResource Muted}"/><TextBlock x:Name="TotalValue" Text="—" FontSize="32" FontFamily="Bahnschrift" Margin="0,4,0,2"/><TextBlock x:Name="CountCaption" Text="等待首次读取" FontSize="11" Foreground="#A49B90"/></StackPanel>
            </Border>
            <Border Grid.Column="2" Style="{StaticResource Card}" Padding="16,12">
              <StackPanel><TextBlock x:Name="ClaimsCaption" Text="本次运行领取" Foreground="{StaticResource Muted}"/><TextBlock x:Name="ClaimsValue" Text="—" FontSize="32" FontFamily="Bahnschrift" Margin="0,4,0,2"/><TextBlock Text="后台重新启动后清零" FontSize="11" Foreground="#A49B90"/></StackPanel>
            </Border>
          </Grid>
          <Border x:Name="ChestArea" Style="{StaticResource Card}" Padding="16,12" Margin="0,0,0,12">
            <StackPanel>
              <Grid Margin="0,0,0,12"><TextBlock Text="宝箱进度" FontWeight="SemiBold"/><TextBlock x:Name="SnapshotLabel" Text="等待连接" Foreground="#9B9186" FontSize="11" HorizontalAlignment="Right" VerticalAlignment="Center"/></Grid>
              <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                <StackPanel Grid.Column="0" Margin="0,0,16,0">
                  <StackPanel.ToolTip><StackPanel><TextBlock x:Name="LeftTag"/><TextBlock x:Name="LeftHint"/></StackPanel></StackPanel.ToolTip>
                  <Grid Margin="0,0,0,8">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                    <TextBlock Text="宝箱一" FontWeight="SemiBold"/>
                    <TextBlock x:Name="LeftTime" Grid.Column="1" Text="未连接" HorizontalAlignment="Right" Foreground="#8E7858"/>
                  </Grid>
                  <Border x:Name="LeftTrack" Height="6" Background="#EFEAE1" CornerRadius="3" ClipToBounds="True"><Border x:Name="LeftFill" Background="#DFB367" HorizontalAlignment="Left" Width="0" CornerRadius="3"/></Border>
                </StackPanel>
                <StackPanel Grid.Column="1" Margin="16,0,0,0">
                  <StackPanel.ToolTip><StackPanel><TextBlock x:Name="RightTag"/><TextBlock x:Name="RightHint"/></StackPanel></StackPanel.ToolTip>
                  <Grid Margin="0,0,0,8">
                    <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                    <TextBlock Text="宝箱二" FontWeight="SemiBold"/>
                    <TextBlock x:Name="RightTime" Grid.Column="1" Text="未连接" HorizontalAlignment="Right" Foreground="#8E7858"/>
                  </Grid>
                  <Border x:Name="RightTrack" Height="6" Background="#EFEAE1" CornerRadius="3" ClipToBounds="True"><Border x:Name="RightFill" Background="#DFB367" HorizontalAlignment="Left" Width="0" CornerRadius="3"/></Border>
                </StackPanel>
              </Grid>
            </StackPanel>
          </Border>
          <Border Style="{StaticResource Card}" Padding="16,12" Margin="0,0,0,12">
            <StackPanel>
              <Grid Margin="0,0,0,8">
                <TextBlock Text="次数增加模式" FontWeight="SemiBold"/>
                <TextBlock Text="选择即保存" FontSize="11" Foreground="#A49B90" HorizontalAlignment="Right"/>
              </Grid>
              <ListBox x:Name="ModeSelector" SelectedIndex="0" SelectionMode="Single" Background="Transparent" BorderThickness="0" ItemContainerStyle="{StaticResource ModeOption}" KeyboardNavigation.DirectionalNavigation="Contained">
                <ListBox.Template><ControlTemplate TargetType="ListBox"><ItemsPresenter/></ControlTemplate></ListBox.Template>
                <ListBox.ItemsPanel><ItemsPanelTemplate><UniformGrid Columns="3"/></ItemsPanelTemplate></ListBox.ItemsPanel>
                <ListBoxItem Tag="brute" AutomationProperties.Name="暴力模式，到期一次补足">
                  <StackPanel><TextBlock Text="暴力模式" FontWeight="SemiBold"/><TextBlock Text="到期一次补足" FontSize="11" Foreground="#9B8870" Margin="0,5,0,0"/></StackPanel>
                </ListBoxItem>
                <ListBoxItem Tag="clicker" AutomationProperties.Name="连点器模式，提前累积，每秒最多100次">
                  <StackPanel><TextBlock Text="连点器模式" FontWeight="SemiBold"/><TextBlock Text="提前累积 · 100 次/秒" FontSize="11" Foreground="#9B8870" Margin="0,5,0,0"/></StackPanel>
                </ListBoxItem>
                <ListBoxItem Tag="human" Margin="0" AutomationProperties.Name="人类模式，提前累积，波动速率与间歇">
                  <StackPanel><TextBlock Text="人类模式" FontWeight="SemiBold"/><TextBlock Text="提前累积 · 波动速率" FontSize="11" Foreground="#9B8870" Margin="0,5,0,0"/></StackPanel>
                </ListBoxItem>
              </ListBox>
              <TextBlock x:Name="ModeHint" FontSize="11" Foreground="#9B9186" TextWrapping="Wrap" Margin="0,10,0,0"/>
            </StackPanel>
          </Border>
          <Border x:Name="ActivityArea" Style="{StaticResource Card}" Padding="16,12">
            <StackPanel>
              <TextBlock x:Name="ActivityHeading" Text="最近活动 · 暂无记录" FontWeight="SemiBold"/>
              <TextBlock x:Name="ActivityText" FontSize="12" Foreground="#81796F" LineHeight="22" TextWrapping="Wrap" Margin="0,6,0,0"/>
            </StackPanel>
          </Border>
        </StackPanel>
      </ScrollViewer>
      <Border x:Name="FooterHint" Grid.Row="2" Padding="24,9,24,10" Background="#F7F6F2" BorderBrush="#EAE5DE" BorderThickness="0,1,0,0" CornerRadius="0,0,20,20">
        <TextBlock Text="关闭窗口收进托盘，后台保持运行；完全退出请右键托盘图标 → 退出助手。" FontSize="11" Foreground="#A49B90" TextWrapping="Wrap" TextAlignment="Left" HorizontalAlignment="Left" MaxWidth="650"/>
      </Border>
    </Grid>
  </Border>
</Window>
'@
$reader = [Xml.XmlNodeReader]::new($markup)
$script:Window = [Windows.Markup.XamlReader]::Load($reader)
Write-PanelLifecycle 'window-created'
$script:Window.Add_ContentRendered({ Write-PanelLifecycle 'visible' })
$script:Window.Add_Closed({ Write-PanelLifecycle 'closed' })
if (-not $RenderPath) { $script:Window.Height=[Math]::Max(660,[Math]::Min(900,[Windows.SystemParameters]::WorkArea.Height-40)) }
$script:Elements = @{}
foreach ($name in @('RootSurface','TitleGrip','MinimizeButton','CloseButton','StatusPill','StatusLabel','MainMessage','DetailMessage','AvailableValue','TotalValue','ClaimsValue','ClaimsCaption','CountCaption','SnapshotLabel','LeftTag','LeftTime','LeftTrack','LeftFill','LeftHint','RightTag','RightTime','RightTrack','RightFill','RightHint','StartButton','PauseButton','StopButton','ActionHint','ActivityText','ModeSelector','ModeHint','AppPathText','BrowseAppButton','FooterHint','MainScroll','UsageHint','StatusArea','StatsArea','ChestArea','ActivityArea','ActivityHeading')) {
    $script:Elements[$name] = $script:Window.FindName($name)
}
function Read-JsonFile([string]$Name) {
    $location = Join-Path $script:Runtime $Name
    if (Test-Path -LiteralPath $location) {
        # The backend atomically replaces status.json. Readers must allow replacement on Windows.
        $stream=[IO.File]::Open($location,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $reader=[IO.StreamReader]::new($stream,[Text.Encoding]::UTF8,$true)
        try { $text=$reader.ReadToEnd() } finally { $reader.Dispose() }
        return ($text | ConvertFrom-Json)
    }
    return $null
}
function Save-PanelMode {
    if ($script:UpdatingModeSelection) { return }
    $temporary = Join-Path $script:Runtime ([guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $mode = [string]$script:Elements.ModeSelector.SelectedItem.Tag
        if ($mode -notin @('brute','clicker','human')) { throw '请选择有效模式。' }
        [IO.File]::WriteAllText($temporary, (@{ mode=$mode } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination (Join-Path $script:Runtime 'preferences.json') -Force
        $script:SavedMode=$mode
        $script:Notice='模式已保存。'
    } catch {
        $script:Notice='模式保存失败：'+$_.Exception.Message
        $script:UpdatingModeSelection=$true
        try {
            $previousMode=if ($script:SavedMode -in @('brute','clicker','human')) {$script:SavedMode} else {'brute'}
            foreach ($item in $script:Elements.ModeSelector.Items) { if ($item.Tag -eq $previousMode) { $script:Elements.ModeSelector.SelectedItem=$item } }
        } finally { $script:UpdatingModeSelection=$false }
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    Update-Panel
}
function Set-PanelAppPath([string]$Path) {
    $script:Elements.AppPathText.Text='桌宠：'+$Path
    $script:Elements.AppPathText.ToolTip=$Path
}
function Select-PanelApp {
    try {
        $path=Resolve-TappoApp -Root $script:Root -Interactive -Choose -Owner $script:Window
        Set-PanelAppPath $path
        $script:Notice='桌宠程序已保存。'
    } catch { $script:Notice=$_.Exception.Message }
    Update-Panel
}
function Local-Time($Value) {
    return ([DateTimeOffset]$Value).ToOffset([TimeSpan]::FromHours(8))
}
function Read-View {
    $data = Read-JsonFile 'status.json'
    $launcher = Read-JsonFile 'launcher-status.json'
    $alive = $false
    if ($data -and $data.running) { $alive = [bool](Get-Process -Id $data.pid -ErrorAction SilentlyContinue) }
    $age = if ($data) { ([DateTimeOffset]::UtcNow - [DateTimeOffset]$data.at).TotalSeconds } else { [double]::PositiveInfinity }
    return @{ data=$data; launcher=$launcher; alive=$alive; stale=($alive -and $age -gt 45); pending=(Test-Path -LiteralPath (Join-Path $script:Runtime 'pending.json')) }
}
function Complete-Operation($View) {
    $op = $script:Operation
    if (-not $op) { return }
    if ($View.launcher -and $View.launcher.error -and [DateTimeOffset]$View.launcher.failedAt -ge $op.started) {
        $script:Notice=$View.launcher.error; $script:Operation=$null; return
    }
    if ($op.process.HasExited -and $op.process.ExitCode -ne 0) {
        $script:Notice = if ($View.data.fault) { $View.data.fault } elseif (-not $View.alive -and $op.action -ne 'start') { '后台进程已退出。请核对桌宠状态，正常退出后再重新启动助手。' } else { '操作未完成，请核对桌宠和后台状态。详细信息已保存至操作日志。' }
        $script:Operation=$null; return
    }
    $done = switch ($op.action) {
        'start' { $View.alive }
        'pause' { $View.alive -and $View.data.paused }
        'resume' { $View.alive -and -not $View.data.paused }
        'stop' { $View.data -and -not $View.alive -and -not $View.data.running -and [DateTimeOffset]$View.data.at -ge $op.started }
        'disconnect' { $View.data -and -not $View.alive -and -not $View.data.running -and $View.data.automationDisconnected -and [DateTimeOffset]$View.data.at -ge $op.started }
    }
    if ($done) {
        $script:Notice = if ($op.action -eq 'stop' -and -not $View.data.normalStartupRestored) { '后台已停止，但桌宠未恢复普通启动，请查看状态说明。' } else { $null }
        $script:Operation=$null
    } elseif (([DateTimeOffset]::UtcNow-$op.started).TotalSeconds -gt 75) {
        $script:Notice='操作尚未确认完成。请检查是否有待处理的管理员提示或桌宠窗口；不要重复点击启动。'
        # Keep controls locked while the command process itself is still running.
        if ($op.process.HasExited) { $script:Operation=$null }
    }
}
function Update-Chest([string]$Prefix,[string]$Side,$View) {
    $e=$script:Elements; $state=$View.data.state
    if (-not $View.alive -or $View.stale -or -not $state -or -not $state.loggedIn) {
        $e[$Prefix+'Time'].Text='未连接'; $e[$Prefix+'Tag'].Text='未连接'
        $e[$Prefix+'Hint'].Text=''; $e[$Prefix+'Fill'].Width=0; return
    }
    $clock=$View.data.chestTimes
    $now=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    if (-not $clock -or $null -eq $clock.updatedAt -or $clock.error -or ($now-$clock.updatedAt) -gt 90000 -or ($now-$clock.updatedAt) -lt 0) {
        $e[$Prefix+'Time'].Text='等待同步'; $e[$Prefix+'Tag'].Text='接口时间未就绪'
        $e[$Prefix+'Hint'].Text=if ($clock.error) {$clock.error} else {'等待服务端宝箱时间，暂不显示旧倒计时'}
        $e[$Prefix+'Fill'].Width=0; return
    }
    $remaining=[Math]::Max(0,[Math]::Ceiling(($clock.$Side-$now)/1000))
    if ($remaining -gt 0) {
        $e[$Prefix+'Time'].Text=('{0:00}:{1:00}' -f [Math]::Floor($remaining/60),($remaining%60))
        $e[$Prefix+'Tag'].Text='计时中'; $e[$Prefix+'Hint'].Text='服务端冷却时间，每 30 秒同步；显示每秒更新'
        $progress=[Math]::Clamp([double](1-$remaining/1800),[double]0,[double]1)
    } else {
        $ready=[bool]$state.buttons.$Side
        $e[$Prefix+'Time'].Text=if ($ready) {'已就绪'} else {'冷却已结束'}
        $e[$Prefix+'Tag'].Text=if ($ready) {'待领取'} else {'等待同步'}
        $e[$Prefix+'Hint'].Text=if ($View.data.paused) {'恢复运行后继续检查领取'} else {'由后台核对并按顺序领取'}
        $progress=1
    }
    $e[$Prefix+'Fill'].Width=[Math]::Max([double]0,[double]($e[$Prefix+'Track'].ActualWidth*$progress))
}
function Update-Panel {
    try {
        $v=Read-View; Complete-Operation $v; $script:View=$v
        $e=$script:Elements; $s=$v.data; $busy=[bool]$script:Operation
        $preference=Read-JsonFile 'preferences.json'
        $selectedMode=if ($preference.mode -in @('brute','clicker','human')) {$preference.mode} else {'brute'}
        $modeName=@{brute='暴力模式';clicker='连点器模式';human='人类模式'}[$selectedMode]
        $modeBehavior=if ($selectedMode -eq 'brute') {'需要补数时一次补足至 2,000 次。'} else {'提前累积至 2,000 次，到期后逐个领取。'}
        $e.ModeHint.Text=if (-not $v.alive) {"已选择：$modeName，启动后台后生效。$modeBehavior"} elseif (-not $s.mode) {"已保存：$modeName。当前后台需停止并重新启动后才能使用模式选择。"} elseif ($s.mode -ne $selectedMode) {"已保存：$modeName，等待后台应用。"} elseif ($s.paused -or $s.fault -or $v.pending) {"已应用：$modeName，当前已暂停或正在确认操作。"} else {"已应用：$modeName。$modeBehavior"}
        $label='未运行'; $color='#EAE7E1'; $message='后台助手尚未运行'; $detail='先退出普通启动的桌宠，再点击“启动后台”。'
        if ($v.alive) {
            $label='运行中'; $color='#E2EFDD'; $message='后台正在照看桌宠'; $detail='自动补足领取所需次数，并按原有冷却检查宝箱。'
            if ($s.paused) { $label='已暂停'; $color='#FFF0D1'; $message='自动操作已暂停'; $detail='计数与领取已暂停，连接仍保留。可以继续运行，或停止并恢复普通桌宠。' }
            if ($v.pending -and -not $s.paused -and -not $s.fault) { $label='处理中'; $color='#FFF0D1'; $message='正在确认操作结果'; $detail='等待桌宠与服务器同步，期间不会重复提交。' }
            if ($s.fault -or ($v.pending -and $s.paused)) { $label='需处理'; $color='#F8E0D8'; $message='后台已暂停，等待确认'; $detail=if ($s.fault) {$s.fault} else {'上次操作结果待确认，请先停止并核对桌宠，避免重复领取。'} }
            if ($v.stale) { $label='状态未更新'; $color='#FFF0D1'; $message='暂时没有收到新的后台状态'; $detail='可能正在等待应用响应。以下数值是上次记录，不代表当前状态。' }
        } elseif ($s -and $s.normalStartupRestored) { $message='桌宠已恢复普通运行'; $detail='要重新开启助手，请先从桌宠设置或托盘退出应用，再点击启动。' }
        elseif ($s -and $s.running) { $label='已离线'; $message='后台进程已退出'; $detail='当前显示上次记录。请先正常退出桌宠，再重新启动助手。' }
        if ($script:Operation) {
            $label=switch ($script:Operation.action) {'start' {'正在启动'} 'pause' {'正在暂停'} 'resume' {'正在恢复'} 'stop' {'正在停止'} 'disconnect' {'正在退出'}}
            $message=$label; $detail=if ($script:Operation.action -eq 'start') {'请按需处理 Windows 管理员提示，启动完成后会自动更新。'} else {'正在等待后台完成当前操作，请稍候。'}
        }
        if ($script:Notice) { $detail=$script:Notice }
        elseif (-not $busy -and $v.launcher.error -and (-not $s -or [DateTimeOffset]$v.launcher.failedAt -gt [DateTimeOffset]$s.at)) { $detail=$v.launcher.error }
        $e.StatusLabel.Text=$label; $e.StatusPill.Background=[Windows.Media.BrushConverter]::new().ConvertFromString($color)
        $e.MainMessage.Text=$message; $e.DetailMessage.Text=$detail
        $e.StartButton.IsEnabled=-not $v.alive -and -not $busy
        $e.PauseButton.Content=if ($s -and $s.paused) {'继续运行'} else {'暂停'}
        $e.PauseButton.IsEnabled=$v.alive -and -not $busy -and -not $v.pending
        $e.StopButton.IsEnabled=$v.alive -and -not $busy
        $e.BrowseAppButton.IsEnabled=-not $v.alive -and -not $busy
        $e.ActionHint.Text=if ($busy) {'处理中…'} else {'每秒刷新'}
        if ($s -and $s.state) {
            $e.AvailableValue.Text=([long]$s.state.available).ToString('N0')
            $e.TotalValue.Text=([long]$s.state.cumulative).ToString('N0')
            $e.ClaimsValue.Text=([long]$s.claimsThisRun).ToString('N0')
            $time=Local-Time $s.at
            $e.CountCaption.Text=if ($v.alive -and -not $v.stale) {'来自后台最近一次读取'} else {'上次运行的记录'}
            $e.ClaimsCaption.Text=if ($v.alive) {'本次运行领取'} else {'上次运行领取'}
            $e.SnapshotLabel.Text=$(if ($v.alive -and -not $v.stale) {'更新于 '} else {'上次记录 '})+$time.ToString('MM-dd HH:mm:ss')
        } else {
            $e.AvailableValue.Text='—'; $e.TotalValue.Text='—'; $e.ClaimsValue.Text='—'
            $e.CountCaption.Text='等待首次读取'; $e.ClaimsCaption.Text='本次运行领取'; $e.SnapshotLabel.Text='等待连接'
        }
        $clock=$s.chestTimes
        $clockAge=if ($null -ne $clock.updatedAt) {[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()-$clock.updatedAt} else {$null}
        $e.SnapshotLabel.Text=if (-not $v.alive -or $v.stale) {'等待连接'} elseif ($clock.error) {'宝箱时间查询失败'} elseif ($null -ne $clockAge -and ($clockAge -gt 90000 -or $clockAge -lt 0)) {'宝箱时间已过期'} elseif ($null -ne $clock.updatedAt) {
            '接口同步 '+[DateTimeOffset]::FromUnixTimeMilliseconds([long]$clock.updatedAt).ToOffset([TimeSpan]::FromHours(8)).ToString('HH:mm:ss')
        } else {'等待接口同步'}
        Update-Chest 'Left' 'left' $v; Update-Chest 'Right' 'right' $v
        $logPath=Join-Path $script:Runtime 'events.jsonl'
        $activity=@()
        if (Test-Path -LiteralPath $logPath) {
            $rows=@(Get-Content -LiteralPath $logPath -Encoding utf8 -Tail 12)
            [array]::Reverse($rows)
            foreach ($row in $rows) {
                try { $event=$row | ConvertFrom-Json } catch { continue }
                $text=switch ($event.event) {
                    'started' {'后台已连接'}
                    'claim-confirmed' { '已领取'+$(if ($event.side -eq 'left') {'宝箱一'} else {'宝箱二'})+' · 消耗 1,000 次' }
                    'boost-confirmed' {'已补充 '+$event.delta+' 次'}
                    'paused-on-error' {'后台暂停，等待处理'}
                    'cleanup-needs-attention' {'停止后需要检查桌宠状态'}
                    default {$null}
                }
                if ($text) { $activity+=((Local-Time $event.at).ToString('MM-dd HH:mm')+'    '+$text) }
                if ($activity.Count -ge 3) { break }
            }
        }
        $e.ActivityHeading.Text=if ($activity.Count) {'最近活动'} else {'最近活动 · 暂无记录'}
        $e.ActivityText.Visibility=if ($activity.Count) {'Visible'} else {'Collapsed'}
        $e.ActivityText.Text=$activity -join "`n"
        Update-PanelTray $v
        if ($script:ExitRequested -and -not $script:Operation) {
            $script:ExitRequested=$false
            if (-not $v.alive -and $s.automationDisconnected) { Complete-PanelExit }
            else { $script:Notice='自动化停止尚未确认，请核对状态后再退出。'; Show-TappoPanel }
        }
    } catch {
        $script:Elements.MainMessage.Text='状态暂时无法读取'
        $script:Elements.DetailMessage.Text='正在等待状态文件更新；面板不会因此自动重试领取。'
        $script:Elements.StartButton.IsEnabled=$false; $script:Elements.PauseButton.IsEnabled=$false; $script:Elements.StopButton.IsEnabled=$false
    }
}
function Invoke-PanelAction([string]$Action) {
    if ($script:Operation) { return }
    if ($Action -eq 'start') {
        try { Set-PanelAppPath (Resolve-TappoApp -Root $script:Root -Interactive -Owner $script:Window) }
        catch { $script:Notice=$_.Exception.Message; Update-Panel; return }
    }
    if ($Action -eq 'start' -and (Get-Process -Name WuwaTappo -ErrorAction SilentlyContinue)) {
        $socket=[Net.Sockets.TcpClient]::new()
        try { $task=$socket.ConnectAsync('127.0.0.1',19222); $connected=$task.Wait(200) -and $socket.Connected }
        catch { $connected=$false } finally { $socket.Dispose() }
        if (-not $connected) { $script:Notice='请先从桌宠设置或托盘选择“退出应用”，再点启动。'; Update-Panel; return }
    }
    try {
        $script:Notice=$null
        $shell=(Get-Process -Id $PID).Path
        $target=if ($Action -eq 'start') {'Start.ps1'} else {'Control.ps1'}
        $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',('"'+(Join-Path $script:Root $target)+'"'))
        if ($Action -ne 'start') { $arguments+=@('-Action',$Action) }
        $started=[DateTimeOffset]::UtcNow
        $process=Start-Process -FilePath $shell -ArgumentList $arguments -WorkingDirectory $script:Root -WindowStyle Hidden -RedirectStandardOutput (Join-Path $script:Runtime 'panel-command-output.log') -RedirectStandardError (Join-Path $script:Runtime 'panel-command-error.log') -PassThru
        $script:Operation=@{action=$Action; started=$started; process=$process}
    } catch { $script:Notice='无法执行操作：'+$_.Exception.Message }
    Update-Panel
}

$initialPreference=Read-JsonFile 'preferences.json'
$script:SavedMode=if ($initialPreference.mode -in @('brute','clicker','human')) {$initialPreference.mode} else {'brute'}
foreach ($item in $script:Elements.ModeSelector.Items) { if ($item.Tag -eq $initialPreference.mode) { $script:Elements.ModeSelector.SelectedItem=$item } }
$script:Elements.ModeSelector.Add_SelectionChanged({ Save-PanelMode })
$script:Elements.BrowseAppButton.Add_Click({ Select-PanelApp })
$script:Elements.StartButton.Add_Click({ Invoke-PanelAction 'start' })
$script:Elements.PauseButton.Add_Click({ if ($script:View.data.paused) { Invoke-PanelAction 'resume' } else { Invoke-PanelAction 'pause' } })
$script:Elements.StopButton.Add_Click({ Invoke-PanelAction 'stop' })
$script:Elements.MinimizeButton.Add_Click({ $script:Window.WindowState='Minimized' })
$script:Elements.CloseButton.Add_Click({ $script:Window.Close() })
$script:Elements.TitleGrip.Add_MouseLeftButtonDown({
    if ($_.ClickCount -eq 2) { $script:Window.WindowState=if ($script:Window.WindowState -eq 'Maximized') {'Normal'} else {'Maximized'} }
    else { $script:Window.DragMove() }
})
try {
    if (-not $RenderPath) {
        try { Set-PanelAppPath (Resolve-TappoApp -Root $script:Root) } catch { $script:Elements.AppPathText.Text='桌宠：尚未找到，点击“选择程序”设置' }
        Initialize-PanelTray
    }
    Update-Panel
    if ($RenderPath) {
        $surface=$script:Elements.RootSurface
        $surface.Measure([Windows.Size]::new($script:Window.Width,$script:Window.Height))
        $surface.Arrange([Windows.Rect]::new(0,0,$script:Window.Width,$script:Window.Height))
        $surface.UpdateLayout(); Update-Panel; $surface.UpdateLayout()
        $image=[Windows.Media.Imaging.RenderTargetBitmap]::new([int]$script:Window.Width,[int]$script:Window.Height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
        $image.Render($surface)
        $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
        $stream=[IO.File]::Create($RenderPath)
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
        Write-Output $RenderPath
    } else {
        $script:Timer=[Windows.Threading.DispatcherTimer]::new(); $script:Timer.Interval=[TimeSpan]::FromSeconds(1)
        $script:Timer.Add_Tick({
            if ($script:ShowEvent.WaitOne(0)) { Show-TappoPanel }
            Update-Panel
        })
        $script:Timer.Start(); Write-PanelLifecycle 'run-window'; [void]$script:Application.Run($script:Window)
    }
} finally {
    Write-PanelLifecycle 'cleanup'
    if ($script:Timer) { $script:Timer.Stop() }
    Dispose-PanelTray
    if ($script:ShowEvent) { $script:ShowEvent.Dispose() }
    if ($script:PanelMutex) { $script:PanelMutex.ReleaseMutex(); $script:PanelMutex.Dispose() }
}
