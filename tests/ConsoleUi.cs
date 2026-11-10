using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tappo {
    // Isolated production-XAML fixtures: no timer, tray, worker, discovery or real startup writes.
    internal static class ConsoleUiChecks {
        static int checks;
        static void Check(bool pass,string message) {checks++;if(!pass)throw new Exception(message);}
        public static int Run() {
            checks=0;
            var root=Path.Combine(Path.GetTempPath(),"tappo-console-ui-"+Guid.NewGuid().ToString("N"));
            var output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"console-previews");Directory.CreateDirectory(output);
            try {
                var store=new RuntimeStore(root);var now=RuntimeStore.Now;
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
                        Check(reopened.E<TextBlock>("MainMessage").Text=="后台意外停止","unexpected worker exit presented as a normal stop");
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
                    var activityY=panel.E<Border>("ActivityArea").TranslatePoint(new Point(),panel.E<Border>("RootSurface")).Y;
                    foreach(var modeIndex in new[]{0,1,2}) {
                        panel.E<ListBox>("ModeSelector").SelectedIndex=modeIndex;panel.Render(Path.Combine(output,"stable-mode-"+modeIndex+".png"));
                        Check(Math.Abs(panel.E<Border>("ActivityArea").TranslatePoint(new Point(),panel.E<Border>("RootSurface")).Y-activityY)<1,"switching mode moves the activity area");
                        var reserve=panel.E<Border>("FixedReserveBox");var speed=panel.E<Border>("FixedSpeedBox");
                        Check(reserve.Visibility==(modeIndex==0?Visibility.Visible:Visibility.Collapsed)&&speed.Visibility==(modeIndex==1?Visibility.Collapsed:Visibility.Visible),"read-only parameter frames have the wrong visibility");
                        if(modeIndex!=1)Check(speed.ActualWidth==180&&speed.ActualHeight==36&&speed.BorderThickness==new Thickness(1)&&speed.CornerRadius==new CornerRadius(8)&&((SolidColorBrush)speed.Background).Color==((SolidColorBrush)panel.E<ComboBox>("ClickerRate").Background).Color,"read-only parameter frame does not match the selector style");
                    }
                    // Focus checks use only the isolated fixture window.
                    panel.Window.Show();panel.Window.Activate();panel.Window.UpdateLayout();
                    var pause=panel.E<Button>("StartButton");pause.Focus();
                    Check(pause.IsKeyboardFocused,"pause cannot receive keyboard focus");
                    var sections=panel.E<StackPanel>("MainSections");var origin=sections.TranslatePoint(new Point(),panel.E<Border>("RootSurface"));
                    var select=panel.E<ComboBox>("ReserveTarget");select.BringIntoView();select.Focus();panel.Window.UpdateLayout();
                    Check(select.IsKeyboardFocusWithin,"parameter cannot receive keyboard focus");
                    Check(sections.TranslatePoint(new Point(),panel.E<Border>("RootSurface"))==origin,"focusing clipped content moved the main panel");
                    panel.Render(Path.Combine(output,"restored-default.png"));
                    var activity=panel.E<Border>("ActivityArea");
                    Check(activity.ActualHeight>=activity.DesiredSize.Height,"activity content is clipped");
                    panel.Window.Hide();
                }
            } finally {Directory.Delete(root,true);}
            return checks;
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
                Check(button.Focusable&&button.IsTabStop&&ToolTipService.GetShowOnDisabled(button),"tooltip filtering disabled keyboard access or disabled-button path hints");
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
                Check(panel.Window.FindName("ContentScroll")==null&&panel.E<Border>("MainViewport").ClipToBounds,"main panel scrolls instead of clipping");
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
                Check(panel.E<Border>("MainViewport").TranslatePoint(new Point(),surface).Y+panel.E<Border>("MainViewport").ActualHeight<=footer.TranslatePoint(new Point(),surface).Y,"main viewport covers footer");
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
