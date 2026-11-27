using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tappo {
    // Isolated production-XAML fixtures: no timer, tray, worker, discovery or real startup writes.
    internal static class ConsoleUiChecks {
        [DllImport("user32.dll",EntryPoint="SendMessageW")]
        static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
        static int checks;
        static void Check(bool pass,string message) {checks++;if(!pass)throw new Exception(message);}
        public static int Run() {
            checks=0;
            var root=Path.Combine(Path.GetTempPath(),"tappo-console-ui-"+Guid.NewGuid().ToString("N"));
            var output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"console-previews");Directory.CreateDirectory(output);
            try {
                var store=new RuntimeStore(root);var now=RuntimeStore.Now;
                ResizeBorders(store);
                CenterViewToggle(new RuntimeStore(Path.Combine(root,"center-view")));
                ViewModes(Path.Combine(root,"view"),output);
                using(var panel=new Panel(store,preview:true)) {
                    Check(panel.Window.Width==880&&panel.Window.Height==846,"default console size does not fit the tightened layout");
                    panel.FitToWorkArea(1080);Check(panel.Window.Height==846,"startup overrides the compact default height");
                    panel.FitToWorkArea(800);Check(panel.Window.Height==760,"startup does not fit a smaller work area");
                    panel.FitToWorkArea(600);Check(panel.Window.Height==660,"startup shrinks below the minimum height");
                    panel.Window.Height=846;
                    Check((string)((ListBoxItem)panel.E<ListBox>("ModeSelector").SelectedItem).Tag=="clicker","new console does not default to clicker mode");
                    foreach(var mode in new[]{"brute","human"}) {
                        var savedStore=new RuntimeStore(Path.Combine(root,"saved-"+mode));savedStore.Write("preferences.json",new Preference {mode=mode});
                        using(var savedPanel=new Panel(savedStore,preview:true))Check((string)((ListBoxItem)savedPanel.E<ListBox>("ModeSelector").SelectedItem).Tag==mode,"default mode overrides a saved selection");
                    }
                    panel.ReadClock=()=>now;panel.DispatchOverride=action=>{throw new Exception("Fixture dispatched automation");};
                    Status status=null;panel.ReadStatus=()=>status;panel.Refresh();
                    Check(panel.E<TextBlock>("AvailableValue").Text=="—"&&panel.E<TextBlock>("ClaimsValue").Text=="—","missing data rendered as zero");
                    Check(panel.E<TextBlock>("TotalValue").Text=="0"&&panel.E<TextBlock>("LeftTime").Text=="--:--"&&panel.E<TextBlock>("RightTime").Text=="--:--"&&panel.E<TextBlock>("SnapshotLabel").Text=="启动后同步","initial placeholders disagree");
                    Check(panel.E<TextBlock>("CountCaption").Text=="助手领取的宝箱数"&&panel.E<StackPanel>("ActivityEmpty").Visibility==Visibility.Visible&&panel.E<TextBlock>("ActivityText").Text=="","count hint or empty activity state is incorrect");
                    Check(panel.E<Button>("FeedbackButton").ToolTip==null&&panel.E<Button>("MinimizeButton").ToolTip==null&&panel.E<Button>("CloseButton").ToolTip==null&&panel.E<TextBlock>("TotalValue").ToolTip==null,"removed tooltips remain");
                    Check(panel.E<TextBlock>("AvailableValue").ToolTip==null&&panel.Window.FindName("LeftHint")==null&&panel.Window.FindName("RightHint")==null,"empty count or chest repeats visible state in a tooltip");
                    panel.SetPath(@"C:\Very long installation directory\Another long directory\WuwaTappo\WuwaTappo.exe");
                    Check(panel.E<Button>("BrowseAppButton").ToolTip.ToString()==panel.E<TextBlock>("AppPathText").Text,"path tooltip does not show the full path");
                    Check(ToolTipService.GetIsEnabled(panel.E<Button>("BrowseAppButton")),"path tooltip is disabled");
                    PathToolTip(panel);
                    Render(panel,output,"idle");
                    ScrollContent(panel,output);
                    foreach(var height in new[]{900,940}) {
                        panel.Render(Path.Combine(output,"empty-centered-"+height+".png"),880,height);
                        var body=panel.E<Grid>("ActivityBody");var empty=panel.E<StackPanel>("ActivityEmpty");var point=empty.TranslatePoint(new Point(),body);
                        Check(body.ActualHeight>empty.ActualHeight&&Math.Abs(point.Y*2+empty.ActualHeight-body.ActualHeight)<1,"empty activity is not centered in the visible body");
                    }
                    RunButtonStates(panel,output,"start");
                    panel.Render(Path.Combine(output,"idle-width-880.png"),880,940);
                    string command=null;panel.DispatchOverride=action=>command=action;
                    panel.E<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(command=="start","initial run button did not start the background");
                    var state=Tests.State();state.available=0;state.cumulative=0;state.left=now+600000;state.right=now+900000;
                    status=new Status {running=true,pid=Process.GetCurrentProcess().Id,at=DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("o"),mode="brute",connected=true,state=state,
                        chestTimes=new ChestTimes {updatedAt=now,left=state.left,right=state.right}};
                    panel.Refresh();
                    Check(panel.E<TextBlock>("AvailableValue").Text=="0"&&panel.E<TextBlock>("TotalValue").Text=="0"&&panel.E<TextBlock>("ClaimsValue").Text=="0","known zero is missing");
                    Check(panel.E<Button>("StartButton").Content.ToString()=="停止运行","running button did not offer stop");
                    Check(!panel.E<Button>("BrowseAppButton").IsEnabled,"running worker allows changing its path");
                    bool pathBlocked=false;
                    try {panel.PreparePathChange(@"C:\Another pet\WuwaTappo.exe").GetAwaiter().GetResult();}catch(InvalidOperationException){pathBlocked=true;}
                    Check(pathBlocked,"path change did not reject an active worker");
                    status.paused=true;status.manuallyPaused=true;command=null;
                    panel.E<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(command=="pause","a changed snapshot reversed the displayed stop action");
                    status.paused=false;status.manuallyPaused=false;
                    status.state=null;panel.Refresh();
                    Check(panel.E<TextBlock>("ClaimsValue").Text=="0"&&panel.E<TextBlock>("TotalValue").Text=="0","local history incorrectly depends on pet snapshot");
                    status.state=state;status.claimsThisRun=3;Render(panel,output,"running");
                    Check(panel.E<TextBlock>("AvailableValue").ToolTip==null,"current count shows a redundant read timestamp hint");
                    RunButtonStates(panel,output,"stop");
                    status.paused=true;status.manuallyPaused=true;Render(panel,output,"paused");command=null;
                    panel.E<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(command=="resume"&&panel.E<Button>("StartButton").Content.ToString()=="启动后台","stopped run button started another worker instead of resuming");
                    Check(panel.E<Button>("BrowseAppButton").IsEnabled,"manually stopped worker keeps the path disabled");
                    panel.DispatchOverride=action=>command=action;pathBlocked=false;
                    try {panel.PreparePathChange(@"C:\Another pet\WuwaTappo.exe").GetAwaiter().GetResult();}catch(InvalidOperationException){pathBlocked=true;}
                    Check(pathBlocked&&command=="stop-background","path change accepted an unconfirmed worker exit");
                    panel.DispatchOverride=action=>{command=action;status.running=false;};command=null;
                    panel.PreparePathChange(@"C:\Another pet\WuwaTappo.exe").GetAwaiter().GetResult();
                    Check(command=="stop-background"&&!status.running,"path change did not release the old worker without closing the pet");
                    status.running=true;panel.DispatchOverride=action=>command=action;
                    status.paused=false;status.manuallyPaused=false;
                    status.connected=false;status.disconnectedSince=now;panel.Refresh();
                    Check(panel.E<TextBlock>("AvailableValue").ToolTip==null&&panel.E<TextBlock>("ClaimsValue").Text=="3","disconnect lost session data or restored a tooltip");
                    Check(panel.E<TextBlock>("LeftTime").Text=="连接已断开"&&panel.E<Border>("LeftFill").Width==0,"disconnect shows live countdown");
                    Render(panel,output,"disconnected");
                    var controlHeight=panel.E<Border>("StatusArea").ActualHeight;
                    status.readRetryAt=now+10000;Render(panel,output,"retry");
                    Check(panel.Window.FindName("RetryButton")==null&&panel.E<Border>("StatusArea").ActualHeight==controlHeight,"retry added a row");
                    store.Write("pending.json",new {historical=true});Render(panel,output,"pending");
                    Check(!panel.E<Button>("BrowseAppButton").IsEnabled,"unconfirmed operation allows a path change");
                    Check(panel.E<Button>("ResetPendingButton").Visibility==Visibility.Collapsed&&panel.Window.FindName("RetryButton")==null&&panel.E<Border>("StatusArea").ActualHeight==controlHeight,"normal verification shows manual actions or adds a row");
                    status.paused=true;status.manuallyPaused=true;panel.Refresh();command=null;
                    panel.E<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(command==null&&!panel.E<Button>("StartButton").IsEnabled,"single button bypassed pending confirmation");
                    status.fault="领取结果仍无法确认";panel.Refresh();
                    Check(panel.E<Button>("ResetPendingButton").Visibility==Visibility.Visible&&panel.E<Button>("ResetPendingButton").IsEnabled,"blocked verification lacks a manual confirmation entry");
                    Render(panel,output,"blocked-confirmation");status.fault=null;
                    status.paused=false;status.manuallyPaused=false;
                    panel.DispatchOverride=action=>{throw new Exception("Fixture dispatched automation");};
                    store.ClearPending();status.readRetryAt=null;
                    status.connected=true;status.disconnectedSince=null;status.fault="模拟故障：正在核对上次领取结果。长状态说明应换行，保留故障原因与停止操作入口，不重复提交。";status.paused=true;Render(panel,output,"fault");
                    status.running=false;panel.Refresh();
                    Check(panel.E<TextBlock>("ClaimsCaption").Text=="上次运行领取"&&panel.E<TextBlock>("ClaimsValue").Text=="3"&&panel.E<TextBlock>("AvailableValue").ToolTip==null,"stopped run lost history or restored a tooltip");
                    Render(panel,output,"stopped");status.running=true;status.fault=null;status.paused=false;
                    status.running=false;status.petClosed=true;status.connected=false;status.paused=true;status.manuallyPaused=true;
                    store.Write("status.json",status);
                    using(var reopened=new Panel(store,preview:true)) {
                        reopened.ReadClock=()=>now;reopened.Refresh();
                        Check(reopened.E<TextBlock>("MainMessage").Text=="准备启动后台"&&reopened.E<TextBlock>("DetailMessage").Text=="请先退出已打开的桌宠，再启动后台。","reopened panel shows a past stop as its current state");
                        Check(reopened.E<Button>("StartButton").IsEnabled&&reopened.E<Button>("StartButton").Content.ToString()=="启动后台"&&reopened.Window.FindName("PauseButton")==null&&reopened.Window.FindName("StopButton")==null,"stopped panel retains stale action labels");
                        Check(reopened.E<TextBlock>("ActivityText").Text.Contains("后台和桌宠已关闭"),"last stop result missing from activity");
                        Render(reopened,output,"reopened-after-stop");
                        status.petClosed=false;status.stoppedWithoutClose=true;store.Write("status.json",status);reopened.Refresh();
                        Check(reopened.E<TextBlock>("MainMessage").Text=="准备启动后台"&&reopened.E<TextBlock>("ActivityText").Text.Contains("未关闭桌宠"),"background-only stop hides the ready state or its result");
                        store.Write("pending.json",new {historical=true});reopened.Refresh();
                        Check(reopened.E<TextBlock>("MainMessage").Text=="上次领取或补充需要确认"&&reopened.E<Button>("ResetPendingButton").Visibility==Visibility.Visible,"idle pending operation hidden by ready state");
                        status.fault="桌宠尚未退出";store.Write("status.json",status);reopened.Refresh();
                        Check(reopened.E<TextBlock>("DetailMessage").Text==status.fault,"stop failure hidden by ready state");
                        store.ClearPending();status.fault=null;status.stoppedWithoutClose=false;status.running=true;status.pid=int.MaxValue;store.Write("status.json",status);reopened.Refresh();
                        Check(reopened.E<TextBlock>("MainMessage").Text=="后台已退出","unexpected worker exit presented as a normal stop");
                    }
                    status.running=true;status.pid=Process.GetCurrentProcess().Id;status.connected=true;status.paused=false;status.manuallyPaused=false;
                    status.at=DateTimeOffset.FromUnixTimeMilliseconds(now-60000).ToString("o");panel.Refresh();
                    Check(panel.E<TextBlock>("AvailableValue").ToolTip==null&&panel.E<Border>("LeftFill").Width==0,"stale snapshot restored a tooltip or live progress");
                    status.at=DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("o");
                    for(var i=0;i<6;i++)store.Log("boost-confirmed",new {delta=100+i,padding=i==5?new string('字',1800):""});
                    for(var i=0;i<20;i++)store.Log("internal-snapshot",new {padding=new string('字',1100)});
                    RuntimeStore.Append(store.FilePath("events.jsonl"),"{malformed record}\r\n");
                    panel.Refresh();
                    var activities=panel.E<TextBlock>("ActivityText").Text.Split('\n');
                    Check(activities.Length==3&&activities[0].Contains("105")&&activities[2].Contains("103"),"three activities are lost behind internal logs, malformed rows or UTF-8 block boundaries");
                    store.Log("manual-paused");store.Log("manual-resumed");store.Log("paused-on-error");store.Log("stopped-on-error");panel.Refresh();
                    activities=panel.E<TextBlock>("ActivityText").Text.Split('\n');
                    Check(activities.Length==3&&activities[0].Contains("异常退出")&&activities[1].Contains("异常暂停")&&activities[2].Contains("手动恢复"),"manual actions are not distinguished from abnormal stops");
                    Check(panel.E<TextBlock>("ClaimsHint").Text=="后台重启后重计","session counter hint describes a panel restart");
                    panel.E<ListBox>("ModeSelector").SelectedIndex=1;
                    panel.E<ComboBox>("ReserveTarget").SelectedIndex=4;panel.E<ComboBox>("ClickerRate").SelectedIndex=2;
                    Check(panel.E<StackPanel>("ActivityEmpty").Visibility==Visibility.Collapsed&&panel.E<TextBlock>("ActivityText").Text.Contains("补充速度已改为 1,000 次/秒")&&panel.E<TextBlock>("ActivityText").Text.Contains("储备目标已改为 50,000 次"),"saved parameter changes are missing from activity");
                    Render(panel,output,"clicker-activity");
                    status.readRetryAt=now+10000;Render(panel,output,"clicker-retry-activity");status.readRetryAt=null;
                    panel.E<ListBox>("ModeSelector").SelectedIndex=2;Render(panel,output,"human-activity");
                    panel.Render(Path.Combine(output,"stable-mode-2.png"));
                    var activityY=panel.E<Border>("ActivityArea").TranslatePoint(new Point(),panel.E<Border>("RootSurface")).Y;
                    foreach(var modeIndex in new[]{0,1,2}) {
                        panel.E<ListBox>("ModeSelector").SelectedIndex=modeIndex;panel.Render(Path.Combine(output,"stable-mode-"+modeIndex+".png"));
                        Check(Math.Abs(panel.E<Border>("ActivityArea").TranslatePoint(new Point(),panel.E<Border>("RootSurface")).Y-activityY)<1,"switching mode moves the activity area");
                        var reserve=panel.E<Border>("BruteTargetBox");var speed=panel.E<Border>("FixedSpeedBox");
                        Check(reserve.Visibility==(modeIndex==0?Visibility.Visible:Visibility.Collapsed)&&speed.Visibility==(modeIndex==2?Visibility.Visible:Visibility.Collapsed),"read-only parameter frames have the wrong visibility");
                        if(modeIndex==2)Check(speed.ActualWidth==180&&speed.ActualHeight==36&&speed.BorderThickness==new Thickness(1)&&speed.CornerRadius==new CornerRadius(8)&&((SolidColorBrush)speed.Background).Color==((SolidColorBrush)panel.E<ComboBox>("ClickerRate").Background).Color,"read-only parameter frame does not match the selector style");
                    }
                    panel.Render(Path.Combine(output,"restored-default.png"));
                    var activity=panel.E<Border>("ActivityArea");
                    Check(activity.ActualHeight>=activity.DesiredSize.Height,"activity content is clipped");
                }
            } finally {Directory.Delete(root,true);}
            return checks;
        }
        static void ViewModes(string root,string output) {
            var store=new RuntimeStore(root);
            var other=new RuntimeStore(Path.Combine(root,"second"),"second",root);
            store.Write("preferences.json",new Preference {mode="human",reserveTarget=10000,autoRun=true});
            var preferences=RuntimeStore.ReadText(store.FilePath("preferences.json"));
            using(var panel=new Panel(store,preview:true)) {
                var button=panel.E<Button>("ViewModeButton");
                var expand=panel.E<Button>("MiniViewModeButton");
                Check(button.Visibility==Visibility.Visible&&!store.Exists("panel-mini.json"),"default view is not full or was persisted without a click");
                panel.Window.Width=900;panel.Window.Height=760;
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var window=panel.Window;
                window.ShowActivated=false;window.ShowInTaskbar=false;window.Opacity=0;
                window.Show();window.UpdateLayout();
                double idleHeight=window.ActualHeight;
                Check(panel.E<Grid>("MiniToolbar").Visibility==Visibility.Visible&&window.Width<820&&idleHeight<330,"mini mode did not compact the window");
                Check(RuntimeStore.ReadText(store.FilePath("preferences.json"))==preferences,"switching views changes automation preferences");
                var viewport=panel.E<ScrollViewer>("MiniViewport");
                RenderMini(panel,output,"idle");
                foreach(var name in new[]{"MiniStatusLabel","MiniStartButton","MiniStatsCard","ChestArea","MiniSettingsCard","MiniViewModeButton"}) {
                    var element=panel.E<FrameworkElement>(name);
                    Check(element.Visibility==Visibility.Visible&&element.ActualHeight>0,"mini mode lost "+name);
                }
                foreach(var name in new[]{"FullToolbar","MainViewport","MiniAlert","FooterHint"})
                    Check(panel.E<FrameworkElement>(name).Visibility==Visibility.Collapsed,"mini mode still shows "+name);
                Check(viewport.ScrollableHeight==0&&Math.Abs(viewport.ViewportHeight-panel.E<StackPanel>("MiniSections").DesiredSize.Height)<1,"idle mini content is clipped or leaves a vertical gap");
                Check(panel.E<TextBlock>("LeftTime").Text=="--:--"&&panel.E<TextBlock>("RightTime").Text=="--:--","idle mini timers lack placeholders");
                Check(panel.E<TextBlock>("ClaimsCaption").Text=="本次运行领取"&&panel.E<TextBlock>("MiniClaimsValue").Text=="—","fresh mini run count lost its caption or placeholder");
                var run=panel.E<Button>("MiniStartButton");
                Check(run.ActualWidth==38&&run.ActualHeight==34&&AutomationProperties.GetName(run)=="启动后台","mini start lost its hit target or accessible name");
                Check(expand.ActualWidth==panel.E<Button>("MiniCloseButton").Width&&((FrameworkElement)expand.Content).Width==14,
                    "mini switch lost its common hit area or compact icon");
                foreach(var name in new[]{"TrayButton","MiniTrayButton"})
                    Check(object.Equals(panel.E<Button>(name).ToolTip,"收起到托盘"),"tray button lost its tooltip: "+name);
                Check(object.Equals(button.ToolTip,"迷你模式")&&object.Equals(expand.ToolTip,"完整模式"),"view switches do not show their target mode");
                Check(object.Equals(run.ToolTip,"启动后台"),"mini start does not show its action");
                foreach(var name in new[]{"MiniMinimizeButton","MiniCloseButton"})
                    Check(panel.E<Button>(name).ToolTip==null,"routine toolbar button has a tooltip: "+name);
                CheckTabFocus(run);
                CheckTabFocus(expand);
                CheckTabFocus(panel.E<Button>("MiniTrayButton"));

                string command=null;panel.DispatchOverride=action=>command=action;
                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(command=="start","mini start did not use the existing action");
                var modes=panel.E<ComboBox>("MiniModeSelector");
                Check((string)modes.SelectedValue=="human"&&panel.E<Border>("FixedSpeedBox").Visibility==Visibility.Visible,"mini did not restore human options");
                Check((string)panel.E<Label>("MiniSpeedLabel").Content=="补充方式","human mode shows a numeric speed label");
                modes.SelectedValue="clicker";window.UpdateLayout();
                Check((string)panel.E<Label>("MiniReserveLabel").Content=="储备目标（次）"&&(string)panel.E<Label>("MiniSpeedLabel").Content=="补充速度（次/秒）",
                    "mini numeric labels do not include their units");
                var target=panel.E<ComboBox>("ReserveTarget");var speed=panel.E<ComboBox>("ClickerRate");
                Check(target.ActualWidth==modes.ActualWidth&&speed.ActualWidth==modes.ActualWidth&&modes.ActualHeight==target.ActualHeight&&speed.ActualHeight==target.ActualHeight,
                    "mini setting editors differ in size");
                target.SelectedIndex=3;speed.SelectedIndex=2;
                var selected=store.Read<Preference>("preferences.json");
                Check(selected.mode=="clicker"&&selected.reserveTarget==20000&&selected.clickerRate==1000&&selected.autoRun,"changing mini options did not share or preserve preferences");
                modes.SelectedValue="brute";
                Check(panel.E<Border>("BruteTargetBox").Visibility==Visibility.Visible&&panel.E<Border>("BruteRateBox").Visibility==Visibility.Visible,"mini brute options are not editable");
                panel.E<TextBox>("BruteTarget").Text="12345";panel.E<TextBox>("BruteRate").Text="275";
                selected=store.Read<Preference>("preferences.json");
                Check(selected.bruteReserveTarget==12345&&selected.bruteRate==275,"mini did not save custom brute values");
                RenderMini(panel,output,"brute");
                modes.SelectedValue="clicker";
                var now=RuntimeStore.Now;panel.ReadClock=()=>now;
                var status=new Status {running=true,pid=Process.GetCurrentProcess().Id,at=DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("o"),
                    connected=true,state=Tests.State(),chestTimes=new ChestTimes {updatedAt=now,left=now+600000,right=now+1200000}};
                status.state.time=now;status.state.available=267304;
                panel.ReadStatus=()=>status;panel.Refresh();
                status.state.loggedIn=false;panel.Refresh();window.UpdateLayout();
                Check(panel.E<TextBlock>("StatusLabel").Text=="等待登录"&&panel.E<TextBlock>("MiniStatusLabel").Text=="等待登录"&&
                    panel.E<TextBlock>("LeftTime").Text=="--:--"&&window.ActualHeight==idleHeight,
                    "login requirement differs between views or changes mini height");
                RenderMini(panel,output,"waiting-login");
                status.state.loggedIn=true;panel.Refresh();
                Check(panel.E<TextBlock>("LeftTime").Text=="10:00"&&panel.E<TextBlock>("RightTime").Text=="20:00","mini chest slots do not show current progress independently");
                RenderMini(panel,output,"running");
                Check(window.ActualHeight==idleHeight&&AutomationProperties.GetName(run)=="停止运行"&&panel.E<TextBlock>("MiniAvailableValue").Text=="267,304",
                    "running changed the mini layout, accessible name or available count");
                Check(object.Equals(run.ToolTip,"停止运行"),"mini stop does not show its action");
                foreach(var name in new[]{"AvailableValue","TotalValue","ClaimsValue"})
                    Check(panel.E<TextBlock>("Mini"+name).Foreground==panel.E<TextBlock>(name).Foreground,"mini statistic does not reuse the full view foreground");
                run.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(command=="pause","mini stop did not use the existing action");
                var width=window.Width;window.Width=window.MinWidth;
                target.SelectedIndex=4;
                RenderMini(panel,output,"minimum");
                var toolbar=panel.E<Grid>("MiniToolbar");
                var tray=panel.E<Button>("MiniTrayButton");
                var trayCenter=tray.TranslatePoint(new Point(tray.ActualWidth/2,tray.ActualHeight/2),toolbar);
                var hit=toolbar.InputHitTest(trayCenter) as DependencyObject;
                Check(tray.Style==expand.Style&&tray.ActualWidth==expand.ActualWidth&&tray.ActualHeight==expand.ActualHeight&&
                    AutomationProperties.GetName(tray)=="收起到托盘"&&tray.TranslatePoint(new Point(),toolbar).X==expand.TranslatePoint(new Point(expand.ActualWidth,0),toolbar).X&&
                    hit!=null&&(hit==tray||tray.IsAncestorOf(hit))&&panel.E<Border>("MiniTitleGrip").ActualWidth>=24,
                    "mini tray shortcut does not match the adjacent toggle or is clipped at minimum width");
                foreach(var editor in new[]{modes,target,speed}) {
                    var selectedText=new TextBlock {Text=editor.SelectionBoxItem.ToString(),FontFamily=editor.FontFamily,FontSize=editor.FontSize};
                    selectedText.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
                    Check(selectedText.DesiredSize.Width+editor.Padding.Left+editor.Padding.Right<=editor.ActualWidth,"mini setting value is clipped at minimum width");
                }
                window.Width=width;

                status.disconnectedSince=now;status.connected=false;status.connectionFailure="debug-unavailable";
                panel.Refresh();window.UpdateLayout();
                Check(window.ActualHeight==idleHeight&&panel.E<Border>("MiniAlert").Visibility==Visibility.Collapsed&&
                    panel.E<TextBlock>("MiniStatusLabel").ToolTip.ToString().Contains("正在重新连接桌宠"),"routine reconnect expands mini or loses its hint");
                status.disconnectedSince=now-61000;panel.Refresh();
                Check(panel.E<Border>("MiniAlert").Visibility==Visibility.Visible&&panel.E<TextBlock>("MiniDetailMessage").Text.Contains("请先停止运行"),
                    "mini reconnect hides actionable guidance");
                status.disconnectedSince=null;status.connected=true;status.connectionFailure=null;

                store.Write("pending.json",new {fixture=true});
                status.paused=true;status.fault="请核对桌宠内的结果。";panel.Refresh();
                var confirm=panel.E<Button>("MiniResetPendingButton");
                RenderMini(panel,output,"pending");
                Check(panel.E<Border>("MiniAlert").Visibility==Visibility.Visible&&panel.E<TextBlock>("MiniDetailMessage").Text==status.fault&&confirm.Visibility==Visibility.Visible&&confirm.IsEnabled,
                    "mini mode hides pending confirmation or its explanation");
                Check(confirm.TranslatePoint(new Point(0,confirm.ActualHeight),viewport).Y<=viewport.ViewportHeight,"mini confirmation is clipped in the default window");
                int prompts=0;panel.ConfirmReset=()=>{prompts++;return true;};
                confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(command=="reset"&&prompts==1&&store.Exists("pending.json"),"mini confirmation bypasses the existing confirmation path");
                store.ClearPending();

                window.Hide();
                expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(panel.E<Grid>("FullToolbar").Visibility==Visibility.Visible&&window.Width==900&&window.Height==760&&panel.E<Border>("FooterHint").Visibility==Visibility.Visible&&
                    (string)panel.E<ListBox>("ModeSelector").SelectedValue=="clicker"&&panel.E<Border>("FullTargetHost").Child==panel.E<Grid>("TargetEditor"),
                    "returning to full mode did not restore window size and controls");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            using(var reopened=new Panel(store,preview:true))
            using(var separate=new Panel(other,preview:true)) {
                var button=reopened.E<Button>("MiniViewModeButton");
                Check(reopened.E<Grid>("MiniToolbar").Visibility==Visibility.Visible&&separate.E<Grid>("FullToolbar").Visibility==Visibility.Visible,"mini preference did not persist or leaked to another instance");
                var window=reopened.Window;
                window.ShowActivated=false;window.ShowInTaskbar=false;window.Opacity=0;
                window.Show();window.Hide();reopened.Show();window.UpdateLayout();
                Check(button.IsVisible,"restoring the window changed the chosen view");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                Check(window.WindowState==WindowState.Normal&&window.ActualWidth>=820&&window.ActualHeight>=660,"shown mini window did not expand to full size");
            }
            using(var reopened=new Panel(store,preview:true))
                Check(reopened.E<Grid>("FullToolbar").Visibility==Visibility.Visible,"explicit return to full mode did not persist");
        }
        static void RenderMini(Panel panel,string output,string name) {
            panel.Window.UpdateLayout();
            panel.Render(Path.Combine(output,"mini-"+name+".png"),(int)Math.Ceiling(panel.Window.ActualWidth),(int)Math.Ceiling(panel.Window.ActualHeight));
        }
        static void CenterViewToggle(RuntimeStore store) {
            using(var panel=new Panel(store,preview:true)) {
                var window=panel.Window;
                window.ShowActivated=false;window.ShowInTaskbar=false;window.Opacity=0;
                panel.FitToWorkArea(SystemParameters.WorkArea.Height);
                window.Show();window.UpdateLayout();
                var shrink=panel.E<Button>("ViewModeButton");
                var expand=panel.E<Button>("MiniViewModeButton");
                CheckTabFocus(shrink);
                CheckTabFocus(panel.E<Button>("TrayButton"));
                CheckTabFocus(panel.E<Button>("StartButton"));
                CheckTabFocus(panel.E<Button>("BrowseAppButton"));
                CheckTabFocus(panel.E<Button>("UpdateButton"));

                var center=window.PointToScreen(new Point(window.ActualWidth/2,window.ActualHeight/2));
                shrink.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var compactCenter=window.PointToScreen(new Point(window.ActualWidth/2,window.ActualHeight/2));
                Check((compactCenter-center).Length<=2,"shrinking the window moves its center");
                expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var restoredCenter=window.PointToScreen(new Point(window.ActualWidth/2,window.ActualHeight/2));
                Check((restoredCenter-center).Length<=2,"expanding the window moves its center");

                shrink.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var area=System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).WorkingArea;
                var corner=window.PointFromScreen(new Point(area.Right,area.Bottom));
                window.Left+=corner.X-window.ActualWidth;window.Top+=corner.Y-window.ActualHeight;
                expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var expandedCorner=window.PointToScreen(new Point(window.ActualWidth,window.ActualHeight));
                Check(Math.Abs(expandedCorner.X-area.Right)<=2&&Math.Abs(expandedCorner.Y-area.Bottom)<=2,
                    "expanding near the window work area edge does not stay inside it");
            }
        }
        static void CheckTabFocus(Button button) {
            var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
            var key=(DependencyPropertyKey)typeof(UIElement).GetField("IsKeyboardFocusedPropertyKey",flags).GetValue(null);
            var oldValue=button.IsKeyboardFocused;
            var ring=(Border)button.Template.FindName("TabFocusRing",button);
            var source=PresentationSource.FromVisual(button);
            Action settle=()=>button.Dispatcher.Invoke(new Action(()=>{}),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Action<Key> press=input=>{
                var args=new KeyEventArgs(Keyboard.PrimaryDevice,source,0,input){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                button.RaiseEvent(args);
                settle();
                Check(!args.Handled,"panel intercepts a keyboard key: "+input);
            };
            try {
                button.SetValue(key,true);
                press(Key.LeftAlt);
                press(Key.System);
                Check(ring.Visibility==Visibility.Collapsed&&button.FocusVisualStyle==null&&button.Focusable&&button.IsTabStop,
                    "Alt reveals a button outline or focus navigation was disabled");
                press(Key.Tab);
                Check(Equals(Window.GetWindow(button).Tag,"Tab"),"the panel does not recognize the Tab key");
                Check(ring.Visibility==Visibility.Visible&&!ring.IsHitTestVisible,"Tab does not reveal the focused button or the outline blocks mouse clicks");
                button.SetValue(key,false);
                settle();
                Check(ring.Visibility==Visibility.Collapsed,"unfocused button keeps its outline");
                button.SetValue(key,true);
                press(Key.RightAlt);
                Check(ring.Visibility==Visibility.Visible,"Alt clears a focus outline requested by Tab");
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent});
                settle();
                Check(ring.Visibility==Visibility.Collapsed&&Equals(button.GetValue(UIElement.IsKeyboardFocusedProperty),true),
                    "mouse input changes focus instead of clearing just the outline");
            } finally {button.SetValue(key,oldValue);}
        }
        static void Wheel(FrameworkElement source,int delta) {
            source.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,0,delta){RoutedEvent=Mouse.MouseWheelEvent});
        }
        static void ScrollContent(Panel panel,string output) {
            var surface=panel.E<Border>("RootSurface");
            var viewport=panel.E<ScrollViewer>("MainViewport");
            var modes=panel.E<ListBox>("ModeSelector");
            var title=panel.E<Grid>("TitleGrip");var footer=panel.E<Border>("FooterHint");
            var minimum=Path.Combine(output,"scroll-top-minimum.png");
            panel.Render(minimum,820,660);
            double footerTop=footer.TranslatePoint(new Point(),surface).Y;
            double titleTop=title.TranslatePoint(new Point(),surface).Y;
            Check(viewport.ScrollableHeight>0&&viewport.VerticalOffset==0,"short window has no content to scroll");
            Check(Math.Abs(viewport.ViewportWidth-viewport.ActualWidth)<1,"hidden scroll bar takes up horizontal space");

            Wheel(modes,-120);surface.UpdateLayout();
            Check(viewport.VerticalOffset>0,"mouse wheel over modes does not scroll short window");
            while(viewport.VerticalOffset<viewport.ScrollableHeight) {
                double offset=viewport.VerticalOffset;
                Wheel(viewport,-120);surface.UpdateLayout();
                Check(viewport.VerticalOffset>offset,"mouse wheel stopped before the end");
            }
            foreach(var name in new[]{"ReserveTarget","ClickerRate"}) {
                var option=panel.E<ComboBox>(name);
                double top=option.TranslatePoint(new Point(),viewport).Y;
                Check(top>=0&&top+option.ActualHeight<=viewport.ViewportHeight,"parameter stays clipped after scrolling: "+name);
            }
            Check(footer.TranslatePoint(new Point(),surface).Y==footerTop&&title.TranslatePoint(new Point(),surface).Y==titleTop,"scrolling moved the title or footer");
            panel.Render(Path.Combine(output,"scroll-bottom-minimum.png"),820,660);
            Wheel(viewport,120);surface.UpdateLayout();
            Check(viewport.VerticalOffset<viewport.ScrollableHeight,"mouse wheel cannot scroll back up");

            panel.Render(Path.Combine(output,"scroll-default.png"));
            Check(viewport.ScrollableHeight==0&&viewport.VerticalOffset==0,"content remains offset or scrollable after enlarging the window");
            Wheel(viewport,-120);surface.UpdateLayout();
            Check(viewport.VerticalOffset==0,"mouse wheel moves content that already fits");
        }
        static int HitTest(Window window,Point point) {
            const int wmNcHitTest=0x0084;
            Point screen=window.PointToScreen(point);
            int position=(ushort)Math.Round(screen.X)|((int)(ushort)Math.Round(screen.Y)<<16);
            return SendMessage(new WindowInteropHelper(window).Handle,wmNcHitTest,IntPtr.Zero,new IntPtr(position)).ToInt32();
        }
        static void ResizeBorders(RuntimeStore store) {
            using(var panel=new Panel(store,preview:true)) {
                var window=panel.Window;
                window.ShowActivated=false;window.ShowInTaskbar=false;window.Opacity=0;
                window.Show();window.UpdateLayout();
                double width=window.ActualWidth,height=window.ActualHeight;
                var points=new[]{new Point(3,height/2),new Point(width-3,height/2),new Point(width/2,3),new Point(16,3),
                    new Point(width-16,3),new Point(width/2,height-3),new Point(16,height-3),new Point(width-16,height-3)};
                for(int i=0;i<points.Length;i++)Check(HitTest(window,points[i])==10+i,"incorrect native resize hit at "+points[i]);

                foreach(var name in new[]{"TitleGrip","ViewModeButton","TrayButton","CloseButton","MinimizeButton","ReserveTarget","UpdateButton"}) {
                    var control=panel.E<FrameworkElement>(name);
                    var center=control.TranslatePoint(new Point(control.ActualWidth/2,control.ActualHeight/2),window);
                    Check(HitTest(window,center)==1,"resize border interferes with "+name);
                }
                Check(window.ResizeMode==ResizeMode.CanResizeWithGrip,"existing resize grip was removed");

                window.WindowState=WindowState.Maximized;window.UpdateLayout();
                Check(HitTest(window,new Point(window.ActualWidth/2,3))==1,"maximized panel exposes a resize border");
            }
        }
        static void PathToolTip(Panel panel) {
            var button=panel.E<Button>("BrowseAppButton");
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            // IsMouseOver reads WPF's cached flag; changing only its dependency property does not simulate hover.
            var writeFlag=typeof(UIElement).GetMethod("WriteFlag",flags);
            var hoverFlag=Enum.Parse(writeFlag.GetParameters()[0].ParameterType,"IsMouseOverCache");
            var constructor=typeof(ToolTipEventArgs).GetConstructor(flags,null,new[]{typeof(bool)},null);
            var wasHovered=button.IsMouseOver;var wasEnabled=button.IsEnabled;var path=button.ToolTip;
            try {
                foreach(var enabled in new[]{true,false}) foreach(var hovered in new[]{false,true}) {
                    button.IsEnabled=enabled;writeFlag.Invoke(button,new object[]{hoverFlag,hovered});
                    Check(button.IsMouseOver==hovered,"fixture did not establish the requested mouse state");
                    var opening=(ToolTipEventArgs)constructor.Invoke(new object[]{true});
                    button.RaiseEvent(opening);
                    Check(opening.Handled==!hovered,"path tooltip does not require mouse hover: enabled="+enabled+", hovered="+hovered);
                    Check(object.Equals(button.ToolTip,path),"tooltip filtering changed the full path");
                }
                Check(ToolTipService.GetShowOnDisabled(button),"disabled-button path hints unavailable");
            } finally {button.IsEnabled=wasEnabled;writeFlag.Invoke(button,new object[]{hoverFlag,wasHovered});}
        }
        static void RunButtonStates(Panel panel,string output,string name) {
            var button=panel.E<Button>("StartButton");
            var flags=System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic;
            // Exercise WPF template states without moving the user's pointer or clicking a live app.
            var hoverKey=(DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey",flags).GetValue(null);
            var pressedKey=(DependencyPropertyKey)typeof(System.Windows.Controls.Primitives.ButtonBase).GetField("IsPressedPropertyKey",flags).GetValue(null);
            var wasHovered=button.IsMouseOver;var wasPressed=button.IsPressed;
            try {
                foreach(var state in new[]{"default","hover","pressed"}) {
                    var pressed=state=="pressed";
                    button.SetValue(hoverKey,state!="default");
                    button.SetValue(pressedKey,pressed);
                    panel.Render(Path.Combine(output,name+"-"+state+".png"));
                    var surface=(Border)button.Template.FindName("RunSurface",button);
                    var background=name=="stop"?(pressed?"#E4CBBE":state=="hover"?"#ECD9CE":"#F4EAE2"):(pressed?"#D4A05A":state=="hover"?"#DFAD66":"#E9BD7D");
                    var border=name=="stop"?(pressed?"#BF8E7D":state=="hover"?"#CFA08E":"#DDB9A9"):(pressed?"#BC8541":state=="hover"?"#CA9249":"#D8A55D");
                    var foreground=name=="stop"?(pressed?"#934B39":"#A45C49"):"#55391F";
                    Check(((SolidColorBrush)surface.Background).Color==(Color)ColorConverter.ConvertFromString(background)&&surface.Opacity==1,"run button has incorrect state background");
                    Check(((SolidColorBrush)surface.BorderBrush).Color==(Color)ColorConverter.ConvertFromString(border),"run button has incorrect state border");
                    Check(((SolidColorBrush)button.Foreground).Color==(Color)ColorConverter.ConvertFromString(foreground),"run button has incorrect state foreground");
                }
            } finally {button.SetValue(pressedKey,wasPressed);button.SetValue(hoverKey,wasHovered);}
        }
        static void Render(Panel panel,string output,string name) {
            foreach(var size in new[]{new Size(880,846),new Size(820,660)}) {
                var suffix=size.Width==880?"-default":"-minimum";
                panel.Render(Path.Combine(output,name+suffix+".png"),(int)size.Width,(int)size.Height);
                var viewport=panel.E<ScrollViewer>("MainViewport");
                Check(viewport.ClipToBounds&&viewport.ComputedVerticalScrollBarVisibility==Visibility.Collapsed&&viewport.ComputedHorizontalScrollBarVisibility==Visibility.Collapsed,"viewport does not clip or shows scroll bars");
                Check(panel.Window.FindName("ModeHint")==null,"removed mode hint still occupies the parameter card");
                var surface=panel.E<Border>("RootSurface");
                var picker=panel.E<Button>("BrowseAppButton");var path=panel.E<TextBlock>("AppPathText");var change=panel.E<Border>("ChangePathAction");
                var pickerPoint=picker.TranslatePoint(new Point(),surface);var startPoint=panel.E<Button>("StartButton").TranslatePoint(new Point(),surface);
                Check(picker.ActualWidth>=350&&picker.ActualWidth<=400&&Math.Abs(pickerPoint.X-startPoint.X)<1,"path component is too wide, too narrow or misaligned");
                Check(picker.IsAncestorOf(path)&&picker.IsAncestorOf(change)&&((TextBlock)change.Child).Text=="更改"&&path.TextTrimming==TextTrimming.CharacterEllipsis&&path.TextWrapping==TextWrapping.NoWrap,"path and change action do not share one selection button");
                var changePoint=change.TranslatePoint(new Point(),surface);var pathPoint=path.TranslatePoint(new Point(),surface);
                Check(pathPoint.X+path.ActualWidth+8<=changePoint.X+0.01&&changePoint.X+change.ActualWidth<=pickerPoint.X+picker.ActualWidth,"path crowds or overflows the change action");
                var another=panel.E<Button>("AnotherAppButton");var anotherPoint=another.TranslatePoint(new Point(),surface);
                var detail=panel.E<TextBlock>("DetailMessage");var detailPoint=detail.TranslatePoint(new Point(),surface);
                Check(another.Content.ToString()=="+ 多开"&&another.BorderThickness==new Thickness(1)&&another.FontWeight==FontWeights.Normal&&anotherPoint.Y<pickerPoint.Y&&detailPoint.X+detail.ActualWidth<=anotherPoint.X,"multi-pet entry lacks a subtle button affordance or overlaps status text");
                foreach(var key in new[]{"StartButton","BrowseAppButton","AnotherAppButton","StartupCheckBox","AutoRunCheckBox","FeedbackButton","UpdateButton"}) {
                    var control=panel.E<FrameworkElement>(key);var point=control.TranslatePoint(new Point(),surface);
                    Check(point.X>=0&&point.X+control.ActualWidth<=size.Width-20&&control.ActualHeight>=28,"control overflow or undersized: "+name+" "+key);
                    if(control is Button) {
                        var button=(Button)control;
                        if(button.Content is string) {
                            var text=new TextBlock {Text=(string)button.Content,FontSize=button.FontSize,FontFamily=button.FontFamily,FontWeight=button.FontWeight};
                            text.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
                            Check(text.DesiredSize.Width<=button.ActualWidth-button.Padding.Left-button.Padding.Right-button.BorderThickness.Left-button.BorderThickness.Right+0.01,"button text clipped: "+key);
                        }
                    }
                }
                foreach(var key in new[]{"StatusLabel","CountCaption","ReserveCaption","ClaimsCaption","ClaimsHint"}) {
                    var text=panel.E<TextBlock>(key);
                    var measure=new TextBlock {Text=text.Text,FontFamily=text.FontFamily,FontSize=text.FontSize,FontWeight=text.FontWeight};
                    measure.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
                    Check(text.TextWrapping==TextWrapping.Wrap||measure.DesiredSize.Width<=text.ActualWidth+1,"caption clipped: "+name+" "+key);
                }
                foreach(var dpi in new[]{120,144}) {
                    // WPF rasterization at 125%/150% density; does not change the host's Windows DPI.
                    var scale=dpi/96.0;var bitmap=new RenderTargetBitmap((int)(size.Width*scale),(int)(size.Height*scale),dpi,dpi,PixelFormats.Pbgra32);bitmap.Render(surface);
                    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using(var file=File.Create(Path.Combine(output,name+suffix+"-"+dpi+"dpi.png")))encoder.Save(file);
                }
                foreach(var key in new[]{"ResetPendingButton"}) {
                    var action=panel.E<Button>(key);if(action.Visibility!=Visibility.Visible)continue;
                    var stop=panel.E<Button>("StartButton");var actionPoint=action.TranslatePoint(new Point(),surface);var stopPoint=stop.TranslatePoint(new Point(),surface);
                    Check(actionPoint.X>=stopPoint.X+stop.ActualWidth+12&&actionPoint.X+action.ActualWidth<=size.Width-24&&Math.Abs(actionPoint.Y+action.ActualHeight/2-stopPoint.Y-stop.ActualHeight/2)<1,"conditional action is not beside the main button");
                }
                var footer=panel.E<Border>("FooterHint");
                var startup=panel.E<CheckBox>("StartupCheckBox");var autoRun=panel.E<CheckBox>("AutoRunCheckBox");var feedback=panel.E<Button>("FeedbackButton");
                var startupPoint=startup.TranslatePoint(new Point(),surface);var autoPoint=autoRun.TranslatePoint(new Point(),surface);var feedbackPoint=feedback.TranslatePoint(new Point(),surface);
                Check(autoRun.Template==startup.Template&&autoPoint.X>=startupPoint.X+startup.ActualWidth+6&&autoPoint.X+autoRun.ActualWidth+6<=feedbackPoint.X&&Math.Abs(autoPoint.Y-startupPoint.Y)<1,"footer switches differ, overlap or are out of order");
                Check(viewport.TranslatePoint(new Point(),surface).Y+viewport.ActualHeight<=footer.TranslatePoint(new Point(),surface).Y,"main viewport covers footer");
                Check(panel.E<Border>("ActivityArea").ClipToBounds,"activity does not clip to the available space");
                if(size.Width==880) {
                    var activity=panel.E<Border>("ActivityArea");var text=panel.E<TextBlock>("ActivityText");
                    if(name=="idle") {
                        var gap=footer.TranslatePoint(new Point(),surface).Y-activity.TranslatePoint(new Point(),surface).Y-activity.ActualHeight;
                        Check(Math.Abs(gap-10)<1,"activity to footer gap differs from card spacing: "+gap);
                    }
                    Check(text.Height==66&&text.TranslatePoint(new Point(),activity).Y+text.ActualHeight<=activity.ActualHeight-activity.Padding.Bottom&&panel.Window.FindName("ActivityScroll")==null,"three activities are clipped or scrollable at the default window size");
                }
            }
        }
    }
}
