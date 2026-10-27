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
        public bool Busy {get;private set;} public bool AllowExit;
        internal bool TrayExitEnabled {get {return trayExit!=null&&trayExit.Enabled;}}
        string notice,savedMode="brute"; bool selecting;
        Forms.NotifyIcon tray; Forms.ContextMenuStrip menu; Forms.ToolStripMenuItem trayPause,trayStop,trayExit,trayStatus;
        DispatcherTimer timer; Mutex mutex; EventWaitHandle showEvent; bool ownsMutex; System.Drawing.Icon icon;
        public Func<Status> ReadStatus; public Action<string> DispatchOverride;
        internal Func<bool> ConfirmReset;
        public Panel(RuntimeStore store) {
            this.store=store; Window=(Window)XamlReader.Parse(Resources.Text("Panel.xaml"));
            ReadStatus=()=>store.Read<Status>("status.json");
            var preference=store.Read<Preference>("preferences.json");
            if(preference!=null&&Policy.Modes.Contains(preference.mode)) savedMode=preference.mode;
            selecting=true; foreach(ListBoxItem item in E<ListBox>("ModeSelector").Items) if((string)item.Tag==savedMode) E<ListBox>("ModeSelector").SelectedItem=item; selecting=false;
            E<ListBox>("ModeSelector").SelectionChanged+=(s,e)=>SaveMode();
            E<Button>("StartButton").Click+=async(s,e)=>await UserAction("start");
            E<Button>("PauseButton").Click+=async(s,e)=>{var status=ReadStatus();await UserAction(status!=null&&status.paused?"resume":"pause");};
            E<Button>("StopButton").Click+=async(s,e)=>await UserAction("stop");
            E<Button>("ResetPendingButton").Click+=async(s,e)=>await ResetPending();
            E<Button>("BrowseAppButton").Click+=(s,e)=>ChoosePath(true);
            E<Button>("MinimizeButton").Click+=(s,e)=>Window.WindowState=WindowState.Minimized;
            E<Button>("CloseButton").Click+=(s,e)=>Window.Close();
            E<Button>("ExitButton").Click+=async(s,e)=>await Exit();
            E<MenuItem>("ExitMenuItem").Click+=async(s,e)=>await Exit();
            E<System.Windows.Controls.Grid>("TitleGrip").MouseLeftButtonDown+=(s,e)=>{
                if(e.ClickCount==2) Window.WindowState=Window.WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
                else if(e.LeftButton==MouseButtonState.Pressed) Window.DragMove();
            };
            Window.Closing+=(s,e)=>{if(!AllowExit){e.Cancel=true;Window.Hide();Window.ShowInTaskbar=false;Life("hidden-to-tray");}};
            Window.Closed+=(s,e)=>Life("closed");
            Window.ContentRendered+=(s,e)=>Life("visible");
            var path=Platform.Discover(store);SetPath(path);
            Refresh();
        }
        public T E<T>(string name) where T:class { var value=Window.FindName(name) as T; if(value==null) throw new InvalidOperationException("界面元素缺失："+name);return value; }
        void Text(string name,string text) { E<TextBlock>(name).Text=text; }
        public void SetPath(string path) {Text("AppPathText",path==null?"桌宠：启动时自动查找，也可手动选择":"桌宠："+path);E<TextBlock>("AppPathText").ToolTip=path;}
        void Life(string phase) { RuntimeStore.Append(store.FilePath("panel-lifecycle.log"),DateTimeOffset.Now.ToString("o")+" pid="+Process.GetCurrentProcess().Id+" "+phase+"\n"); }
        public static string InstanceKey(string root) {
            using(var sha=SHA256.Create()) using(var user=WindowsIdentity.GetCurrent())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(root+"|"+user.User.Value))).Replace("-","").Substring(0,16);
        }
        public bool ClaimInstance() {
            var key=InstanceKey(store.Root);mutex=new Mutex(false,"Local\\TappoPanel-"+key);showEvent=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\TappoPanelShow-"+key);
            try {ownsMutex=mutex.WaitOne(0);} catch(AbandonedMutexException){ownsMutex=true;}
            if(!ownsMutex) showEvent.Set(); return ownsMutex;
        }
        public void Show() {Window.ShowInTaskbar=true;Window.Show();Window.WindowState=WindowState.Normal;Window.Activate();Life("restored");}
        public void InitializeTray() {
            menu=new Forms.ContextMenuStrip();trayStatus=(Forms.ToolStripMenuItem)menu.Items.Add("后台：未运行");trayStatus.Enabled=false;
            menu.Items.Add("打开控制面板",null,(s,e)=>Show());
            trayPause=(Forms.ToolStripMenuItem)menu.Items.Add("暂停自动化",null,async(s,e)=>{var status=ReadStatus();await UserAction(status!=null&&status.paused?"resume":"pause");});
            trayStop=(Forms.ToolStripMenuItem)menu.Items.Add("停止并恢复普通桌宠",null,async(s,e)=>await UserAction("stop"));
            menu.Items.Add(new Forms.ToolStripSeparator());trayExit=(Forms.ToolStripMenuItem)menu.Items.Add("退出助手",null,async(s,e)=>await Exit());
            using(var stream=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("helper.ico")) icon=new System.Drawing.Icon(stream);
            tray=new Forms.NotifyIcon {Icon=icon,ContextMenuStrip=menu,Text="Wuwa Tappo 助手",Visible=true};tray.DoubleClick+=(s,e)=>Show();
        }
        public void StartTimer() {
            timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
            timer.Tick+=(s,e)=>{if(showEvent!=null&&showEvent.WaitOne(0)) Show();Refresh();};timer.Start();
        }
        bool ChoosePath(bool force) {
            try {
                var path=force?null:Platform.Discover(store);
                if(path==null) {
                    var dialog=new Microsoft.Win32.OpenFileDialog {Title="选择已安装的 WuwaTappo.exe",Filter="Wuwa Tappo (WuwaTappo.exe)|WuwaTappo.exe",CheckFileExists=true};
                    if(dialog.ShowDialog(Window)!=true) return false;path=dialog.FileName;
                }
                Platform.SavePath(store,path);SetPath(path);return true;
            } catch(Exception error) {notice=error.Message;Refresh();return false;}
        }
        void SaveMode() {
            if(selecting) return;
            try {
                var item=E<ListBox>("ModeSelector").SelectedItem as ListBoxItem;var mode=item==null?null:item.Tag as string;
                if(!Policy.Modes.Contains(mode)) throw new InvalidOperationException("请选择有效模式。");
                store.Write("preferences.json",new Preference {mode=mode});savedMode=mode;notice="模式已保存。";
            } catch(Exception error) {
                notice="模式保存失败："+error.Message;selecting=true;
                try {foreach(ListBoxItem item in E<ListBox>("ModeSelector").Items) if((string)item.Tag==savedMode) E<ListBox>("ModeSelector").SelectedItem=item;}
                finally {selecting=false;}
            }
            Refresh();
        }
        public static bool Alive(Status status) {return status!=null&&status.running&&RuntimeStore.Alive(status.pid);}
        internal static void PrepareStart(RuntimeStore store) {
            var old=store.Read<Status>("lock.json");
            if(old!=null&&RuntimeStore.Alive(old.pid)) throw new InvalidOperationException("自动化已运行，请先停止现有后台。");
            store.Write("control.json",new Command {id=Guid.NewGuid().ToString(),action="resume"});
        }
        public async Task Act(string action,string pendingHash=null) {
            if(Busy) return;
            if(DispatchOverride!=null) {DispatchOverride(action);return;}
            if(action=="start"&&!ChoosePath(false)) return;
            Busy=true;notice=null;Refresh();var requested=DateTimeOffset.UtcNow;
            string commandId=Guid.NewGuid().ToString();
            try {
                if(action=="reset"&&!Alive(ReadStatus())) {
                    using(var guard=new FileStream(store.FilePath("worker.guard"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) store.ResolvePending(pendingHash);
                    notice="未确认记录已归档，原操作不会重发；可点击启动后台。";return;
                }
                if(action=="start") {
                    PrepareStart(store);
                    var info=new ProcessStartInfo(Platform.Executable,"--worker") {WorkingDirectory=store.Root,UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden};
                    if(!Platform.Administrator) info.Verb="runas";
                    using(var process=Process.Start(info)) {
                        if(process==null) throw new InvalidOperationException("后台未启动。");
                        store.Write("launcher-status.json",new LauncherStatus {requestedAt=requested.ToString("o"),runnerId=process.Id});
                    }
                } else {
                    var status=ReadStatus();if(!Alive(status)) throw new InvalidOperationException("后台未运行。");
                    if(action=="resume"&&store.Exists("pending.json")) throw new InvalidOperationException("存在结果未确认的操作，已阻止恢复。");
                    store.Write("control.json",new Command {id=commandId,action=action,pendingHash=pendingHash});
                }
                var until=RuntimeStore.Now+(action=="start"?75000:40000);
                while(RuntimeStore.Now<until) {
                    await Task.Delay(300);var status=ReadStatus();
                    var launcher=store.Read<LauncherStatus>("launcher-status.json");DateTimeOffset failed;
                    if(launcher!=null&&launcher.error!=null&&DateTimeOffset.TryParse(launcher.failedAt,out failed)&&failed>=requested) throw new InvalidOperationException(launcher.error);
                    DateTimeOffset at;
                    if(status==null||!DateTimeOffset.TryParse(status.at,out at)||at<requested) continue;
                    if(action=="reset"&&status.resetCommandId==commandId) {
                        if(status.resetError!=null) throw new InvalidOperationException(status.resetError);
                        notice="未确认记录已归档，已恢复自动化。";return;
                    }
                    if(action=="start"&&Alive(status)||action=="pause"&&Alive(status)&&status.paused||action=="resume"&&Alive(status)&&!status.paused) return;
                    if((action=="stop"||action=="disconnect")&&!status.running) {
                        if(action=="disconnect"&&!status.automationDisconnected) throw new InvalidOperationException("自动化停止尚未确认。");
                        if(action=="stop"&&!status.normalStartupRestored) throw new InvalidOperationException(status.fault??"后台已停止，但桌宠未恢复普通启动。");
                        return;
                    }
                    if(action=="start"&&!status.running&&status.fault!=null) throw new InvalidOperationException(status.fault);
                }
                throw new TimeoutException("操作尚未确认完成，请核对管理员提示与后台状态，不要重复启动。");
            } catch(Exception error) {notice=error.Message;throw;}
            finally {Busy=false;Refresh();}
        }
        async Task UserAction(string action) {
            try {await Act(action);}
            catch(Exception error) {
                try {store.Log("panel-action-failed",new {action=action,errorType=error.GetType().Name,message=error.Message});} catch { }
                Refresh();
            }
        }
        internal async Task ResetPending() {
            if(Busy) return;
            try {
                var status=ReadStatus();
                if(!store.Exists("pending.json")) {notice="当前没有待确认操作。";return;}
                if(Alive(status)&&(!status.paused||status.fault==null)) throw new InvalidOperationException("操作仍在确认中，请等待完成后再处理。");
                var hash=store.PendingHash();
                var confirmed=ConfirmReset!=null?ConfirmReset():MessageBox.Show(Window,
                    "该操作可能已经执行，但结果尚未确认。\n\n继续后会归档原记录，不重发该操作；后台已连接时会恢复自动化，否则需手动启动。\n不会清零次数、宝箱进度或日志。\n\n是否结束这笔待确认操作？",
                    "结束待确认操作",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
                if(confirmed) await Act("reset",hash);
            } catch(Exception error) {notice=error.Message;}
            finally {Refresh();}
        }
        public async Task Exit() {
            if(Busy) {notice="正在处理操作，请等待完成后退出。";Show();return;}
            try {
                if(Alive(ReadStatus())) await Act("disconnect");
                AllowExit=true;Window.Close();if(Application.Current!=null) Application.Current.Shutdown();
            } catch(Exception error) {notice="退出未完成："+error.Message;Refresh();Show();}
        }
        public void Refresh() {
            try {
                var s=ReadStatus();var alive=Alive(s);DateTimeOffset at;
                var stale=alive&&(!DateTimeOffset.TryParse(s.at,out at)||(DateTimeOffset.UtcNow-at).TotalSeconds>45);
                var pending=store.Exists("pending.json");
                string label="未运行",color="#EAE7E1",message="后台助手尚未运行",detail="先退出普通启动的桌宠，再点击“启动后台”。";
                if(alive) {
                    label="运行中";color="#E2EFDD";message="后台正在照看桌宠";detail="自动补足领取所需次数，并按原有冷却检查宝箱。";
                    if(s.readRetryAt.HasValue) {label="等待重试";color="#FFF0D1";message="状态查询失败，正在等待自动重试";detail=(s.readRetryError??"状态暂时无法读取")+"；约 "+Math.Max(0,(long)Math.Ceiling((s.readRetryAt.Value-RuntimeStore.Now)/1000.0))+" 秒后重试，期间不补数、不领取。";}
                    if(s.paused) {label="已暂停";color="#FFF0D1";message="自动操作已暂停";detail="计数与领取已暂停，连接仍保留。";}
                    if(pending&&!s.paused&&s.fault==null) {label="处理中";color="#FFF0D1";message="正在确认操作结果";detail="等待桌宠与服务器同步，期间不会重复提交。";}
                    if(s.fault!=null||pending&&s.paused) {label="需处理";color="#F8E0D8";message="后台已暂停，等待确认";detail=s.fault??"上次操作结果待确认，请先停止并核对桌宠。";}
                    if(stale) {label="状态未更新";color="#FFF0D1";message="暂时没有收到新的后台状态";detail="以下数值是上次记录，不代表当前状态。";}
                } else if(s!=null&&s.normalStartupRestored) {message="桌宠已恢复普通运行";detail="要重新开启助手，请先从桌宠设置或托盘退出应用，再点击启动。";}
                else if(s!=null&&s.running) {message="后台进程已退出";detail="当前显示上次记录，请核对桌宠状态。";}
                if(Busy) {label="处理中";message="正在等待后台完成操作";detail="请按需处理管理员提示，不要重复启动。";}
                Text("StatusLabel",label);E<Border>("StatusPill").Background=(Brush)new BrushConverter().ConvertFromString(color);
                Text("MainMessage",message);Text("DetailMessage",notice??detail);
                E<Button>("StartButton").IsEnabled=!alive&&!Busy;E<Button>("PauseButton").IsEnabled=alive&&!Busy&&!pending;
                E<Button>("PauseButton").Content=s!=null&&s.paused?"继续运行":"暂停";
                E<Button>("StopButton").IsEnabled=alive&&!Busy;E<Button>("BrowseAppButton").IsEnabled=!alive&&!Busy;
                E<Button>("ResetPendingButton").Visibility=pending?Visibility.Visible:Visibility.Collapsed;
                E<Button>("ResetPendingButton").IsEnabled=pending&&!Busy&&(!alive||!stale&&s.paused&&s.fault!=null);
                Text("ActionHint",Busy?"处理中…":"每秒刷新");
                var state=s==null?null:s.state;
                Text("AvailableValue",state==null||!state.available.HasValue?"—":state.available.Value.ToString("N0"));
                Text("TotalValue",state==null||!state.cumulative.HasValue?"—":state.cumulative.Value.ToString("N0"));
                Text("ClaimsValue",state==null?"—":s.claimsThisRun.ToString("N0"));
                Text("CountCaption",state==null?"等待首次读取":alive&&!stale?"来自后台最近一次读取":"上次运行的记录");Text("ClaimsCaption",alive?"本次运行领取":"上次运行领取");
                var names=new Dictionary<string,string>{{"brute","暴力模式"},{"clicker","连点器模式"},{"human","人类模式"}};
                var behavior=savedMode=="brute"?"需要补数时一次补足至 2,000 次。":"提前累积至 2,000 次，到期后逐个领取。";
                Text("ModeHint",!alive?"已选择："+names[savedMode]+"，启动后台后生效。"+behavior:s.mode!=savedMode?"已保存："+names[savedMode]+"，等待后台应用。":"已应用："+names[savedMode]+(s.paused||s.fault!=null||pending?"，当前已暂停或正在确认操作。":"。"+behavior));
                var clock=s==null?null:s.chestTimes;
                bool valid=clock!=null&&clock.updatedAt.HasValue&&clock.left.HasValue&&clock.right.HasValue&&clock.error==null&&RuntimeStore.Now-clock.updatedAt>=0&&RuntimeStore.Now-clock.updatedAt<=90000;
                Text("SnapshotLabel",!alive||stale?"等待连接":clock!=null&&clock.error!=null?"宝箱时间查询失败":valid?"接口同步 "+DateTimeOffset.FromUnixTimeMilliseconds(clock.updatedAt.Value).ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss"):"等待接口同步");
                Chest("Left","left",s,alive&&!stale,valid);Chest("Right","right",s,alive&&!stale,valid);
                Activity();
                if(tray!=null) {
                    tray.Text="Wuwa Tappo 助手 · "+label;trayStatus.Text="后台："+label;
                    trayPause.Text=s!=null&&s.paused?"继续运行":"暂停自动化";trayPause.Enabled=alive&&!Busy&&!pending;
                    trayStop.Enabled=alive&&!Busy;trayExit.Enabled=true;trayExit.Text=alive?"停止自动化并退出助手":"退出助手";
                }
            } catch(Exception error) {
                Text("MainMessage","状态暂时无法读取");Text("DetailMessage",error is IOException?"正在等待状态文件更新；不会自动重试领取。":error.Message);
                E<Button>("StartButton").IsEnabled=false;E<Button>("PauseButton").IsEnabled=false;E<Button>("StopButton").IsEnabled=false;
                E<Button>("ResetPendingButton").IsEnabled=false;
                if(tray!=null) {trayPause.Enabled=false;trayStop.Enabled=false;trayExit.Enabled=true;trayStatus.Text="后台：状态读取失败";}
            }
        }
        void Chest(string prefix,string side,Status s,bool alive,bool valid) {
            var state=s==null?null:s.state;double progress=0;string text="未连接",tag="未连接",hint="";
            if(alive&&state!=null&&state.loggedIn) {
                if(!valid) {text="等待同步";tag="接口时间未就绪";hint=s.chestTimes==null?"等待服务端宝箱时间":s.chestTimes.error??"等待服务端宝箱时间，暂不显示旧倒计时";}
                else {
                    var target=side=="left"?s.chestTimes.left:s.chestTimes.right;var remaining=Math.Max(0,(long)Math.Ceiling((target.Value-RuntimeStore.Now)/1000.0));
                    if(remaining>0) {text=(remaining/60).ToString("00")+":"+(remaining%60).ToString("00");tag="计时中";hint="服务端冷却时间，每 30 秒同步；显示每秒更新";progress=Math.Max(0,Math.Min(1,1-remaining/1800.0));}
                    else {text=state.Button(side)?"已就绪":"冷却已结束";tag=state.Button(side)?"待领取":"等待同步";hint=s.paused?"恢复运行后继续检查领取":"由后台核对并按顺序领取";progress=1;}
                }
            }
            Text(prefix+"Time",text);Text(prefix+"Tag",tag);Text(prefix+"Hint",hint);
            E<Border>(prefix+"Fill").Width=Math.Max(0,E<Border>(prefix+"Track").ActualWidth*progress);
        }
        void Activity() {
            var path=store.FilePath("events.jsonl");var lines=new List<string>();
            if(File.Exists(path)) {
                foreach(var row in Tail(path,12).Reverse()) {
                    try {
                        var ev=Json.Decode<Dictionary<string,object>>(row);var name=ev["event"] as string;string text=null;
                        if(name=="started") text="后台已连接";
                        else if(name=="claim-confirmed") text="已领取"+((string)ev["side"]=="left"?"宝箱一":"宝箱二")+" · 消耗 1,000 次";
                        else if(name=="boost-confirmed") text="已补充 "+ev["delta"]+" 次";
                        else if(name=="paused-on-error") text="后台暂停，等待处理";
                        else if(name=="pending-resolved") text="已归档待确认操作，原操作不重发";
                        else if(name=="read-query-retry") text="状态查询失败，等待自动重试";
                        else if(name=="read-query-recovered") text="状态查询已恢复";
                        else if(name=="cleanup-needs-attention") text="停止后需要检查桌宠状态";
                        if(text!=null) lines.Add(DateTimeOffset.Parse((string)ev["at"]).ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm")+"    "+text);
                        if(lines.Count>=3) break;
                    } catch { }
                }
            }
            Text("ActivityHeading",lines.Count==0?"最近活动 · 暂无记录":"最近活动");Text("ActivityText",string.Join("\n",lines));
            E<TextBlock>("ActivityText").Visibility=lines.Count==0?Visibility.Collapsed:Visibility.Visible;
        }
        static IEnumerable<string> Tail(string path,int count) {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                long start=stream.Length;int lines=0;
                while(start>0) {stream.Position=--start;if(stream.ReadByte()==10&&++lines>count) {start++;break;}}
                stream.Position=start;using(var reader=new StreamReader(stream,Encoding.UTF8,true)) return reader.ReadToEnd().Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
            }
        }
        public void Render(string path,int width=960,int height=900) {
            var surface=E<Border>("RootSurface");Refresh();surface.Measure(new Size(width,height));surface.Arrange(new Rect(0,0,width,height));surface.UpdateLayout();Refresh();surface.UpdateLayout();
            var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(surface);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using(var stream=File.Create(path)) encoder.Save(stream);
        }
        public void Dispose() {
            if(timer!=null) timer.Stop();if(tray!=null) {tray.Visible=false;tray.Dispose();} if(menu!=null) menu.Dispose();if(icon!=null) icon.Dispose();
            if(showEvent!=null) showEvent.Dispose();if(ownsMutex) {mutex.ReleaseMutex();ownsMutex=false;}if(mutex!=null) mutex.Dispose();
        }
    }
}
