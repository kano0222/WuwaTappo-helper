using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

namespace Tappo {
    internal sealed class Panel : IDisposable {
        readonly RuntimeStore store; public readonly Window Window;
        readonly ActivityLog activityLog;
        readonly DiagnosticSession diagnostics;
        readonly bool verifyIdentity;
        public bool Busy {get;private set;} public bool AllowExit;
        internal bool TrayExitEnabled {get {return trayExit!=null&&trayExit.Enabled;}}
        string notice,savedMode=Preference.DefaultMode; bool selecting,choosingClose,noticeIsSuccess;
        string busyAction,unconfirmedCommand,unconfirmedAction,unconfirmedSession; bool startupElevation;
        Status lastDisplayedStatus; long noticeUntil;
        bool noticeActionFailure; string noticeStatusAt;
        internal Func<RuntimeIdentity,string> InspectIdentity=RuntimeIdentity.Inspect;
        int savedTarget=2000,savedRate=100,savedBruteTarget=2000,savedBruteRate=100;
        string runAction="start";
        bool startupEnabled;
        bool autoRun,autoRunAttempted;
        bool autoTrayPending,autoTrayCancelled;
        ReleaseNotes releaseNotes;
        ReleaseNotesDialog releaseNotesDialog;
        internal Window ReleaseNotesWindow {get {return releaseNotesDialog==null?null:releaseNotesDialog.Window;}}
        internal Func<bool> ReadStartup=()=>false;
        internal Action<bool> WriteStartup=enabled=>{};
        internal Forms.ToolStripMenuItem TrayStartup {get;private set;}
        internal Forms.ToolStripMenuItem TrayAutoRun {get;private set;}
        Forms.NotifyIcon tray; Forms.ContextMenuStrip menu; Forms.ToolStripMenuItem trayRun,trayExit,trayStatus;
        DispatcherTimer timer; Mutex mutex; EventWaitHandle showEvent; bool ownsMutex; System.Drawing.Icon icon;
        public Func<Status> ReadStatus; public Action<string> DispatchOverride;
        internal Func<bool> ConfirmReset;
        internal Func<CloseChoice> ClosePrompt;
        internal bool AutoCheckUpdates;
        internal bool AutoShowReleaseNotes;
        internal Func<CancellationToken,Task<UpdateInfo>> UpdateCheck=Updates.Check;
        internal Func<UpdateInfo,Action<int>,CancellationToken,Task<string>> UpdateDownload=(info,progress,token)=>UpdateInstaller.Download(info,AppDomain.CurrentDomain.BaseDirectory,progress,token);
        internal Action<string> InstallUpdate=UpdateInstaller.Launch;
        internal Action<string> OpenUpdatePage=url=>{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});};
        readonly CancellationTokenSource updateCancellation=new CancellationTokenSource();
        bool checkingUpdates,downloadingUpdate,launchingUpdate,disposed; long nextUpdateCheck; UpdateInfo update; string updateStage;
        internal Func<long> ReadClock=()=>RuntimeStore.Now;
        internal int ControlTimeoutMs=40000;
        public Panel(RuntimeStore store,bool preview=false) {
            this.store=store;activityLog=new ActivityLog(store.FilePath("events.jsonl")); Window=(Window)XamlReader.Parse(Resources.Text("Panel.xaml"));
            diagnostics=new DiagnosticSession(store,"panel",background:!preview);
            verifyIdentity=!preview;
            diagnostics.Sample=diagnostics.Observe;
            if(!preview) {
                var startup=new LogonStartup(System.Reflection.Assembly.GetExecutingAssembly().Location,store);
                ReadStartup=()=>startup.Enabled;WriteStartup=startup.SetEnabled;
            }
            E<CheckBox>("StartupCheckBox").Click+=(s,e)=>SetStartup(E<CheckBox>("StartupCheckBox").IsChecked==true);
            E<CheckBox>("AutoRunCheckBox").Click+=(s,e)=>SetAutoRun(E<CheckBox>("AutoRunCheckBox").IsChecked==true);
            ReadStatus=()=>store.Read<Status>("status.json");
            var preference=store.Read<Preference>("preferences.json");
            autoRun=preference!=null&&preference.autoRun;E<CheckBox>("AutoRunCheckBox").IsChecked=autoRun;
            if(preference!=null&&Policy.Modes.Contains(preference.mode)) savedMode=preference.mode;
            if(preference!=null&&Preference.ReserveTargets.Contains(preference.reserveTarget)) savedTarget=preference.reserveTarget;
            if(preference!=null&&Preference.ClickerRates.Contains(preference.clickerRate)) savedRate=preference.clickerRate;
            if(preference!=null&&Preference.ValidBruteTarget(preference.bruteReserveTarget))savedBruteTarget=preference.bruteReserveTarget;
            if(preference!=null&&Preference.ValidBruteRate(preference.bruteRate))savedBruteRate=preference.bruteRate;
            E<ComboBox>("ReserveTarget").ItemsSource=Preference.ReserveTargets.Select(v=>new ComboBoxItem {Content=v.ToString("N0")+" 次",Tag=v}).ToArray();
            E<ComboBox>("ClickerRate").ItemsSource=Preference.ClickerRates.Select(v=>new ComboBoxItem {Content=v.ToString("N0")+" 次/秒",Tag=v}).ToArray();
            RestoreSelection();
            foreach(var name in new[]{"BruteTarget","BruteRate"}) {
                E<TextBox>(name).TextChanged+=(s,e)=>SaveBrute();
                E<TextBox>(name).LostKeyboardFocus+=(s,e)=>RestoreBruteInput();
                E<TextBox>(name).PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Enter) {FinishBruteEditing();e.Handled=true;}};
            }
            Window.PreviewMouseDown+=(s,e)=>{
                var input=Keyboard.FocusedElement as TextBox;
                if((input==E<TextBox>("BruteTarget")||input==E<TextBox>("BruteRate"))&&input!=null&&!input.IsMouseOver)FinishBruteEditing();
            };
            E<ListBox>("ModeSelector").SelectionChanged+=(s,e)=>SaveMode();
            E<ComboBox>("ReserveTarget").SelectionChanged+=(s,e)=>SaveMode();
            E<ComboBox>("ClickerRate").SelectionChanged+=(s,e)=>SaveMode();
            E<Button>("StartButton").Click+=async(s,e)=>{if(E<Button>("StartButton").IsEnabled)await UserAction(runAction);};
            E<Button>("ResetPendingButton").Click+=async(s,e)=>await ResetPending();
            E<Button>("BrowseAppButton").Click+=async(s,e)=>await ChoosePath(true);
            E<Button>("BrowseAppButton").ToolTipOpening+=(s,e)=>{if(!E<Button>("BrowseAppButton").IsMouseOver)e.Handled=true;};
            E<Button>("AnotherAppButton").Click+=(s,e)=>OpenAnother();
            E<Button>("MinimizeButton").Click+=(s,e)=>Window.WindowState=WindowState.Minimized;
            E<Button>("CloseButton").Click+=async(s,e)=>await RequestClose();
            UpdateCaption("检查更新");
            E<Button>("UpdateButton").Click+=async(s,e)=>await UpdateAction();
            E<Button>("FeedbackButton").Click+=(s,e)=>{
                try {OpenUpdatePage(Updates.IssuesUrl);}
                catch {SetNotice("无法打开浏览器，请手动访问 "+Updates.IssuesUrl);Refresh();}
            };
            E<Button>("ExportDiagnosticsButton").Click+=async(s,e)=>{
                var dialog=new Microsoft.Win32.SaveFileDialog {Filter="日志文件 (*.zip)|*.zip",FileName="Tappo-logs-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip",OverwritePrompt=false};
                if(dialog.ShowDialog(Window)!=true)return;
                var button=E<Button>("ExportDiagnosticsButton");button.IsEnabled=false;
                try {await Task.Run(()=>diagnostics.Export(dialog.FileName));SetNotice("日志已保存。",true);}
                catch(Exception error) {SetNotice("日志保存失败："+error.Message);}
                finally {button.IsEnabled=true;Refresh();}
            };
            E<System.Windows.Controls.Grid>("TitleGrip").MouseLeftButtonDown+=(s,e)=>{
                if(e.ClickCount==2) Window.WindowState=Window.WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
                else if(e.LeftButton==MouseButtonState.Pressed) Window.DragMove();
            };
            Window.Closing+=(s,e)=>{if(!AllowExit){e.Cancel=true;Window.Dispatcher.BeginInvoke(new Action(async()=>await RequestClose()));}};
            Window.Closed+=(s,e)=>Life("closed");
            Window.ContentRendered+=async(s,e)=>{if(disposed)return;Life("visible");if(!preview) {if(AutoShowReleaseNotes)ShowReleaseNotes();await AutoRunOnOpen();}if(AutoCheckUpdates&&RuntimeStore.Now>=nextUpdateCheck)await CheckForUpdates();};
            var path=preview?null:Platform.Discover(store);SetPath(path);
            Refresh();
        }
        public T E<T>(string name) where T:class { var value=Window.FindName(name) as T; if(value==null) throw new InvalidOperationException("界面元素缺失："+name);return value; }
        void Text(string name,string text) { E<TextBlock>(name).Text=text; }
        void SetNotice(string message,bool success=false,bool operationFailure=false) {
            notice=message;noticeIsSuccess=success;noticeUntil=ReadClock()+12000;
            noticeActionFailure=operationFailure;noticeStatusAt=lastDisplayedStatus==null?null:lastDisplayedStatus.at;
        }
        internal static string TimeoutMessage(string action,bool elevation) {
            switch(action) {
                case "start":return "后台启动尚未确认。"+(elevation?"如出现管理员确认窗口，请处理后等待状态更新。":"请等待状态更新。")+"请勿重复启动；仍无响应时请导出日志。";
                case "pause":return "未能确认后台已停止运行，可能仍在执行。请勿重复启动；请等待状态更新或导出日志。";
                case "resume":return "未能确认后台已恢复运行，操作可能已执行。请等待状态更新，勿重复启动；仍无响应时请导出日志。";
                case "stop":return "未能确认后台和桌宠已关闭，可能仍在执行。请勿重复启动；请等待状态更新或导出日志。";
                case "stop-background":case "disconnect":return "未能确认自动化已断开，后台可能仍在执行。请勿重复启动；请等待状态更新或导出日志。";
                case "reset":return "未能确认核对记录已处理，操作可能已执行。请勿重复确认或启动；请等待状态更新或导出日志。";
                default:return "操作结果尚未确认。请等待状态更新或导出日志。";
            }
        }
        string PresentationIdentity(Status status) {
            if(status==null)return "no-status";
            if(status.identity==null)return verifyIdentity?"identity-unavailable":RuntimeStore.Alive(status.pid)?"matched":"process-exited";
            if(!diagnostics.StatusBelongs(status))return "identity-unverifiable";
            if(status.identity.pid!=status.pid)return "identity-unverifiable";
            return InspectIdentity(status.identity);
        }
        string RecordTime(string at) {DateTimeOffset value;return DateTimeOffset.TryParse(at,out value)?value.ToLocalTime().ToString("MM-dd HH:mm:ss"):"时间未知";}
        string StatusAge(Status status) {
            DateTimeOffset at;
            if(status==null||!DateTimeOffset.TryParse(status.at,out at))return "更新时间未知";
            var seconds=(ReadClock()-at.ToUnixTimeMilliseconds())/1000;
            if(seconds<0)return "更新时间异常";
            return "上次状态距今 "+(seconds<60?seconds+" 秒":seconds<3600?seconds/60+" 分 "+seconds%60+" 秒":seconds/3600+" 小时 "+seconds%3600/60+" 分");
        }
        void HistoricalNumbers(Status status,bool history,bool? balanceHistory=null) {
            var oldBalance=balanceHistory??history;
            Text("AvailableCaption",oldBalance?"可用次数 · 上次记录":"可用次数");
            Text("ClaimsCaption",status==null?"运行领取":history?status.running?"领取数 · 上次记录":"上次运行领取":"本次运行领取");
            Text("ClaimsHint",history?"记录于 "+RecordTime(status==null?null:status.at):"后台重启后重计");
            var sampleAt=status==null?null:status.at;
            if(status!=null&&status.state!=null&&status.state.time>0) {
                try {sampleAt=DateTimeOffset.FromUnixTimeMilliseconds(status.state.time).ToString("o");}catch(ArgumentOutOfRangeException) {sampleAt=null;}
            }
            Text("ReserveCaption",oldBalance?"记录于 "+RecordTime(sampleAt):"每箱消耗 1,000 · 储备目标 "+(savedMode=="brute"?savedBruteTarget:savedTarget).ToString("N0"));
        }
        void RefreshTotal() {
            try {Text("TotalValue",new ClaimHistory(store,Instances.SavedPath(store.Root)).Total.ToString("N0"));Text("CountCaption","助手领取的宝箱数");}
            catch {Text("TotalValue","—");Text("CountCaption","统计读取失败");}
        }
        public void SetPath(string path) {Text("AppPathText",path??"选择桌宠程序");E<Button>("BrowseAppButton").ToolTip=path;
            var label=path==null?null:new DirectoryInfo(Path.GetDirectoryName(path)).Name;
            Window.Title="Wuwa Tappo · 后台助手"+(label==null?"":" · "+label);
            Text("InstanceTitle","WUWA TAPPO  /  后台助手"+(label==null?"":"  /  "+label));
            if(tray!=null)tray.Text=("Wuwa Tappo · "+(label??"助手")).Substring(0,Math.Min(63,("Wuwa Tappo · "+(label??"助手")).Length));
        }
        void Life(string phase) { RuntimeStore.Append(store.FilePath("panel-lifecycle.log"),DateTimeOffset.Now.ToString("o")+" pid="+Process.GetCurrentProcess().Id+" "+phase+"\n"); }
        public static string InstanceKey(string root) {
            using(var sha=SHA256.Create()) using(var user=WindowsIdentity.GetCurrent())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root.ToUpperInvariant()+"|"+user.User.Value))).Replace("-","").Substring(0,16);
        }
        public bool ClaimInstance() {
            var key=InstanceKey(store.Root);mutex=new Mutex(false,"Local\\TappoPanel-"+key);showEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\TappoPanelShow-"+key);
            try {ownsMutex=mutex.WaitOne(0);} catch(AbandonedMutexException){ownsMutex=true;}
            if(!ownsMutex) showEvent.Set(); return ownsMutex;
        }
        internal void FitToWorkArea(double height) {Window.Height=Math.Max(Window.MinHeight,Math.Min(Window.Height,height-40));}
        public void Show() {autoTrayCancelled=true;Window.ShowInTaskbar=true;Window.Show();Window.WindowState=WindowState.Normal;Window.Activate();if(releaseNotesDialog!=null)releaseNotesDialog.Window.Activate();Life("restored");}
        void HideToTray() {Window.Hide();Window.ShowInTaskbar=false;Life("hidden-to-tray");}
        internal async Task RequestClose() {
            if(choosingClose) return;
            if(Busy) {SetNotice("正在处理操作，请等待完成后关闭。");Refresh();return;}
            choosingClose=true;
            try {
                var choice=ClosePrompt==null?new CloseDialog(Window).Show():ClosePrompt();
                if(choice==CloseChoice.Tray) {if(releaseNotesDialog!=null)releaseNotesDialog.Window.Activate();else HideToTray();}
                else if(choice==CloseChoice.Exit) await Exit();
            } finally {choosingClose=false;}
        }
        public void InitializeTray() {
            menu=new Forms.ContextMenuStrip();trayStatus=(Forms.ToolStripMenuItem)menu.Items.Add("后台：未运行");trayStatus.Enabled=false;
            menu.Items.Add("打开控制面板",null,(s,e)=>Show());
            trayRun=(Forms.ToolStripMenuItem)menu.Items.Add("启动后台",null,async(s,e)=>{if(trayRun.Enabled)await UserAction(runAction);});
            menu.Items.Add(new Forms.ToolStripSeparator());
            TrayStartup=(Forms.ToolStripMenuItem)menu.Items.Add("开机自启",null,(s,e)=>SetStartup(!startupEnabled));
            TrayAutoRun=(Forms.ToolStripMenuItem)menu.Items.Add("自动运行",null,(s,e)=>SetAutoRun(!autoRun));TrayAutoRun.Checked=autoRun;
            menu.Opening+=(s,e)=>Refresh();RefreshStartup();
            menu.Items.Add(new Forms.ToolStripSeparator());trayExit=(Forms.ToolStripMenuItem)menu.Items.Add("退出助手",null,async(s,e)=>await Exit());
            using(var stream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("helper.ico")) icon=new System.Drawing.Icon(stream);
            tray=new Forms.NotifyIcon {Icon=icon,ContextMenuStrip=menu,Text="Wuwa Tappo 助手",Visible=true};tray.DoubleClick+=(s,e)=>Show();SetPath(Platform.Discover(store));Refresh();
        }
        public void StartTimer() {
            timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
            timer.Tick+=async(s,e)=>{
                if(UpdateInstaller.Requested(AppDomain.CurrentDomain.BaseDirectory)) {
                    if(!Busy&&!Alive(ReadStatus())) {AllowExit=true;Window.Close();if(Application.Current!=null)Application.Current.Shutdown();}
                    else {SetNotice("正在等待当前操作结束并退出，以安装更新…");Refresh();}
                    return;
                }
                if(showEvent!=null&&showEvent.WaitOne(0)) Show();Refresh();TryAutoTray();if(AutoCheckUpdates&&RuntimeStore.Now>=nextUpdateCheck)await CheckForUpdates();
            };timer.Start();
        }
        internal void SetStartup(bool enabled) {
            var previous=startupEnabled;
            try {WriteStartup(enabled);var actual=ReadStartup();if(actual!=enabled)throw new InvalidOperationException("设置未能保存，请重试。");startupEnabled=actual;}
            catch {startupEnabled=previous;SetNotice("未能保存开机自启设置，请重试。");}
            Refresh();
        }
        internal void SetAutoRun(bool enabled) {
            try {
                store.Write("preferences.json",new Preference {mode=savedMode,reserveTarget=savedTarget,clickerRate=savedRate,bruteReserveTarget=savedBruteTarget,bruteRate=savedBruteRate,autoRun=enabled});
                autoRun=enabled;
            } catch {SetNotice("未能保存自动运行设置，请重试。");}
            E<CheckBox>("AutoRunCheckBox").IsChecked=autoRun;if(TrayAutoRun!=null)TrayAutoRun.Checked=autoRun;Refresh();
        }
        internal async Task AutoRunOnOpen() {
            if(autoRunAttempted)return;autoRunAttempted=true;
            if(!autoRun||Busy||disposed)return;
            try {
                Refresh();
                if(store.Exists("pending.json")||!E<Button>("StartButton").IsEnabled)return;
                if(runAction=="pause") {autoTrayPending=true;TryAutoTray();return;}
                if(runAction=="start"&&Platform.Discover(store)==null) {
                    SetNotice("请先选择桌宠程序，再点击“启动后台”。");Refresh();return;
                }
                if(await UserAction(runAction,true)) {autoTrayPending=true;TryAutoTray();}
            } catch(Exception error) {SetNotice(error.Message);Refresh();}
        }
        internal void TryAutoTray() {
            if(!autoTrayPending||autoTrayCancelled||!autoRun||Busy||disposed||AllowExit||releaseNotesDialog!=null||tray==null)return;
            try {
                var status=ReadStatus();DateTimeOffset at;
                if(store.Exists("pending.json")||!Alive(status)||status.paused||status.manuallyPaused||status.recovering||status.fault!=null||status.readRetryAt.HasValue||status.retrying||
                    status.connected!=true||Connection.Hint(status,ReadClock())!=null||!DateTimeOffset.TryParse(status.at,out at)||ReadClock()-at.ToUnixTimeMilliseconds()>45000)return;
                autoTrayPending=false;HideToTray();
            } catch { } // Keep the panel visible if startup health cannot be confirmed.
        }
        internal void ShowReleaseNotes(string version=null,ReleaseNote[] entries=null) {
            if(releaseNotes!=null||disposed)return;
            try {
                releaseNotes=ReleaseNotes.Open(store.BaseRoot,version??Updates.BuildVersion,entries??Json.Decode<ReleaseNote[]>(Resources.Text("ReleaseNotes.json")));
                if(releaseNotes==null)return;
                releaseNotesDialog=new ReleaseNotesDialog(Window,releaseNotes.Version,releaseNotes.Entries);
                releaseNotesDialog.Window.Closed+=(s,e)=>{
                    try {if(!disposed&&!AllowExit)releaseNotes.MarkRead();}
                    catch {autoTrayCancelled=true;SetNotice("更新内容已关闭，但未能保存已读状态，下次打开时可能再次显示。");Refresh();}
                    finally {releaseNotes.Dispose();releaseNotes=null;releaseNotesDialog=null;}
                    TryAutoTray();
                };
                releaseNotesDialog.Window.Show();
            } catch {
                if(releaseNotes!=null)releaseNotes.Dispose();releaseNotes=null;releaseNotesDialog=null;
                autoTrayCancelled=true;
                SetNotice("暂时无法显示更新内容，请稍后重新打开助手。");Refresh();
            }
        }
        void RefreshStartup() {
            string error=null;
            try {startupEnabled=ReadStartup();}catch {error="暂时无法读取开机自启设置。";}
            var toggle=E<CheckBox>("StartupCheckBox");toggle.IsChecked=startupEnabled;toggle.IsEnabled=error==null;
            if(TrayStartup!=null) {TrayStartup.Checked=startupEnabled;TrayStartup.Enabled=error==null;}
        }
        void UpdateCaption(string text) {
            var button=E<Button>("UpdateButton");button.Content="v"+Updates.DisplayVersion+" · "+text;
            button.ToolTip=updateStage==null?null:"更新会重启当前助手及其多开窗口，桌宠保留；后台按“自动运行”设置启动。";
        }
        internal async Task UpdateAction() {
            if(checkingUpdates||downloadingUpdate||launchingUpdate||disposed)return;
            var button=E<Button>("UpdateButton");
            if(updateStage!=null) {
                try {
                    launchingUpdate=true;button.IsEnabled=false;UpdateCaption("准备更新…");
                    InstallUpdate(updateStage);
                    // Keep the panel available on updater failure; its event coordinates a successful exit.
                    UpdateCaption("重启更新");
                } catch(InvalidUpdateFilesException error) {
                    updateStage=null;UpdateCaption("重新下载");
                    SetNotice(error.Message);Refresh();
                } catch {UpdateCaption("重启更新");SetNotice("更新启动失败，请重试。");Refresh();}
                finally {launchingUpdate=false;if(!disposed)button.IsEnabled=true;}
                return;
            }
            if(update!=null&&update.available) {
                downloadingUpdate=true;button.IsEnabled=false;UpdateCaption("下载中…");
                try {
                    updateStage=await UpdateDownload(update,percent=>Window.Dispatcher.BeginInvoke(new Action(()=>{
                        if(!disposed&&downloadingUpdate)UpdateCaption("下载 "+percent+"%");
                    })),updateCancellation.Token);
                    if(disposed)return;
                    UpdateCaption("重启更新");
                } catch {
                    if(disposed)return;
                    UpdateCaption("重试下载");
                } finally {downloadingUpdate=false;if(!disposed)button.IsEnabled=true;}
            } else await CheckForUpdates();
        }
        internal async Task CheckForUpdates() {
            if(checkingUpdates||downloadingUpdate||launchingUpdate||updateStage!=null||disposed)return;
            checkingUpdates=true;nextUpdateCheck=RuntimeStore.Now+6*60*60*1000L;
            var button=E<Button>("UpdateButton");button.IsEnabled=false;UpdateCaption("检查中…");
            try {
                var result=await UpdateCheck(updateCancellation.Token);
                if(disposed)return;
                update=result;button.Tag=result.available?"update":null;
                UpdateCaption(result.available?"下载新版":"已是最新");
            } catch {
                if(disposed)return;
                update=null;button.Tag=null;
                nextUpdateCheck=RuntimeStore.Now+15*60*1000L;
                UpdateCaption("重试检查");
            } finally {checkingUpdates=false;if(!disposed)button.IsEnabled=true;}
        }
        void OpenAnother() {
            try {
                var dialog=new Microsoft.Win32.OpenFileDialog {Title="连接另一个桌宠：选择 WuwaTappo.exe",Filter="Wuwa Tappo (WuwaTappo.exe)|WuwaTappo.exe",CheckFileExists=true};
                if(dialog.ShowDialog(Window)!=true)return;
                var path=dialog.FileName;
                if(!Platform.ValidPath(path))throw new InvalidOperationException("请选择 Wuwa Tappo 安装目录中的 WuwaTappo.exe。");
                var baseRoot=AppDomain.CurrentDomain.BaseDirectory;
                RuntimeStore target;
                using(Instances.LockBindings(baseRoot)) {
                    var primary=new RuntimeStore(baseRoot);
                    if(Instances.SavedPath(baseRoot)==null) {
                        var discovered=Platform.Discover(primary);
                        if(discovered!=null)Platform.SavePath(primary,discovered);
                    }
                    target=Instances.Open(baseRoot,Instances.ForPath(baseRoot,path));
                    if(Instances.SavedPath(target.Root)==null)Platform.SavePath(target,path);
                }
                using(var process=Process.Start(new ProcessStartInfo(Platform.Executable,Instances.Arguments(target)) {UseShellExecute=true,WorkingDirectory=baseRoot})) { }
            } catch(Exception error) {SetNotice(error.Message);Refresh();}
        }
        async Task<bool> ChoosePath(bool force) {
            try {
                var path=force?null:Platform.Discover(store);
                if(path==null) {
                    var dialog=new Microsoft.Win32.OpenFileDialog {Title="选择已安装的 WuwaTappo.exe",Filter="Wuwa Tappo (WuwaTappo.exe)|WuwaTappo.exe",CheckFileExists=true};
                    if(dialog.ShowDialog(Window)!=true) return false;path=dialog.FileName;
                }
                if(!Platform.ValidPath(path))throw new InvalidOperationException("请选择 Wuwa Tappo 安装目录中的 WuwaTappo.exe。");
                var previous=Instances.SavedPath(store.Root);
                await PreparePathChange(path);
                var baseRoot=AppDomain.CurrentDomain.BaseDirectory;
                using(Instances.LockBindings(baseRoot)) {
                    var other=Path.Combine(baseRoot,"runtime","instances",Instances.Key(path),"config.local.json");
                    if(store.InstanceId==null&&File.Exists(other))throw new InvalidOperationException("此桌宠已有独立窗口，请通过“多开桌宠”打开。");
                    Platform.SavePath(store,path);
                }
                SetPath(path);
                if(force&&(previous==null||Instances.Key(previous)!=Instances.Key(path))) {
                    try {store.Log("program-selected");}
                    catch {SetNotice("程序位置已保存，但活动记录写入失败。");}
                }
                Refresh();return true;
            } catch(Exception error) {SetNotice(error.Message);Refresh();return false;}
        }
        internal async Task PreparePathChange(string path) {
            var previous=Instances.SavedPath(store.Root);
            if(previous!=null&&Instances.Key(previous)==Instances.Key(path))return;
            if(Busy||store.InstanceId!=null||store.Exists("pending.json"))throw new InvalidOperationException("请先停止运行并核对待确认操作，再更换程序位置。");
            var status=ReadStatus();
            if(Alive(status)) {
                DateTimeOffset at;
                if(!status.paused||!status.manuallyPaused||!DateTimeOffset.TryParse(status.at,out at)||(DateTimeOffset.FromUnixTimeMilliseconds(ReadClock())-at).TotalSeconds>45)throw new InvalidOperationException("请先停止运行，等待状态确认后再更换程序位置。");
                await Act("stop-background");
                if(Alive(ReadStatus()))throw new InvalidOperationException("旧后台尚未退出，请稍后再更换程序位置。");
            }
        }
        void RestoreSelection() {
            selecting=true;
            try {
                foreach(ListBoxItem item in E<ListBox>("ModeSelector").Items) if((string)item.Tag==savedMode) E<ListBox>("ModeSelector").SelectedItem=item;
                foreach(ComboBoxItem item in E<ComboBox>("ReserveTarget").Items) if((int)item.Tag==savedTarget) E<ComboBox>("ReserveTarget").SelectedItem=item;
                foreach(ComboBoxItem item in E<ComboBox>("ClickerRate").Items) if((int)item.Tag==savedRate) E<ComboBox>("ClickerRate").SelectedItem=item;
                E<TextBox>("BruteTarget").Text=savedBruteTarget.ToString();
                E<TextBox>("BruteRate").Text=savedBruteRate.ToString();
                Text("BruteError","");
            } finally {selecting=false;}
        }
        void FinishBruteEditing() {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(Window,null);
        }
        void RestoreBruteInput() {
            int target,rate;
            if(!ReadBrute(out target,out rate))RestoreSelection();
        }
        bool ReadBrute(out int target,out int rate) {
            var targetParsed=ParseWhole(E<TextBox>("BruteTarget").Text,out target);
            var rateParsed=ParseWhole(E<TextBox>("BruteRate").Text,out rate);
            return targetParsed&&rateParsed&&Preference.ValidBruteTarget(target)&&Preference.ValidBruteRate(rate);
        }
        static bool ParseWhole(string text,out int value) {
            value=0;return text.Length>0&&text.All(c=>c>='0'&&c<='9')&&int.TryParse(text,out value);
        }
        void SaveBrute() {
            if(selecting||savedMode!="brute")return;
            int target,rate;
            if(!ReadBrute(out target,out rate)) {
                Text("BruteError","请输入整数：目标 1,000～99,999,999；速度 1～2,000。当前仍使用上次有效值。");return;
            }
            Text("BruteError","");
            if(target==savedBruteTarget&&rate==savedBruteRate)return;
            bool saved=false;
            try {
                var preference=new Preference {mode=savedMode,reserveTarget=savedTarget,clickerRate=savedRate,bruteReserveTarget=target,bruteRate=rate,autoRun=autoRun};
                preference.Validate();store.Write("preferences.json",preference);
                var targetChanged=target!=savedBruteTarget;var rateChanged=rate!=savedBruteRate;
                savedBruteTarget=target;savedBruteRate=rate;saved=true;
                if(targetChanged)store.Log("reserve-target-selected",new {target=target});
                if(rateChanged)store.Log("clicker-rate-selected",new {rate=rate});
                SetNotice("自定义参数已保存，后台下一轮操作生效。",true);
            } catch(Exception error) {SetNotice(saved?"设置已保存，但活动记录写入失败。":"自定义参数保存失败："+error.Message);RestoreSelection();}
            Refresh();
        }
        void SaveMode() {
            if(selecting) return;
            bool saved=false;
            try {
                var item=E<ListBox>("ModeSelector").SelectedItem as ListBoxItem;var mode=item==null?null:item.Tag as string;
                var preference=new Preference {mode=mode,reserveTarget=(int)((ComboBoxItem)E<ComboBox>("ReserveTarget").SelectedItem).Tag,clickerRate=(int)((ComboBoxItem)E<ComboBox>("ClickerRate").SelectedItem).Tag,bruteReserveTarget=savedBruteTarget,bruteRate=savedBruteRate,autoRun=autoRun};
                preference.Validate();
                var modeChanged=savedMode!=mode;var targetChanged=savedTarget!=preference.reserveTarget;var rateChanged=savedRate!=preference.clickerRate;
                store.Write("preferences.json",preference);savedMode=mode;savedTarget=preference.reserveTarget;savedRate=preference.clickerRate;saved=true;
                if(modeChanged) {RestoreSelection();store.Log("mode-selected",new {mode=mode});}
                if(targetChanged)store.Log("reserve-target-selected",new {target=savedTarget});
                if(rateChanged)store.Log("clicker-rate-selected",new {rate=savedRate});
                SetNotice("模式与参数已保存。",true);
            } catch(Exception error) {
                SetNotice(saved?"设置已保存，但活动记录写入失败。":"设置保存失败："+error.Message);RestoreSelection();
            }
            Refresh();
        }
        public static bool Alive(Status status) {return status!=null&&status.running&&RuntimeStore.Alive(status.pid)&&
            (status.identity==null||RuntimeIdentity.Inspect(status.identity)!="pid-reused");}
        static string PauseAction(Status status,bool pending) {return status!=null&&(pending||status.recovering?status.manuallyPaused:status.paused)?"resume":"pause";}
        internal static void PrepareStart(RuntimeStore store,string commandId=null) {
            var old=store.Read<Status>("lock.json");
            if(old!=null&&RuntimeStore.Alive(old.pid)) throw new InvalidOperationException("自动化已运行，请先停止现有后台。");
            store.Write("control.json",new Command {id=commandId??Guid.NewGuid().ToString(),action="resume"});
        }
        public async Task Act(string action,string pendingHash=null) {
            if(UpdateInstaller.Requested(AppDomain.CurrentDomain.BaseDirectory))throw new InvalidOperationException("助手正在退出以安装更新，请稍候。");
            if(Busy) return;
            if(DispatchOverride!=null) {DispatchOverride(action);return;}
            if(action=="start"&&!await ChoosePath(false)) return;
            busyAction=action;startupElevation=false;
            Busy=true;SetNotice(null);Refresh();var requested=DateTimeOffset.UtcNow;
            string commandId=Guid.NewGuid().ToString();
            var commandStarted=RuntimeStore.Now;string commandResult="completed";
            string targetSession=null;
            var diagnosticCommand=new Command {id=commandId,action=action};
            diagnostics.Command("command-requested",diagnosticCommand);
            try {
                if(action=="reset"&&!Alive(ReadStatus())) {
                    using(var guard=new FileStream(store.FilePath("worker.guard"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) store.ResolvePending(pendingHash);
                    SetNotice("记录已保留，请点击启动后台。",true);return;
                }
                if(action=="start") {
                    PrepareStart(store,commandId);
                    diagnostics.Command("command-sent",new Command {id=commandId,action="resume"});
                    var info=new ProcessStartInfo(Platform.Executable,"--worker"+Instances.Arguments(store)) {WorkingDirectory=store.Root,UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden};
                    if(!Platform.Administrator) {info.Verb="runas";startupElevation=true;}
                    using(var process=Process.Start(info)) {
                        if(process==null) throw new InvalidOperationException("后台未启动。");
                        store.Write("launcher-status.json",new LauncherStatus {requestedAt=requested.ToString("o"),runnerId=process.Id});
                    }
                } else {
                    var status=ReadStatus();if(!Alive(status)) throw new InvalidOperationException("后台未运行。");
                    if(!diagnostics.StatusBelongs(status))throw new InvalidOperationException("当前文件属于其他后台会话，请导出日志核对。");
                    targetSession=status.identity==null?null:status.identity.session;
                    if(action=="resume"&&store.Exists("pending.json")) throw new InvalidOperationException("存在结果未确认的操作，已阻止恢复。");
                    store.Write("control.json",new Command {id=commandId,action=action,pendingHash=pendingHash});
                    diagnostics.Command("command-sent",diagnosticCommand);
                }
                var until=RuntimeStore.Now+(action=="start"?75000:ControlTimeoutMs);
                while(RuntimeStore.Now<until) {
                    await Task.Delay(300);var status=ReadStatus();
                    var launcher=store.Read<LauncherStatus>("launcher-status.json");DateTimeOffset failed;
                    if(launcher!=null&&launcher.error!=null&&DateTimeOffset.TryParse(launcher.failedAt,out failed)&&failed>=requested) throw new InvalidOperationException(launcher.error);
                    DateTimeOffset at;
                    if(status==null||!DateTimeOffset.TryParse(status.at,out at)||at<requested) continue;
                    if(targetSession!=null&&(status.identity==null||status.identity.session!=targetSession||status.commandId!=commandId))continue;
                    if(action=="reset"&&status.resetCommandId==commandId) {
                        if(status.resetError!=null) throw new InvalidOperationException(status.resetError);
                        SetNotice("已继续运行，上次操作不会重发。",true);return;
                    }
                    if(action=="start"&&Alive(status)||action=="pause"&&Alive(status)&&status.manuallyPaused||action=="resume"&&Alive(status)&&!status.paused&&!status.retrying) return;
                    if((action=="stop"||action=="stop-background"||action=="disconnect")&&!status.running) {
                        if(RuntimeStore.Alive(status.pid)) continue;
                        if(action=="disconnect"&&!status.automationDisconnected) throw new InvalidOperationException("自动化停止尚未确认。");
                        if(action=="stop"&&!status.petClosed&&!status.stoppedWithoutClose) throw new InvalidOperationException(status.fault??"后台已停止，但桌宠关闭尚未确认。");
                        SetNotice(null);
                        return;
                    }
                    if(action=="start"&&!status.running&&status.fault!=null) throw new InvalidOperationException(status.fault);
                }
                throw new TimeoutException(TimeoutMessage(action,startupElevation));
            } catch(Exception error) {
                commandResult="failed";
                diagnostics.Command("command-failed",diagnosticCommand,error.GetType().Name);
                if(error is TimeoutException) {
                    diagnostics.CaptureControlTimeout(commandId);
                    unconfirmedCommand=commandId;unconfirmedAction=action;unconfirmedSession=targetSession;
                }
                SetNotice(error.Message,operationFailure:true);throw;
            }
            finally {diagnostics.Record("command-wait-ended",action,commandId,commandResult,elapsed:Math.Max(0,RuntimeStore.Now-commandStarted));Busy=false;Refresh();}
        }
        async Task<bool> UserAction(string action,bool automatic=false) {
            if(!automatic)autoTrayCancelled=true;
            try {await Act(action);return true;}
            catch(Exception error) {
                SetNotice(error.Message,operationFailure:true);
                try {store.Log("panel-action-failed",new {action=action,errorType=error.GetType().Name,message=error.Message});} catch { }
                Refresh();
                return false;
            }
        }
        internal async Task ResetPending() {
            if(Busy) return;
            try {
                var status=ReadStatus();
                if(!store.Exists("pending.json")) {SetNotice("当前没有待确认操作。",true);return;}
                if(Alive(status)&&(!status.paused||status.fault==null)) throw new InvalidOperationException("操作仍在确认中，请等待完成后再处理。");
                var hash=store.PendingHash();
                var confirmed=ConfirmReset!=null?ConfirmReset():MessageBox.Show(Window,
                    "已核对桌宠，要继续运行吗？\n\n上次操作不会重发，记录会保留。\n后台未运行时，请再点击启动后台。",
                    "确认并继续",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
                if(confirmed) await Act("reset",hash);
            } catch(Exception error) {SetNotice(error.Message);}
            finally {Refresh();}
        }
        public async Task Exit() {
            if(Busy) {SetNotice("正在处理操作，请等待完成后退出。");Show();return;}
            try {
                if(Alive(ReadStatus())) await Act("disconnect");
                AllowExit=true;Window.Close();if(Application.Current!=null) Application.Current.Shutdown();
            } catch(Exception error) {SetNotice("退出未完成："+error.Message,operationFailure:true);Refresh();Show();}
        }
        public void Refresh() {
            RefreshStartup();
            RefreshTotal();
            try {
                var s=ReadStatus();var alive=Alive(s);DateTimeOffset at;
                var stale=alive&&(verifyIdentity&&s.identity==null||!diagnostics.StatusBelongs(s)||!DateTimeOffset.TryParse(s.at,out at)||(DateTimeOffset.FromUnixTimeMilliseconds(ReadClock())-at).TotalSeconds>45);
                var identity=PresentationIdentity(s);
                var exited=identity=="process-exited"||identity=="pid-reused";
                var unknown=s!=null&&identity!="matched"&&!exited;
                var history=s!=null&&(!alive||stale||unknown||exited);
                lastDisplayedStatus=s;
                if(unconfirmedCommand!=null&&s!=null) {
                    var sameSession=unconfirmedSession==null||s.identity!=null&&s.identity.session==unconfirmedSession;
                    var acknowledged=s.commandId==unconfirmedCommand&&
                        (unconfirmedAction=="pause"?s.manuallyPaused:unconfirmedAction=="resume"?!s.paused&&!s.retrying:unconfirmedAction=="reset"?s.resetCommandId==unconfirmedCommand:true);
                    if(exited&&sameSession||!history&&identity=="matched"&&(!sameSession||acknowledged)) {
                        unconfirmedCommand=null;unconfirmedAction=null;unconfirmedSession=null;SetNotice(null);
                    }
                }
                if(notice!=null&&ReadClock()>=noticeUntil)SetNotice(null);
                var pending=store.Exists("pending.json");
                if(noticeActionFailure&&unconfirmedCommand==null&&!history&&s!=null&&s.at!=noticeStatusAt&&!pending&&s.fault==null&&!s.readRetryAt.HasValue&&!s.paused)SetNotice(null);
                string label="未运行",color="#EAE7E1",message="准备启动后台",detail="请先退出已打开的桌宠，再启动后台。";
                if(alive) {
                    label="运行中";color="#E2EFDD";message="后台运行中";detail="自动补充次数，领取到期宝箱。";
                    if(s.readRetryAt.HasValue) {label="等待重试";color="#FFF0D1";message=Connection.RetryMessage(s.readRetryStage,s.connectionFailure);detail=(s.readRetryError??"暂时无法获取桌宠信息")+"；约 "+Math.Max(0,(long)Math.Ceiling((s.readRetryAt.Value-ReadClock())/1000.0))+" 秒后重试。";}
                    if(s.paused) {label=s.manuallyPaused?"已停止":"已暂停";color="#FFF0D1";message=s.manuallyPaused?"已停止运行":"已暂停运行";detail=s.manuallyPaused?"桌宠不受影响，点击“启动后台”可继续。":"";}
                    if(pending&&!s.paused&&s.fault==null) {label="处理中";color="#FFF0D1";message="正在检查领取或补充是否成功";detail="";}
                    if(s.fault!=null||pending&&s.paused) {label="需处理";color="#F8E0D8";message="后台已暂停，需要确认";detail=s.fault??"请查看上次领取或补充是否成功，再点击“确认并继续”。";}
                    if(s.recovering) {label="检查中";color="#FFF0D1";message="正在检查上次领取或补充是否成功";detail=s.fault??"无需重复操作。";if(s.manuallyPaused)detail+=" 已停止运行，检查后也不会自动继续。";}
                    var connectionHint=Connection.Hint(s,ReadClock());
                    if(connectionHint!=null) {
                        if(pending||s.manuallyPaused||s.fault!=null||s.recovering) detail=connectionHint+" "+detail;
                        else {label=s.connectionFailure=="incompatible"?"需处理":s.connected!=false&&s.state!=null&&s.state.locked?"等待更新":"等待连接";color="#FFF0D1";message=s.connectionFailure=="incompatible"?"暂不支持当前桌宠版本":s.connectionFailure=="main-not-ready"?"正在等待桌宠启动":s.connected!=false&&s.state!=null&&s.state.locked?"等待桌宠更新完成":"正在重新连接桌宠";detail=connectionHint;}
                    }
                    if(stale) {label="响应异常";color="#FFF0D1";message="后台响应异常";detail=StatusAge(s)+"，当前显示的是上次记录。请等待状态更新或导出日志。";}
                } else if(s!=null&&s.fault!=null) {label="启动或运行失败";color="#F8E0D8";message="后台已停止";detail=s.fault;}
                else if(pending) {label="需处理";color="#FFF0D1";message="上次领取或补充需要确认";detail="请查看桌宠，再点击“确认并继续”。";}
                else if(s!=null&&s.running) {message="无法确认后台状态";detail="当前显示上次记录。请勿重复启动；请导出日志。";}
                if(exited&&s!=null) {
                    label="已退出";color="#EAE7E1";message="后台已退出";
                    detail=(string.IsNullOrWhiteSpace(s.fault)?"": "上次错误："+s.fault+"。")+"当前显示上次运行记录。"+(pending?"请先核对上次操作；如需分析原因，请导出日志。":"如需继续，请点击“启动后台”；如需分析原因，请导出日志。");
                } else if(unknown) {
                    label="状态未知";color="#FFF0D1";message="无法确认后台状态";detail=StatusAge(s)+"，当前显示上次记录。请勿重复启动；请导出日志。";
                }
                if(alive&&s.connected==false&&detail==message) detail="连接后自动继续。";
                if(unconfirmedCommand!=null&&!Busy) {label="结果未确认";color="#FFF0D1";message="操作结果尚未确认";detail=TimeoutMessage(unconfirmedAction,startupElevation);}
                if(Busy) {label="处理中";message="正在处理，请稍候";detail=busyAction=="start"&&startupElevation?"如出现管理员确认窗口，请处理后等待后台确认。":"正在等待后台确认操作结果。";}
                Text("StatusLabel",label);E<Border>("StatusPill").Background=(Brush)new BrushConverter().ConvertFromString(color);
                var statusNeedsAttention=pending||stale||s!=null&&s.fault!=null||
                    alive&&(s.paused||s.manuallyPaused||s.recovering||s.readRetryAt.HasValue||Connection.Hint(s,ReadClock())!=null);
                if(statusNeedsAttention&&noticeIsSuccess) SetNotice(null);
                Text("MainMessage",message);Text("DetailMessage",Busy||unknown||exited||stale||unconfirmedCommand!=null?detail:notice??detail);
                var pauseAction=alive?PauseAction(s,pending):"pause";var pauseEnabled=alive&&!Busy&&(!pending||!s.manuallyPaused);
                runAction=alive?pauseAction:"start";
                E<Button>("StartButton").Content=runAction=="pause"?"停止运行":"启动后台";
                E<Button>("StartButton").Tag=runAction;
                E<Button>("StartButton").IsEnabled=!Busy&&(!alive||pauseEnabled)&&!(runAction=="resume"&&stale);
                E<Button>("BrowseAppButton").IsEnabled=!Busy&&!pending&&Instances.Arguments(store)==""&&(!alive||!stale&&s.paused&&s.manuallyPaused);
                E<Button>("AnotherAppButton").IsEnabled=!Busy;
                var canConfirm=pending&&(!alive||!stale&&s.paused&&s.fault!=null);
                E<Button>("ResetPendingButton").Visibility=canConfirm?Visibility.Visible:Visibility.Collapsed;
                E<Button>("ResetPendingButton").IsEnabled=canConfirm&&!Busy;
                var state=s==null?null:s.state;
                Text("AvailableValue",state==null||!state.available.HasValue?"—":state.available.Value.ToString("N0"));
                Text("ClaimsValue",s==null?"—":s.claimsThisRun.ToString("N0"));
                var snapshotCurrent=alive&&!history&&s.connected!=false;
                E<FrameworkElement>("ReserveTarget").Visibility=savedMode=="brute"?Visibility.Collapsed:Visibility.Visible;
                E<FrameworkElement>("BruteTargetBox").Visibility=savedMode=="brute"?Visibility.Visible:Visibility.Collapsed;
                E<FrameworkElement>("BruteRateBox").Visibility=savedMode=="brute"?Visibility.Visible:Visibility.Collapsed;
                E<FrameworkElement>("ClickerRate").Visibility=savedMode=="clicker"?Visibility.Visible:Visibility.Collapsed;
                E<FrameworkElement>("FixedSpeed").Visibility=savedMode=="human"?Visibility.Visible:Visibility.Collapsed;
                Text("FixedSpeed","波动慢速");
                E<Label>("SpeedLabel").Content=savedMode=="human"?"补充方式":savedMode=="brute"?"补充速度（次/秒）":"补充速度";
                E<Label>("ReserveLabel").Content=savedMode=="brute"?"储备目标（次）":"储备目标";
                E<Label>("ReserveLabel").Target=savedMode=="brute"?(UIElement)E<TextBox>("BruteTarget"):E<ComboBox>("ReserveTarget");
                E<Label>("SpeedLabel").Target=savedMode=="brute"?(UIElement)E<TextBox>("BruteRate"):E<ComboBox>("ClickerRate");
                var balanceHistory=history||s!=null&&s.connected==false||state!=null&&state.time>0&&(ReadClock()-state.time>45000||state.time>ReadClock());
                HistoricalNumbers(s,history,balanceHistory);
                var clock=s==null?null:s.chestTimes;
                bool valid=clock!=null&&clock.updatedAt.HasValue&&clock.left.HasValue&&clock.right.HasValue&&clock.error==null&&ReadClock()-clock.updatedAt>=0&&ReadClock()-clock.updatedAt<=90000;
                var chestAt=clock!=null&&clock.updatedAt.HasValue?DateTimeOffset.FromUnixTimeMilliseconds(clock.updatedAt.Value).ToString("o"):s==null?null:s.at;
                Text("SnapshotLabel",history||s!=null&&s.connected==false?"上次记录 "+RecordTime(chestAt):!alive?"启动后同步":clock!=null&&clock.error!=null?"宝箱时间查询失败":valid?"上次同步 "+DateTimeOffset.FromUnixTimeMilliseconds(clock.updatedAt.Value).ToLocalTime().ToString("HH:mm:ss"):"等待接口同步");
                Chest("Left","left",s,snapshotCurrent,valid);Chest("Right","right",s,snapshotCurrent,valid);
                Activity(s);
                if(tray!=null) {
                    tray.Text="Wuwa Tappo 助手 · "+label;trayStatus.Text="后台："+label;
                    trayRun.Text=E<Button>("StartButton").Content.ToString();trayRun.Enabled=E<Button>("StartButton").IsEnabled;
                    trayExit.Enabled=true;trayExit.Text="退出助手";
                }
            } catch(Exception) {
                Text("StatusLabel","状态未知");Text("MainMessage","无法确认后台状态");Text("DetailMessage","暂时无法读取运行记录。请勿重复启动；请导出日志。");
                HistoricalNumbers(lastDisplayedStatus,true);Text("SnapshotLabel","上次记录 "+RecordTime(lastDisplayedStatus==null?null:lastDisplayedStatus.at));
                Chest("Left","left",lastDisplayedStatus,false,false);Chest("Right","right",lastDisplayedStatus,false,false);
                E<Button>("StartButton").IsEnabled=false;
                E<Button>("ResetPendingButton").IsEnabled=false;
                if(tray!=null) {trayRun.Enabled=false;trayExit.Enabled=true;trayStatus.Text="后台：状态读取失败";}
            }
        }
        void Chest(string prefix,string side,Status s,bool alive,bool valid) {
            var state=s==null?null:s.state;double progress=0;string text=s==null?"--:--":!s.running?"未连接":s.connected==false?"连接已断开":!alive?"状态未更新":state==null?"等待首次读取":"等待同步";
            if(alive&&state!=null&&!state.loggedIn) text="等待登录";
            if(alive&&state!=null&&state.loggedIn) {
                if(!valid) text="等待同步";
                else {
                    var target=side=="left"?s.chestTimes.left:s.chestTimes.right;var remaining=Math.Max(0,(long)Math.Ceiling((target.Value-ReadClock())/1000.0));
                    if(remaining>0) {text=(remaining/60).ToString("00")+":"+(remaining%60).ToString("00");progress=Math.Max(0,Math.Min(1,1-remaining/1800.0));}
                    else {text=state.Button(side)?"已就绪":"冷却已结束";progress=1;}
                }
            }
            Text(prefix+"Time",text);
            E<Border>(prefix+"Fill").Width=Math.Max(0,E<Border>(prefix+"Track").ActualWidth*progress);
        }
        void Activity(Status status) {
            activityLog.Refresh();var entries=activityLog.Recent.Take(3).ToList();
            DateTimeOffset at;
            if(status!=null) {
                if(!DateTimeOffset.TryParse(status.at,out at))at=DateTimeOffset.FromUnixTimeMilliseconds(ReadClock());
                var identity=PresentationIdentity(status);
                if(status.running&&(identity=="process-exited"||identity=="pid-reused")) {
                    entries.Add(new ActivityRecord {at=at,message=string.IsNullOrWhiteSpace(status.fault)?"后台已退出，未记录原因（时间为最后状态时间）":"后台已退出，上次错误："+status.fault,error=true});
                } else if(!status.running&&string.IsNullOrWhiteSpace(status.fault)&&(status.petClosed||status.stoppedWithoutClose))entries.Add(new ActivityRecord {at=at,message=status.petClosed?"后台和桌宠已关闭":"后台已停止，未关闭桌宠"});
            }
            if(activityLog.ReadError!=null)entries.Insert(0,new ActivityRecord {at=DateTimeOffset.FromUnixTimeMilliseconds(ReadClock()),message="运行记录暂时无法读取，请稍后重试。",error=true});
            var shown=entries.OrderByDescending(entry=>entry.at).Take(3).ToList();var lines=shown.Select(entry=>entry.Line).ToList();
            Text("ActivityText",string.Join("\n",lines));
            E<FrameworkElement>("ActivityEmpty").Visibility=lines.Count==0?Visibility.Visible:Visibility.Collapsed;
        }
        public void Render(string path,int width=880,int height=846) {
            var surface=E<Border>("RootSurface");Refresh();surface.Measure(new Size(width,height));surface.Arrange(new Rect(0,0,width,height));surface.UpdateLayout();Refresh();surface.UpdateLayout();
            var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(surface);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using(var stream=File.Create(path)) encoder.Save(stream);
        }
        public void Dispose() {
            if(disposed)return;
            disposed=true;updateCancellation.Cancel();updateCancellation.Dispose();
            diagnostics.Dispose();
            if(releaseNotesDialog!=null)releaseNotesDialog.Window.Close();
            if(releaseNotes!=null){releaseNotes.Dispose();releaseNotes=null;}
            if(timer!=null) timer.Stop();if(tray!=null) {tray.Visible=false;tray.Dispose();} if(menu!=null) menu.Dispose();if(icon!=null) icon.Dispose();
            if(showEvent!=null) showEvent.Dispose();if(ownsMutex) {mutex.ReleaseMutex();ownsMutex=false;}if(mutex!=null) mutex.Dispose();
        }
    }
}
