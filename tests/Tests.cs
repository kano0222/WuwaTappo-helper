using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Tappo {
    internal sealed class FakeClient : IAppClient {
        bool connected=true;
        public bool Connected {get{return connected;}set{connected=value;}}
        public Snapshot State=Tests.State(); public int Reads,Boosts,Claims,Quits,Closed,Recoveries; public bool ThrowClaim,OmitReward;
        public Func<Snapshot> ClaimResult,SnapshotResult;
        public Func<Task<Snapshot>> SnapshotTask;
        public Func<ServerState> ServerResult;
        public Func<string> VersionResult;
        public Func<Journal,Snapshot> RecoveryResult;
        public Func<Journal,string,PairResult> PairResult;
        public bool SupportsClaimPair {get{return PairResult!=null;}}
        public Task<PairResult> ClaimPair(Journal journal,string command) {return Task.FromResult(PairResult(journal,command));}
        public int QueryRetries; public Func<int> RetryQueryResult;
        public Task<int> RetryQuery() {QueryRetries++;return Task.FromResult(RetryQueryResult==null?0:RetryQueryResult());}
        public Task<Snapshot> Recover(Journal journal) {Recoveries++;return Task.FromResult(RecoveryResult==null?new Snapshot {accountKey=State.accountKey,operationId=journal.id,outcome="unknown",failureStage="receipt-missing"}:RecoveryResult(journal));}
        public Task<string> CheckVersion() {return Task.FromResult(VersionResult==null?State.appVersion:VersionResult());}
        public Func<int,Snapshot,Snapshot> BoostResult;
        public Task<Snapshot> Snapshot() {return SnapshotTask==null?Task.FromResult(SnapshotResult==null?State:SnapshotResult()):SnapshotTask();}
        public Task<ServerState> Server() {Reads++;return Task.FromResult(ServerResult==null?new ServerState {accountKey=State.accountKey,total=State.cumulative,consumed=State.consumed,left=State.left,right=State.right}:ServerResult());}
        public Task<Snapshot> Boost(int delta,Snapshot before) {Boosts++;if(BoostResult!=null)return Task.FromResult(BoostResult(delta,before));var result=Tests.Copy(before);result.cumulative+=delta;result.available+=delta;State=result;return Task.FromResult(result);}
        public Task<Snapshot> Claim(Journal journal) {var before=journal.before;Claims++;if(ThrowClaim) throw new TimeoutException("调用超时，禁止自动重发操作");var result=ClaimResult==null?Tests.Copy(before):ClaimResult();result.operationId=before.operationId;result.rewardVerified=!OmitReward;result.reward=OmitReward?null:new RewardReceipt {assetId=123,assetName="test",assetType="accessory",quantityBefore=0,quantityAfter=1};State=result;return Task.FromResult(result);}
        public Func<Task> QuitResult;
        public Task Quit() {Quits++;return QuitResult==null?Task.FromResult(0):QuitResult();}
        public void Dispose() {Closed++;}
    }
    internal static class Tests {
        static int checks;static string root;
        public static Snapshot State() {return new Snapshot {accountKey="test-key",time=5000,loggedIn=true,available=2000,cumulative=4000,consumed=2000,left=6000,right=7000,buttons=new Dictionary<string,bool>{{"left",true},{"right",true}}};}
        public static Snapshot Copy(Snapshot s) {return Json.Decode<Snapshot>(Json.Encode(s));}
        static void Check(bool value,string message) {if(!value) throw new Exception(message);checks++;}
        static void Reject(Action action,string message) {bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,message);}
        static RuntimeStore Store() {return new RuntimeStore(Path.Combine(root,Guid.NewGuid().ToString("N")));}
        [STAThread] public static int Main(string[] args) {
            if(args.Length==2&&args[0]=="--diagnostic-process")return DiagnosticEvidenceChecks.Fixture(args[1]);
            if(args.Length>=2&&args[0]=="--hosted-webview-fixture")return HostedWebViewChecks.Fixture(args);
            if(args.Length>=2&&args[0]=="--pet-fixture")return PetLifecycleChecks.Fixture(args[1]);
            if(args.Length==2&&args[0]=="--apply-update")return UpdateInstaller.Apply(args[1]);
            if(args.Length==2&&args[0]=="--update-participant") {
                var isolatedRoot=AppDomain.CurrentDomain.BaseDirectory;
                var isolatedStore=Instances.Open(isolatedRoot,args[1]=="primary"?null:args[1]);
                var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
                using(var panel=new Panel(isolatedStore,preview:true)) {
                    if(!panel.ClaimInstance())return 2;
                    panel.StartTimer();
                    isolatedStore.Write("update-ready.json",new {ready=true});
                    var timeout=new DispatcherTimer {Interval=TimeSpan.FromSeconds(30)};timeout.Tick+=(s,e)=>app.Shutdown(3);timeout.Start();
                    app.Run();timeout.Stop();
                    if(!panel.AllowExit)return 3;
                    isolatedStore.Write("update-stopped.json",new {stopped=true});return 0;
                }
            }
            if(File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"update-test-mode"))&&(args.Length==0||args.Length==2&&args[0]=="--instance")) {
                var isolatedStore=Instances.Open(AppDomain.CurrentDomain.BaseDirectory,args.Length==0?null:args[1]);
                isolatedStore.Write("update-restarted.json",new {readme=File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"README.md"))});return 0;
            }
            var startupCheck=Array.IndexOf(args,"--startup-args-check");
            if(startupCheck>=0&&startupCheck+2<args.Length) {
                var debug=args[startupCheck+1]=="debug";
                var expected=args[startupCheck+2]=="-"?null:Encoding.UTF8.GetString(Convert.FromBase64String(args[startupCheck+2]));
                var switches=args.Where(a=>a.StartsWith("--edge-webview-switches=",StringComparison.Ordinal)).ToArray();
                return Environment.GetEnvironmentVariable(StartupChecks.ArgumentsVariable)==expected&&
                    (debug?switches.Length==1&&switches[0]=="--edge-webview-switches="+StartupChecks.DebugArguments:switches.Length==0)?0:1;
            }
            if(args.Length==2&&args[0]=="--update-probe") {
                try {var update=Updates.Check(CancellationToken.None).GetAwaiter().GetResult();File.WriteAllText(args[1],Json.Encode(new {current=Updates.CurrentVersion,build=Updates.BuildVersion,latest=update.version,available=update.available,url=update.url}));return 0;}
                catch(Exception error){Console.Error.WriteLine("TLS="+System.Net.ServicePointManager.SecurityProtocol);while(error!=null){Console.Error.WriteLine(error.GetType().Name+": "+error.Message);error=error.InnerException;}return 1;}
            }
            if(args.Length==2&&args[0]=="--process-check") {
                var alive=RuntimeStore.Alive(int.Parse(args[1]));Console.WriteLine("Query-only process check: "+alive);return alive?0:2;
            }
            if(args.Length==1&&args[0]=="--dual-probe") {
                foreach(var process in Process.GetProcessesByName("WuwaTappo"))using(process) {
                    var path=Platform.ProcessPath(process.Id);
                    Console.WriteLine("pid="+process.Id+" installation="+(path==null?"unavailable":new DirectoryInfo(Path.GetDirectoryName(path)).Name));
                    if(path!=null)try {Console.WriteLine("verifiedPort="+Instances.PortFor(path));}catch(Exception error){Console.WriteLine(error.Message);}
                }
                return 0;
            }
            if(args.Length==2&&args[0]=="--panel-lease-check") {
                using(var panel=new Panel(new RuntimeStore(args[1]),preview:true))return panel.ClaimInstance()?0:2;
            }
            if(args.Length==2&&args[0]=="--instance-lease-check") {
                try {using(Instances.Acquire(args[1]))return 0;}catch(InvalidOperationException){return 2;}
            }
            if(args.Length==2&&args[0]=="--panel-child") return PanelChild(args[1]);
            var suite=args.Length==0?"All":args.Length==2&&args[0]=="--suite"?args[1]:null;
            suite=new[]{"All","Worker","Panel","Platform"}.FirstOrDefault(value=>string.Equals(value,suite,StringComparison.OrdinalIgnoreCase));
            if(suite==null) {Console.Error.WriteLine("Use --suite All|Worker|Panel|Platform");return 1;}
            root=Path.Combine(Path.GetTempPath(),"tappo-native-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try {
                if(suite=="All"||suite=="Worker") {
                    Policies();Files();checks+=ClaimHistoryChecks.Run();checks+=DiagnosticEvidenceChecks.Run();Clocks().GetAwaiter().GetResult();Workers().GetAwaiter().GetResult();WakeReplenishment().GetAwaiter().GetResult();BoostObservations().GetAwaiter().GetResult();Resets().GetAwaiter().GetResult();QueryRetries().GetAwaiter().GetResult();Connections().GetAwaiter().GetResult();
                    ServerChestConfirmation().GetAwaiter().GetResult();WaitForServerClaimBalance().GetAwaiter().GetResult();VersionPreflight().GetAwaiter().GetResult();
                    StartupChestClock().GetAwaiter().GetResult();PendingChestClock().GetAwaiter().GetResult();
                    FixedPairWorker().GetAwaiter().GetResult();
                    PairScheduling().GetAwaiter().GetResult();
                    checks+=ConnectionChecks.Run().GetAwaiter().GetResult();checks+=RetryChecks.Run().GetAwaiter().GetResult();checks+=ResumeChecks.Run().GetAwaiter().GetResult();checks+=RecoveryChecks.Run().GetAwaiter().GetResult();checks+=CapabilityChecks.Run().GetAwaiter().GetResult();
                }
                if(suite=="All"||suite=="Panel") {ResetPanels();Layouts();ClaimActivities();CloseChoices();Lifecycle();checks+=ConnectionChecks.Panels();UpdatePanels();StartupPanels();TrayRunControls();checks+=ConsoleUiChecks.Run();checks+=PanelMessagingChecks.Run();checks+=PanelLifetimeChecks.Run();checks+=DiagnosticChecks.Run();checks+=AutoRunChecks.Run();checks+=ReleaseNotesChecks.Run();}
                if(suite=="All"||suite=="Platform") {checks+=StartupChecks.Run();checks+=PetLifecycleChecks.Run();checks+=TransportChecks.Run().GetAwaiter().GetResult();checks+=UpdateChecks.Run().GetAwaiter().GetResult();checks+=InstallerChecks.Run().GetAwaiter().GetResult();}
                Console.WriteLine("PASS: "+checks+" native checks ("+suite+"); no account mutations, app restarts, or system policy changes.");return 0;
            } catch(Exception error) {Console.Error.WriteLine(error);return 1;}
            finally {
                var target=Path.GetFullPath(root);var temp=Path.GetFullPath(Path.GetTempPath());
                if(target.StartsWith(temp,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(target).StartsWith("tappo-native-tests-")) Directory.Delete(target,true);
            }
        }
        static void ClaimActivities() {
            var store=Store();
            var receivedAt=RuntimeStore.Now-10000;
            store.Log("claim-pair-slot",new {side="left",operationId="left",outcome="success",receivedAt});
            store.Log("claim-pair-slot",new {side="right",operationId="skipped",outcome="skipped"});
            store.Log("claim-received",new {side="right",operationId="right",receivedAt=receivedAt+2000});
            store.Log("boost-confirmed",new {delta=100});
            using(var panel=new Panel(store,preview:true)) {
                panel.Refresh();
                var activity=panel.E<TextBlock>("ActivityText");
                var pending=activity.Text;
                var lines=pending.Split('\n');
                Check(lines.Length==3&&lines[0].EndsWith("已补充 100 次")&&
                    lines[1].EndsWith("已领取宝箱二 · 确认中")&&lines[2].EndsWith("已领取宝箱一 · 确认中"),
                    "activity did not show both successful receipts in time order");
                store.Log("claim-confirmed",new {side="left",operationId="left",receivedAt});
                panel.Refresh();
                Check(activity.Text==pending.Replace("宝箱一 · 确认中","宝箱一 · 消耗 1,000 次"),
                    "pair confirmation changed the original time or added another activity");
                store.Log("claim-confirmed",new {side="right",operationId="right",receivedAt=receivedAt+2000});
                store.Log("claim-confirmed",new {side="right",operationId="right",receivedAt=receivedAt+2000});
                panel.Refresh();
                Check(activity.Text==pending.Replace(" · 确认中"," · 消耗 1,000 次"),
                    "single confirmation changed the original time or added another activity");
            }
        }
        static void TrayRunControls() {
            var store=Store();using(var panel=new Panel(store,preview:true)) {
                var now=RuntimeStore.Now;Status status=null;string command=null;
                panel.ReadStatus=()=>status;panel.ReadClock=()=>now;panel.DispatchOverride=action=>command=action;panel.InitializeTray();
                var menu=(System.Windows.Forms.ContextMenuStrip)typeof(Panel).GetField("menu",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(panel);
                var run=(System.Windows.Forms.ToolStripMenuItem)typeof(Panel).GetField("trayRun",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(panel);
                var exit=(System.Windows.Forms.ToolStripMenuItem)typeof(Panel).GetField("trayExit",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(panel);
                Check(!menu.Items.Cast<System.Windows.Forms.ToolStripItem>().Any(item=>item.Text.Contains("暂停")||item.Text.Contains("停止并")||item.Text=="停止后台"),"tray retains obsolete pause/stop entries");
                Check(run.Text=="启动后台"&&run.Enabled&&exit.Text=="退出助手","idle tray actions do not match the panel");
                run.PerformClick();Check(command=="start","idle tray does not start the background");
                status=new Status {running=true,pid=Process.GetCurrentProcess().Id,at=DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("o"),connected=true};panel.Refresh();command=null;
                Check(run.Text=="停止运行"&&run.Enabled==panel.E<Button>("StartButton").IsEnabled&&exit.Text=="退出助手","running tray actions do not match the panel");
                status.paused=true;status.manuallyPaused=true;
                run.PerformClick();Check(command=="pause","changed status reverses the rendered tray stop action");
                panel.Refresh();command=null;
                Check(run.Text=="启动后台"&&run.Enabled,"stopped tray action does not offer start");
                run.PerformClick();Check(command=="resume","stopped tray action does not resume the existing background");
                store.Write("pending.json",new {historical=true});panel.Refresh();command=null;
                run.PerformClick();Check(!run.Enabled&&!panel.E<Button>("StartButton").IsEnabled&&command==null,"tray bypasses pending-operation safeguards");
                store.ClearPending();status.at=DateTimeOffset.FromUnixTimeMilliseconds(now-60000).ToString("o");panel.Refresh();
                Check(!run.Enabled&&!panel.E<Button>("StartButton").IsEnabled,"stale status permits tray resume");
                panel.ReadStatus=()=>{throw new IOException("fixture read failure");};panel.Refresh();
                Check(!run.Enabled&&exit.Enabled&&exit.Text=="退出助手","failed status read leaves inconsistent tray controls");
            }
        }
        static void StartupPanels() {
            var store=Store();
            var startup=new LogonStartup(@"C:\测试目录\helper folder\WuwaTappoHelper.exe",store);
            string value=null;int writes=0;
            startup.ReadValue=()=>value;startup.WriteValue=text=>{value=text;writes++;};
            Check(!startup.Enabled,"autostart default enabled");
            Check(startup.Command=="\"C:\\测试目录\\helper folder\\WuwaTappoHelper.exe\"","autostart path not quoted");
            var other=new LogonStartup(@"C:\helper.exe",Instances.Open(store.Root,new string('A',64)));
            Check(other.Name!=startup.Name&&other.Command.EndsWith(" --instance "+new string('A',64)),"autostart instances share entry or lose binding");
            using(var panel=new Panel(store,preview:true)) {
                panel.ReadStartup=()=>startup.Enabled;panel.WriteStartup=startup.SetEnabled;
                panel.InitializeTray();
                var toggle=panel.E<CheckBox>("StartupCheckBox");
                toggle.IsChecked=true;toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Check(startup.Enabled&&toggle.IsChecked==true&&panel.TrayStartup.Checked,"panel startup switch not synchronized");
                panel.TrayStartup.PerformClick();
                Check(value==null&&toggle.IsChecked==false&&!panel.TrayStartup.Checked,"tray startup switch not synchronized");
                value=startup.Command;panel.Refresh();
                Check(toggle.IsChecked==true&&panel.TrayStartup.Checked&&writes==2,"external startup change not reflected or refresh writes settings");
                startup.WriteValue=text=>{throw new UnauthorizedAccessException("denied");};
                panel.TrayStartup.PerformClick();
                Check(toggle.IsChecked==true&&panel.TrayStartup.Checked,"failed startup write changed state");
                Check(panel.E<TextBlock>("DetailMessage").Text=="未能保存开机自启设置，请重试。","startup write failure missing or exposes system details");
                panel.ReadStartup=()=>{throw new IOException("unavailable");};panel.Refresh();
                Check(!toggle.IsEnabled&&!panel.TrayStartup.Enabled&&toggle.IsChecked==true,"startup read failure lost state or remained editable");
                Check(toggle.ToolTip==null&&string.IsNullOrEmpty(panel.TrayStartup.ToolTipText),"startup error retains a tooltip");
                panel.ReadStartup=()=>startup.Enabled;panel.Refresh();
                Check(toggle.IsEnabled&&panel.TrayStartup.Enabled,"startup read recovery remained disabled");
                Check(!File.Exists(store.FilePath("control.json")),"startup switch dispatched background command");
            }
            startup.WriteValue=text=>{};value=null;
            Reject(()=>startup.SetEnabled(true),"startup failed readback accepted");
            var longPath=new LogonStartup("C:\\"+new string('x',245)+"\\WuwaTappoHelper.exe",store);
            longPath.ReadValue=()=>null;longPath.WriteValue=text=>{throw new Exception("write should not run");};
            try {longPath.SetEnabled(true);throw new Exception("long startup command accepted");}
            catch(InvalidOperationException error){Check(error.Message.Contains("路径过长"),"long command reached registry write");}
        }
        static void Policies() {
            var s=State();Func<double> random=()=>0.5;
            var legacy=Json.Decode<Preference>("{\"mode\":\"clicker\"}");legacy.Validate();
            Check(legacy.reserveTarget==2000&&legacy.clickerRate==100,"legacy preference defaults");
            foreach(var target in Preference.ReserveTargets) foreach(var rate in Preference.ClickerRates) {
                s=State();s.available=0;
                var plan=Policy.Choose(s,"clicker",true,random,target,rate);
                Check(plan.delta==rate&&plan.delayMs==1000,"configured clicker rate");
                s.available=target-1;Check(Policy.Choose(s,"clicker",true,random,target,rate).delta==rate,"last clicker batch truncated");
                Check(Policy.Choose(s,"human",true,random,target,rate).delta==5,"last human batch truncated");
                s.available=target-1+rate;Check(Policy.Choose(s,"clicker",true,random,target,rate).kind=="wait","clicker continued after overshoot");
                s.available=target+4;Check(Policy.Choose(s,"human",true,random,target,rate).kind=="wait","human continued after overshoot");
                s.available=target;Check(Policy.Choose(s,"clicker",true,random,target,rate).kind=="wait","configured target reached");
                s.available=target+200;Check(Policy.Choose(s,"human",true,random,target,rate).kind=="wait","lower target consumed balance");
            }
            foreach(var targetValue in new[]{1000,12345,99999999})foreach(var speed in new[]{1,1234,2000}) {
                s=State();s.available=targetValue-1;
                var custom=Policy.Choose(s,"brute",true,random,targetValue,speed);
                Check(custom.delta==speed&&custom.delayMs==1000,"custom brute batch or cadence");
                s.available+=speed;Check(Policy.Choose(s,"brute",true,random,targetValue,speed).kind=="wait","brute target stop");
                Check(Policy.Choose(State(),"brute",false,random,targetValue,speed).kind=="wait","brute throttle");
            }
            foreach(var invalid in new[]{0,-1,999,100000000,int.MaxValue})Reject(()=>new Preference {mode="brute",bruteReserveTarget=invalid}.Validate(),"invalid brute target accepted");
            foreach(var invalid in new[]{0,-1,2001,int.MaxValue})Reject(()=>new Preference {mode="brute",bruteRate=invalid}.Validate(),"invalid brute speed accepted");
            s=State();s.available=0;s.left=s.time;
            Check(Policy.Choose(s,"brute",true,random,10000,1000).delta==1000,"brute custom rate");
            Reject(()=>Policy.Choose(s,"clicker",true,random,3000,100),"invalid target accepted");
            Reject(()=>Policy.Choose(s,"clicker",true,random,2000,2000),"invalid rate accepted");
            s=State();s.available=0;
            Check(Policy.Choose(s,"brute",true,random).kind=="boost","brute must accumulate early");
            s.left=5000;s.available=721;Check(Policy.Choose(s,"brute",true,random).delta==100,"brute delta");
            s=State();s.available=0;s.buttons["left"]=false;s.buttons["right"]=false;
            Check(Policy.Choose(s,"brute",true,random).kind=="boost","brute must accumulate without buttons");
            s.left=s.time;Check(Policy.Choose(s,"brute",true,random).delta==100,"due chest without button must replenish");
            s.available=379;Check(Policy.Choose(s,"brute",true,random).delta==100,"low balance after wake must replenish");
            s.available=2000;Check(Policy.Choose(s,"brute",true,random).kind=="wait","claim at cooldown boundary");
            s.time+=2999;Check(Policy.Choose(s,"brute",true,random).kind=="wait","claim before safety margin");
            s.time++;Check(Policy.Choose(s,"brute",true,random).kind=="claim","API claim must not require a button after safety margin");
            foreach(var mode in Policy.Modes) {
                var boundary=State();boundary.left=boundary.time-2999;
                Check(Policy.Choose(boundary,mode,true,random).kind=="wait",mode+" claimed inside safety margin");
                boundary.left--;
                Check(Policy.Choose(boundary,mode,true,random).side=="left",mode+" missed safety margin boundary");
                boundary.left=boundary.time;boundary.right=boundary.time-3000;
                Check(Policy.Choose(boundary,mode,true,random).side=="right",mode+" blocked eligible other slot");
            }
            s.available=0;
            Check(Policy.Choose(s,"clicker",true,random).delta==100,"clicker rate");
            Check(Policy.Choose(s,"human",true,random).delta==5,"human rate");
            Check(Policy.Choose(s,"human",true,()=>0).delayMs==2000,"human rest");
            Check(Policy.Choose(s,"clicker",false,random).kind=="wait","boost throttling");
            s.left=s.time+1000;s.available=1234;
            var fullBatch=Policy.Choose(s,"clicker",true,random,2000,1000);s.available+=fullBatch.delta;
            Check(s.available==2234&&Policy.Choose(s,"clicker",true,random,2000,1000).kind=="wait","1234 to 2234 reserve threshold");
            s=State();s.time=7000;s.available=1000;s.left=s.right=4000;Check(Policy.Choose(s,"brute",false,random).side=="left","serial slots");
            s.claimBusy=true;Check(Policy.Choose(s,"clicker",true,random).kind=="wait","animation lock");
            s=State();s.available=null;Reject(()=>Policy.Choose(s,"brute",true,random),"missing count accepted");
            s=State();s.left=null;Reject(()=>Policy.Choose(s,"brute",true,random),"missing cooldown accepted");
            s=State();s.loggedIn=false;Check(Policy.Choose(s,"brute",true,random).kind=="wait","logged out");s.loggedIn=true;s.locked=true;Check(Policy.Choose(s,"brute",true,random).kind=="wait","update lock");
            var before=State();before.time=7000;before.left=before.right=4000;var after=Copy(before);after.consumed+=1000;after.available-=1000;after.left=9000;
            var a=new ServerState {accountKey="test-key",total=4000,consumed=2000,left=4000,right=4000};var b=new ServerState {accountKey="test-key",total=4000,consumed=3000,left=9000,right=4000};
            Policy.Confirm("left",before,after,a,b);checks++;
            Check(Policy.Choose(after,"brute",true,random).side=="right","second slot after confirmation");
            foreach(var damage in new Action<Snapshot>[] {x=>x.right=9000,x=>x.consumed=4000,x=>x.claimBusy=true,x=>{x.left=4000;x.right=9000;}}) {
                var bad=Copy(after);damage(bad);Reject(()=>Policy.Confirm("left",before,bad,a,b),"invalid claim confirmed");
            }
            b.left=9500;Reject(()=>Policy.Confirm("left",before,after,a,b),"server mismatch confirmed");
            Check(Policy.ConfirmRejected(70006,before,before,a,a).Contains("冷却"),"known rejection");
            Reject(()=>Policy.ConfirmRejected(12345,before,before,a,a),"unknown rejection cleared");
            Reject(()=>Policy.ConfirmRejected(70006,before,after,a,a),"changed state rejection cleared");
        }
        static void Files() {
            Check(RuntimeStore.Alive(Process.GetCurrentProcess().Id),"current process is not alive");
            Check(!RuntimeStore.Alive(0)&&!RuntimeStore.Alive(-1),"invalid process accepted");
            var store=Store();store.Write("preferences.json",new Preference {mode="human"});Check(store.Read<Preference>("preferences.json").mode=="human","preferences roundtrip");
            File.WriteAllText(store.FilePath("pending.json"),"{\"historical\":true}");
            Reject(()=>store.Begin(new Plan {kind="claim"},State(),new ServerState()),"historical journal overwritten");
            Check(File.ReadAllText(store.FilePath("pending.json"))=="{\"historical\":true}","journal changed");
            using(var reader=new FileStream(store.FilePath("preferences.json"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                store.Write("preferences.json",new Preference {mode="brute"});
                store.Write("preferences.json",new Preference {mode="clicker"});
            }
            Check(store.Read<Preference>("preferences.json").mode=="clicker","atomic status replacement");
            Check(Platform.InstallExecutable("\"Z:\\Missing App\"")==@"Z:\Missing App\WuwaTappo.exe","quoted install path");
            Check(!Platform.ValidPath(@"Z:\Missing App\WuwaTappo.exe"),"missing app accepted");
            Check(!Platform.ValidPath("WuwaTappo.exe"),"relative app accepted");
            var missing=Json.Decode<Snapshot>("{\"loggedIn\":true}");Reject(()=>Policy.Choose(missing,"brute",true,()=>0.5),"missing JSON counts accepted");
            RenderClaimScript();
            store.Write("control.json",new Command {id="previous-stop",action="disconnect"});Panel.PrepareStart(store);
            Check(store.Read<Command>("control.json").id=="previous-stop","start preflight wrote an unbound command");
            store.Write("lock.json",new {pid=Process.GetCurrentProcess().Id});
            var command=RuntimeStore.ReadText(store.FilePath("control.json"));Reject(()=>Panel.PrepareStart(store),"duplicate worker start accepted");
            Check(RuntimeStore.ReadText(store.FilePath("control.json"))==command,"duplicate start resumed active worker");
            var pid=Process.GetCurrentProcess().Id;
            store.Write("status.json",new {pid=pid,running=true});
            WorkerOwnership.BindControl(store,store.ReadWorkerStatus("status.json"),new Command());
            store.Write("lock.json",new {pid=pid,identity=(object)null});
            store.Write("status.json",new {pid=pid,running=true,identity=(object)null});
            var incomplete=store.ReadWorkerStatus("status.json");
            Check(WorkerOwnership.CanStart(store)&&!WorkerOwnership.MayBeRunning(store,incomplete),"incomplete native record fell back to a live PID");
            using(WorkerOwnership.Acquire(store)) {
                Check(WorkerOwnership.MayBeRunning(store,incomplete),"incomplete status ignored an occupied guard");
                Reject(()=>WorkerOwnership.BindControl(store,incomplete,new Command()),"incomplete native identity authorized a command");
            }
        }
        static void RenderClaimScript() {
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var socket=new System.Net.WebSockets.ClientWebSocket();
            var constructor=typeof(AppClient).GetConstructor(flags,null,new[]{typeof(System.Net.WebSockets.ClientWebSocket)},null);
            using(var client=(AppClient)constructor.Invoke(new object[]{socket})) {
                var caps=(Capabilities)typeof(AppClient).GetField("capabilities",flags).GetValue(client);
                var profile=new AppProfile {commonPath="/assets/common.js",clickPath="/assets/clicks.js",exports=new Dictionary<string,string> {
                    {"environment","env"},{"cookie",""},{"store","state"},{"locked","locked"},{"userInfo","user"},{"decode","decodeAlias"},
                    {"openChest","openAlias"},{"saveClicks","persistAlias"},{"bagList","bagAlias"},{"refreshPanel","ready"},
                    {"chestHost","https://api-mc-wuwatappo.kurogames-global.com"},{"chestCookie","cookieAlias"},{"chestToast","toastAlias"},{"chestRenew","renewAlias"}}};
                typeof(Capabilities).GetProperty("Profile").GetSetMethod(true).Invoke(caps,new object[]{profile});
                var journal=new Journal {action=new Plan {side="left"},before=State(),server=new ServerState {observedAt=123}};
                var script=(string)typeof(AppClient).GetMethod("ClaimScript",flags).Invoke(client,new object[]{journal,false});
                var tokens=System.Text.RegularExpressions.Regex.Matches(script,"__[A-Z_]+__");
                Check(tokens.Cast<System.Text.RegularExpressions.Match>().All(t=>new[]{"__SIDE__","__RECOVER__","__TAURI_INTERNALS__","__TAPPO_HELPER_TX__","__TAPPO_HELPER_QUERY__","__TAPPO_HELPER_CLICK_REPORT__","__TAPPO_HELPER_REPORT_TIMING__","__TAPPO_HELPER_NATIVE_COOLDOWN__"}.Contains(t.Value)),"unresolved claim bridge placeholders");
                Check(script.Contains("m[\"openAlias\"]")&&script.Contains("m[\"decodeAlias\"]"),"discovered claim aliases not rendered");
                Check(script.Contains("\"observedAt\":123"),"claim query baseline not rendered");
                Check(!script.Contains("persistAlias")&&!script.Contains("/assets/clicks.js"),"claim still depends on click persistence");
                Check(!script.Contains("__CLAIM_TRANSPORT__")&&script.Contains("const requestChest="),"single claim transport was not rendered");
                var pair=(string)typeof(AppClient).GetMethod("Script",flags).Invoke(client,new object[]{"pair",null});
                Check(!pair.Contains("__CLAIM_TRANSPORT__")&&pair.Contains("const requestChest=")&&pair.Contains("m[\"cookieAlias\"]"),
                    "paired claim transport was not rendered");
                journal.action.kind="claim-pair";
                foreach(var step in new[]{"left","observe"}) {
                    bool reachedTransport=false;
                    try {client.ClaimPair(journal,step).GetAwaiter().GetResult();}
                    catch(IOException) {reachedTransport=true;}
                    Check(reachedTransport,"paired claim could not reach the disconnected test transport");
                }
                var boost=(string)typeof(AppClient).GetMethod("Script",flags).Invoke(client,new object[]{"boost",State()});
                Check(boost.Contains("clicks[\"persistAlias\"]")&&boost.Contains("import(\"/assets/clicks.js\")"),"boost click persistence not rendered");
            }
        }
        static async Task Clocks() {
            long now=1000;int reads=0;var clock=new ChestClock(()=>now);Func<Task<ServerState>> read=()=>{reads++;return Task.FromResult(new ServerState {left=90000,right=120000});};
            await clock.Poll(read);for(now=2000;now<31000;now+=1000) await clock.Poll(read);Check(reads==1,"poll rate");
            await clock.Poll(read);Check(reads==2,"poll due");now=40000;clock.Accept(new ServerState {left=180000,right=120000});now=61000;await clock.Poll(read);Check(reads==2,"transaction refresh not reused");
            now=70000;await clock.Poll(()=>{throw new IOException("timeout");});Check(clock.Value.error!=null&&clock.Value.updatedAt==40000,"query failure freshness");
            now=71000;await clock.Poll(read);Check(reads==2,"failure hot retry");now=100000;await clock.Poll(read);Check(clock.Value.error==null,"query recovery");
            clock.Reset();Check(!clock.Value.updatedAt.HasValue,"logout cache retained");Reject(()=>clock.Accept(new ServerState {right=10}),"missing server time accepted");
        }
        static async Task Workers() {
            foreach(var action in new[]{"disconnect","stop-background","stop","poll","timeout","reject","success","boost","skip","wrong-slot","unknown-rejection","unverified-reward"}) {
                var store=Store();var client=new FakeClient();long now=100000;int loops=0,restores=0;
                bool historical=action=="disconnect"||action=="stop-background"||action=="stop"||action=="poll";
                if(historical) File.WriteAllText(store.FilePath("pending.json"),"{\"historical\":true}");
                if(!historical) {client.State.left=4000;client.State.time=7000;}
                if(action=="timeout") client.ThrowClaim=true;
                if(action=="reject") client.ClaimResult=()=>{var s=Copy(client.State);s.rejected=true;s.code=70006;return s;};
                if(action=="success") client.ClaimResult=()=>{var s=Copy(client.State);s.left=90000;s.consumed+=1000;s.available-=1000;return s;};
                if(action=="unverified-reward") {client.OmitReward=true;client.ClaimResult=()=>{var s=Copy(client.State);s.outcome="confirmed";s.left=90000;s.consumed+=1000;s.available-=1000;return s;};}
                if(action=="boost") {client.State.left=900000;client.State.right=900000;client.State.available=0;store.Write("preferences.json",new Preference {mode="clicker"});}
                if(action=="skip") client.ClaimResult=()=>{var s=Copy(client.State);s.skipped=true;s.reason="条件已变化";s.left=900000;return s;};
                if(action=="wrong-slot") client.ClaimResult=()=>{var s=Copy(client.State);s.right=90000;s.consumed+=1000;s.available-=1000;return s;};
                if(action=="unknown-rejection") client.ClaimResult=()=>{var s=Copy(client.State);s.rejected=true;s.code=12345;return s;};
                Worker worker=null;
                worker=new Worker(store,()=> {
                    if(action=="disconnect"||action=="stop-background"||action=="stop")
                        store.Write("control.json",new Command {id="fixture",action=action,targetSession=worker.Status.identity.session});
                    return Task.FromResult<IAppClient>(client);
                },async c=>{restores++;await c.Quit();},ms=>{now+=30000;if(++loops==2)worker.RequestDisconnect();return Task.FromResult(0);},()=>now);
                await worker.Run();
                Check(restores==(action=="stop"?1:0),action+" restore intent");Check(client.Quits==restores,action+" quit intent");Check(client.Closed==1,action+" connection cleanup");
                Check(!worker.Status.running&&worker.Status.automationDisconnected,action+" disconnect confirmation");
                Check(worker.Status.petClosed==(action=="stop"),action+" pet close result");
                Check(new ClaimHistory(store,null).Total==worker.Status.claimsThisRun,action+" persistent claim count");
                using(new FileStream(store.FilePath("worker.guard"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) { }
                store.Write("control.json",new Command {id="old-stop",action="stop",targetSession=worker.Status.identity.session});
                var nextClient=new FakeClient();nextClient.State.loggedIn=false;Worker next=null;int restartLoops=0;
                next=new Worker(store,()=>Task.FromResult<IAppClient>(nextClient),c=>{throw new Exception("restarting closed another pet");},ms=> {
                    if(++restartLoops==1)store.Write("control.json",new Command {id="unbound-stop",action="stop"});
                    else if(restartLoops==2)store.Write("control.json",new Command {id="restart",action="disconnect",targetSession=next.Status.identity.session});
                    else next.RequestDisconnect();
                    return Task.FromResult(0);
                },()=>now);
                await next.Run();Check(nextClient.Closed==1&&!next.Status.running,action+" worker could not restart after stopping");Check(!store.Exists("lock.json"),action+" stale lock");
                Check(restartLoops==2&&next.Status.commandId=="restart",action+" worker applied an old or unbound command");
                if(historical) {Check(File.ReadAllText(store.FilePath("pending.json"))=="{\"historical\":true}","historical record changed");Check(client.Claims==0&&client.Boosts==0,"pending allowed mutations");}
                if(action=="poll") Check(client.Reads==2,"paused historical polling");
                if(action=="timeout") Check(store.Exists("pending.json")&&client.Claims==1,"uncertain claim retried");
                if(action=="reject") Check(!store.Exists("pending.json")&&worker.Status.fault==null&&worker.Status.readRetryAt.HasValue&&client.Claims==2,"known rejection must wait and retry");
                if(action=="success") Check(!store.Exists("pending.json")&&worker.Status.claimsThisRun==1,"confirmed claim not cleared");
                if(action=="unverified-reward") Check(store.Exists("pending.json")&&worker.Status.claimsThisRun==0&&client.Claims==1&&worker.Status.fault!=null,"deduction without reward was counted as success");
                if(action=="boost") Check(!store.Exists("pending.json")&&client.Boosts==2&&client.State.available==200,"native boost loop");
                if(action=="skip") Check(!store.Exists("pending.json")&&client.Claims==1,"skipped action left journal");
                if(action=="wrong-slot"||action=="unknown-rejection") Check(store.Exists("pending.json")&&client.Claims==1,"unconfirmed result retried");
            }
        }
        static async Task VersionPreflight() {
            foreach(var scenario in new[]{"upgrade","failure"}) {
                var store=Store();var client=new FakeClient();client.State.appVersion="1.0.1";client.State.left=4000;client.State.time=7000;
                client.SnapshotResult=()=>Copy(client.State);
                client.VersionResult=()=> {
                    if(scenario=="failure") throw new IOException("version unavailable");
                    client.State.appVersion="1.0.2";return client.State.appVersion;
                };
                client.ClaimResult=()=> {var after=Copy(client.State);after.left=900000;after.consumed+=1000;after.available-=1000;return after;};
                Worker worker=null;int loops=0;
                worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=> {
                    loops++;
                    if(loops==1)Check(client.Claims==0&&client.Boosts==0&&!store.Exists("pending.json"),scenario+" submitted before version validation");
                    if(scenario=="failure"||loops==2)worker.RequestDisconnect();return Task.FromResult(0);
                },()=>5000);
                await worker.Run();
                Check(client.Claims==(scenario=="upgrade"?1:0),scenario+" version preflight result");
                Check(!store.Exists("pending.json"),scenario+" version read created pending mutation");
                if(scenario=="failure")Check(worker.Status.readRetryCount==1,"version failure must use read-only retry");
            }
        }
        static async Task ServerChestConfirmation() {
            var store=Store();var client=new FakeClient();client.State.left=client.State.right=4000;client.State.time=7000;
            // The other local slot is already stale before this claim starts.
            client.State.right=3000;
            var raw=client.State;
            var server=new ServerState {accountKey="test-key",total=4000,consumed=2000,left=4000,right=4000};
            client.ServerResult=()=>new ServerState {accountKey="test-key",total=server.total,consumed=server.consumed,left=server.left,right=server.right};
            client.ClaimResult=()=> {
                server.consumed+=1000;server.left=900000;
                raw.consumed+=1000;raw.available-=1000;
                raw.buttons["left"]=false;raw.buttons["right"]=false;
                var confirmed=Copy(raw);confirmed.left=server.left;confirmed.right=server.right;
                return confirmed;
            };
            // Return the stale raw local times on subsequent reads, as the real pet does.
            client.SnapshotResult=()=>Copy(raw);
            Worker worker=null;
            worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=> {
                worker.RequestDisconnect();return Task.FromResult(0);
            },()=>5000);
            await worker.Run();
            Check(client.Claims==1&&worker.Status.claimsThisRun==1,"server confirmation with stale local other-slot time");
            Check(worker.Status.fault==null&&!store.Exists("pending.json"),"server-confirmed claim retained as uncertain");
            Check(raw.left==4000&&raw.right==3000,"helper mutated pet local times");
        }
        static async Task StartupChestClock() {
            foreach(var scenario in new[]{"normal","missing","observed","wrong-account","wrong-operation","rate-limited"}) {
                var store=Store();var client=new FakeClient();const long now=100000;
                client.State.time=now;client.State.left=client.State.right=1000;
                var server=new ServerState {accountKey=client.State.accountKey,total=4000,consumed=2000,left=900000,right=1000};
                client.ServerResult=()=>server;
                bool publishedBeforeClaim=false;
                client.VersionResult=()=>{
                    publishedBeforeClaim=store.Read<Status>("status.json").chestTimes.left==server.left;
                    return "changed";
                };
                if(scenario!="normal")store.Begin(new Plan {kind="claim",side="left"},Copy(client.State),server);
                client.RecoveryResult=journal=>{
                    var after=Copy(client.State);
                    after.operationId=scenario=="wrong-operation"?"different":journal.id;
                    after.outcome=scenario=="missing"?"unknown":"pending";
                    if(scenario=="rate-limited"){after.httpStatus=429;after.retryAfterMs=60000;after.errorType="HttpError";}
                    if(scenario=="observed"||scenario=="wrong-account"||scenario=="wrong-operation") {
                        after.serverObservedAt=now-1;
                        after.observedServer=new ServerState {accountKey=scenario=="wrong-account"?"other":server.accountKey,
                            total=server.total,consumed=server.consumed,left=800000,right=server.right};
                    }
                    return after;
                };
                Worker worker=null;
                worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=> {
                    worker.RequestDisconnect();return Task.FromResult(0);
                },()=>now);
                await worker.Run();
                var clock=worker.Status.chestTimes;
                Check(client.Claims==0&&store.Exists("pending.json")==(scenario!="normal"),scenario+" startup mutated a pending claim");
                if(scenario=="observed")Check(client.Reads==0&&clock.left==800000&&clock.updatedAt==now-1,"startup did not reuse the verified query");
                else if(scenario=="rate-limited")Check(client.Reads==0&&!clock.updatedAt.HasValue,"startup bypassed query backoff");
                else Check(client.Reads==1&&clock.left==server.left,scenario+" startup did not read the current account clock");
                if(scenario=="normal")Check(publishedBeforeClaim,"first cooldown was not published before version preflight");
            }
        }
        static async Task FixedPairWorker() {
            foreach(var scenario in new[]{"success","left-skipped","right-skipped","right-timeout","pause-after-left","old-pause-after-left","reward-stalled","receipt-clock"}) {
                var store=Store();var client=new FakeClient();var calls=new List<string>();
                const long now=100000;
                var state=client.State;state.time=now;state.left=1000;state.right=2000;
                state.cumulative=4015;state.local=15;state.serverTotal=4000;state.available=2015;
                var baseline=new ServerState {accountKey=state.accountKey,total=4000,consumed=2000,left=1000,right=2000,observedAt=now-400};
                client.ServerResult=()=>baseline;
                Worker worker=null;
                client.PairResult=(journal,command)=>{
                    calls.Add(command);
                    var disk=store.Read<Journal>("pending.json");
                    var result=new PairResult {operationId=journal.id,accountKey=state.accountKey};
                    if(command!="observe") {
                        var intent=command=="left"?disk.left:disk.right;
                        Check(intent.outcome=="armed","pair intent missing before call");
                        if(command=="right") {
                            Check(disk.left.outcome=="success"&&disk.left.receivedAt.HasValue&&disk.bagBefore!=null&&
                                disk.left.id!=disk.right.id,"right started before durable first receipt");
                            if(scenario=="receipt-clock") {
                                var times=store.Read<Status>("status.json").chestTimes;
                                Check(times.left==900000&&times.right==baseline.right,"first receipt was not published before right submission");
                            }
                            if(scenario=="right-timeout")throw new IOException("fixture lost response");
                            if(scenario=="right-skipped"){result.outcome="skipped";return result;}
                        }else if(scenario=="left-skipped") {
                            result.outcome="skipped";
                            result.server=new ServerState {accountKey=baseline.accountKey,total=4000,consumed=2000,
                                left=900000,right=901000,observedAt=now-123};
                            return result;
                        }else if(scenario=="pause-after-left"||scenario=="old-pause-after-left") {
                            var session=scenario=="pause-after-left"?worker.Status.identity.session:Guid.NewGuid().ToString("N");
                            store.Write("control.json",new Command {targetSession=session,id="pause",action="pause"});
                        }
                        result.outcome="success";
                        var sentAt=scenario=="reward-stalled"?now-61000:now;
                        result.slot=new PairSlot {id=intent.id,outcome="success",assetId=42,cost=1000,clickNum=4000,sentAt=sentAt,receivedAt=sentAt+1,applied=true};
                        if(scenario=="receipt-clock")result.slot.receiptCooldown=command=="left"?900000:901000;
                        result.bagBefore=new Dictionary<string,long>{{"42",3}};
                        return result;
                    }
                    var both=disk.right!=null&&disk.right.outcome=="success";
                    result.server=new ServerState {accountKey=baseline.accountKey,total=4000,consumed=both?4000:3000,left=900000,right=both?901000:2000};
                    result.state=Copy(state);result.state.consumed=result.server.consumed;
                    result.state.left=result.server.left;result.state.right=result.server.right;
                    result.outcome=scenario=="reward-stalled"?"pending":
                        disk.right!=null&&!both&&disk.right.outcome!="skipped"?"unknown":"confirmed";
                    if(scenario=="receipt-clock"){result.server=baseline;result.outcome="pending";}
                    return result;
                };
                var loops=0;
                Func<Worker> make=()=>new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),
                    ms=>{if(scenario!="left-skipped"||++loops>=2)worker.RequestDisconnect();return Task.FromResult(0);},()=>now);
                worker=make();await worker.Run();
                if(scenario=="left-skipped") {
                    var times=worker.Status.chestTimes;
                    Check(string.Join(",",calls)=="left"&&client.Reads==1&&!store.Exists("pending.json")&&
                        times.left==900000&&times.right==901000&&times.updatedAt==now-123,
                        "unsent left slot lost the new query or scheduled another mutation");
                }else if(scenario=="right-skipped") {
                    Check(string.Join(",",calls)=="left,right,observe"&&!store.Exists("pending.json")&&worker.Status.claimsThisRun==1,
                        "unsent right slot prevented independent verification of the left receipt");
                }else if(scenario=="right-timeout") {
                    Check(store.Read<Journal>("pending.json").right.outcome=="armed","lost response discarded second intent");
                    worker=make();await worker.Run();
                    Check(store.Exists("pending.json")&&string.Join(",",calls)=="left,right,observe","restart replayed a pair request");
                }else if(scenario=="reward-stalled") {
                    Check(store.Exists("pending.json")&&worker.Status.fault!=null&&worker.Status.claimsThisRun==0,
                        "pair verification timeout did not preserve the journal and expose the fault");
                }else if(scenario=="receipt-clock") {
                    Check(store.Exists("pending.json")&&worker.Status.claimsThisRun==0&&
                          worker.Status.chestTimes.left==900000&&worker.Status.chestTimes.right==901000,
                          "stale verification rolled back receipt cooldowns or retired the journal");
                }else if(scenario=="pause-after-left") {
                    Check(string.Join(",",calls)=="left,observe"&&!store.Exists("pending.json")&&worker.Status.claimsThisRun==1,
                        "pause did not prevent right handoff");
                }else {
                    Check(string.Join(",",calls)=="left,right,observe"&&!store.Exists("pending.json")&&worker.Status.claimsThisRun==2,
                        "pair did not send both before verifying");
                }
                Check(client.Claims==0&&client.Boosts==0,"pair fell through to another mutation");
            }
        }
        static async Task PairScheduling() {
            const long start=100000;
            foreach(var scenario in new[]{"left-near","right-near","far","slots-change"}) {
                var store=Store();var client=new FakeClient();
                var now=start;
                var first=start-Policy.ClaimSafetyMarginMs;
                var gap=scenario=="far"?60000:scenario=="right-near"?5000:1750;
                var left=scenario=="right-near"?first+gap:first;
                var right=scenario=="right-near"?first:first+gap;
                var state=client.State;
                state.time=now;state.left=left;state.right=right;state.serverTotal=4000;
                client.ServerResult=()=>new ServerState {accountKey=state.accountKey,total=4000,consumed=2000,
                    left=left,right=right,observedAt=now};
                string selected=null;
                client.PairResult=(journal,command)=>{
                    Check(command=="left"&&selected==null&&now==start+gap,scenario+" double claim started at the wrong time");
                    selected="pair";
                    return new PairResult {operationId=journal.id,accountKey=state.accountKey,outcome="skipped"};
                };
                client.ClaimResult=()=>{
                    selected=store.Read<Journal>("pending.json").action.side;
                    return new Snapshot {accountKey=state.accountKey,outcome="skipped",skipped=true};
                };
                Worker worker=null;
                worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=>{
                    if(selected!=null||now>=start+5000)worker.RequestDisconnect();
                    else {
                        Check(!store.Exists("pending.json")&&client.Boosts==0,scenario+" mutated while waiting for the other slot");
                        now+=ms;state.time=now;
                        if(scenario=="slots-change")state.right=right+1000;
                    }
                    return Task.FromResult(0);
                },()=>now);
                await worker.Run();
                var expected=scenario=="far"?0:scenario=="slots-change"?1000:gap;
                Check(selected==(scenario=="far"||scenario=="slots-change"?"left":"pair")&&now==start+expected&&
                    !store.Exists("pending.json")&&client.Boosts==0,scenario+" selected the wrong claim or exceeded the wait window");
            }
        }
        static async Task PendingChestClock() {
            var store=Store();var client=new FakeClient();long now=5000;var sides=new List<string>();
            var receivedAt=RuntimeStore.Now-1000;
            var state=client.State;state.time=now;state.left=state.right=1000;state.cumulative=5000;state.available=3000;
            var initial=new ServerState {accountKey=state.accountKey,total=5000,consumed=2000,left=1000,right=1000};
            var advanced=new ServerState {accountKey=state.accountKey,total=5000,consumed=3000,left=900000,right=1000};
            client.ServerResult=()=>initial;
            client.ClaimResult=()=>{
                sides.Add(store.Read<Journal>("pending.json").action.side);
                var after=Copy(client.State);after.outcome="pending";
                after.claimReceivedAt=sides.Count==1?receivedAt:(long?)null;
                if(sides.Count==1){after.receiptCooldown=900000;after.left=after.receiptCooldown;}
                return after;
            };
            client.RecoveryResult=journal=>{
                var after=Copy(client.State);after.operationId=journal.id;after.claimReceivedAt=receivedAt;
                after.left=advanced.left;after.consumed=advanced.consumed;after.available=2000;
                after.observedServer=advanced;after.serverObservedAt=6000;after.outcome="pending";
                if(now==8000){
                    after.outcome="confirmed";after.confirmedServer=advanced;
                    after.rewardVerified=true;after.reward=new RewardReceipt {assetId=1,quantityBefore=1,quantityAfter=2};
                    client.State=after;
                }
                return after;
            };
            Worker worker=null;
            worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=>{
                var status=store.Read<Status>("status.json");
                if(now==5000)Check(status.chestTimes.left==advanced.left&&status.chestTimes.right==initial.right&&
                    store.Read<Journal>("pending.json").receiptCooldown==advanced.left&&status.claimsThisRun==0,
                    "single receipt did not publish its cooldown while verification was pending");
                if(now==5000)Check(File.ReadLines(store.FilePath("events.jsonl")).Any(line=>line.Contains("\"event\":\"claim-received\"")),
                    "single receipt did not publish an activity before verification");
                if(now==6000||now==7000)Check(status.chestTimes.left==advanced.left&&status.chestTimes.updatedAt==6000&&
                    store.Exists("pending.json")&&status.claimsThisRun==0&&client.Claims==1,"early clock retired or replayed the pending claim");
                if(now==8000)Check(!store.Exists("pending.json")&&status.claimsThisRun==1&&client.Claims==1,"final confirmation did not retire just the first claim");
                if(now>=9000)worker.RequestDisconnect();
                now+=ms;client.State.time=now;
                return Task.FromResult(0);
            },()=>now);
            await worker.Run();
            Check(string.Join(",",sides)=="left,right"&&client.Reads==1,"second slot scheduling or query reuse changed");
            Check(File.ReadLines(store.FilePath("events.jsonl")).Count(line=>line.Contains("\"event\":\"claim-received\""))==1,
                "single receipt activity repeated during recovery");
        }
        static async Task WaitForServerClaimBalance() {
            var store=Store();var client=new FakeClient();long now=7000;int loops=0;
            client.State.time=now;client.State.left=4000;client.State.right=900000;
            client.State.cumulative=3015;client.State.available=1015;
            var server=new ServerState {accountKey="test-key",total=2990,consumed=2000,left=4000,right=900000};
            client.ServerResult=()=>new ServerState {accountKey=server.accountKey,total=server.total,consumed=server.consumed,left=server.left,right=server.right};
            client.ClaimResult=()=> {
                var after=Copy(client.State);after.available-=1000;after.consumed+=1000;after.left=900001;
                server.consumed=after.consumed;server.left=after.left;return after;
            };
            Worker worker=null;
            worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=> {
                if(++loops<=2) {
                    Check(client.Claims==0&&client.Boosts==1&&!store.Exists("pending.json"),"balance wait must allow only the scheduled boost");
                    if(loops==2)Check(worker.Status.message=="等待桌宠同步服务器余额","balance sync wait not shown between boosts");
                    if(loops==2){server.total=3000;now+=5000;client.State.time=now;}
                } else worker.RequestDisconnect();
                return Task.FromResult(0);
            },()=>now);
            await worker.Run();
            Check(client.Claims==1&&worker.Status.claimsThisRun==1&&!store.Exists("pending.json"),"server-confirmed balance did not permit a single claim");
            foreach(var scenario in new[]{"total-behind","slot-not-due","reserve-met","consumed-mismatch"}) {
                store=Store();client=new FakeClient();now=7000;
                client.State.time=11000;client.State.left=4000;client.State.right=900000;
                client.State.available=scenario=="reserve-met"?2000:1200;
                client.State.cumulative=client.State.available+2000;
                client.State.serverTotal=scenario=="total-behind"?3200:3100;
                client.ServerResult=()=>new ServerState {accountKey="test-key",total=scenario=="reserve-met"?2900:3100,
                    consumed=scenario=="consumed-mismatch"?2001:2000,
                    left=scenario=="slot-not-due"?8000:4000,right=900000};
                worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=>{
                    worker.RequestDisconnect();return Task.FromResult(0);
                },()=>now);
                await worker.Run();
                int expected=scenario=="total-behind"||scenario=="slot-not-due"?1:0;
                Check(client.Claims==0&&client.Boosts==expected&&!store.Exists("pending.json"),scenario+" blocked claim fallback");
            }
        }
        static async Task WakeReplenishment() {
            var store=Store();var client=new FakeClient();long now=5000;int loops=0;
            store.Write("preferences.json",new Preference {mode="brute",bruteRate=1621});
            client.State.available=379;client.State.buttons["left"]=client.State.buttons["right"]=false;
            client.BoostResult=(delta,before)=> {
                var after=Copy(before);after.available+=delta;after.cumulative+=delta;
                after.buttons["left"]=true;client.State=after;return after;
            };
            client.ClaimResult=()=> {
                var after=Copy(client.State);after.available-=1000;after.consumed+=1000;
                after.left=now+1800000;after.buttons["left"]=false;return after;
            };
            Worker worker=null;
            worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=> {
                // Cross both cooldowns while the worker is suspended, then resume observation.
                if(++loops==1) {now+=7200000;client.State.time=now;}
                if(client.Claims>=1||loops>=5)worker.RequestDisconnect();return Task.FromResult(0);
            },()=>now);
            await worker.Run();
            Check(client.Boosts==1&&client.Claims==1,"wake must replenish before API claim");
            Check(worker.Status.fault==null&&!store.Exists("pending.json"),"wake recovery left uncertain operation");
            Check(client.State.available==1000&&client.State.consumed==3000,"wake balance confirmation");
        }
        static async Task BoostObservations() {
            foreach(var customMode in new[]{false,true}) {
                var store=Store();var client=new FakeClient();client.State.available=4900;client.State.left=client.State.right=900000;
                store.Write("preferences.json",new Preference {mode=customMode?"brute":"clicker",reserveTarget=5000,clickerRate=500,bruteReserveTarget=5000,bruteRate=500});
                long time=100000;int loops=0;var deltas=new List<int>();
                client.BoostResult=(delta,before)=>{deltas.Add(delta);var after=Copy(before);after.available+=delta;after.cumulative+=delta;client.State=after;return after;};
                Worker worker=null;
                worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=>{
                    time+=ms;loops++;
                    if(deltas.Count==1)store.Write("preferences.json",new Preference {mode=customMode?"brute":"clicker",reserveTarget=10000,clickerRate=1000,bruteReserveTarget=10000,bruteRate=1000});
                    if(deltas.Count==2)store.Write("preferences.json",new Preference {mode=customMode?"brute":"clicker",reserveTarget=2000,clickerRate=100,bruteReserveTarget=2000,bruteRate=100});
                    if(loops>=8)worker.RequestDisconnect();
                    return Task.FromResult(0);
                },()=>time);
                await worker.Run();
                Check(deltas.SequenceEqual(new[]{500,1000}),"worker did not apply full batches, live target/rate or lower-target stop");
                Check(client.State.available==6400&&worker.Status.reserveTarget==2000&&worker.Status.clickerRate==100,"worker parameter status or balance incorrect");
                Check(!store.Exists("pending.json")&&worker.Status.fault==null,"configured boost left pending operation");
            }
            foreach(var scenario in new[]{"delayed","report-reset","timeout","consumed","slot","logout","overwritten","invalid-receipt"}) {
                var store=Store();var client=new FakeClient();client.State.available=0;client.State.left=client.State.right=900000;
                store.Write("preferences.json",new Preference {mode="clicker"});
                var before=Copy(client.State);int observations=0,waits=0;
                client.BoostResult=(delta,s)=> {
                    var after=Copy(s);
                    if(scenario=="report-reset") {after.cumulative-=50;after.available-=50;}
                    if(scenario=="consumed") after.consumed+=1000;
                    if(scenario=="slot") after.left++;
                    if(scenario=="logout") after.loggedIn=false;
                    if(scenario=="overwritten"||scenario=="invalid-receipt") {
                        after.boostOverwritten=true;after.boostAppliedTotal=s.cumulative+delta;after.boostReportTotal=s.cumulative;
                        after.serverTotal=s.cumulative;after.local=0;
                        if(scenario=="invalid-receipt")after.boostAppliedTotal=null;
                    }
                    return after;
                };
                client.SnapshotResult=()=> {
                    if(client.Boosts==0) return client.State;
                    observations++;var after=Copy(before);
                    if(scenario!="timeout"&&scenario!="invalid-receipt"&&observations>=3) {after.cumulative+=100;after.available+=100;}
                    return after;
                };
                Worker worker=null;
                worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=> {
                    if(ms==250) waits++;else worker.RequestDisconnect();return Task.FromResult(0);
                },()=>100000);
                await worker.Run();
                bool confirmed=scenario=="delayed"||scenario=="report-reset"||scenario=="overwritten";
                Check(client.Boosts==1,scenario+" resent mutation");
                Check(store.Exists("pending.json")!=confirmed,scenario+" journal handling");
                Check((worker.Status.fault==null)==confirmed,scenario+" confirmation result");
                Check(waits==(scenario=="overwritten"?0:confirmed?3:scenario=="timeout"||scenario=="invalid-receipt"?12:0),scenario+" observation bound");
                if(scenario=="overwritten")Check(RuntimeStore.ReadText(store.FilePath("events.jsonl")).Contains("boost-overwritten"),"overwritten boost misreported as success");
                if(!confirmed) {
                    Check(RuntimeStore.ReadText(store.FilePath("events.jsonl")).Contains("boost-unconfirmed"),scenario+" observation missing");
                    Check(store.Read<Journal>("pending.json").before.cumulative==before.cumulative,scenario+" original state changed");
                    // Restarting must retain the operation and describe which action is blocked.
                    client.Boosts=0;worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=>{worker.RequestDisconnect();return Task.FromResult(0);},()=>100000);
                    await worker.Run();
                    Check(client.Boosts==0&&worker.Status.fault.Contains("补充 100 次"),scenario+" restart lost pending action");
                }
            }
        }
        static async Task Resets() {
            foreach(var scenario in new[]{"success","changed","archive-failed","replayed"}) {
                var store=Store();var client=new FakeClient();client.State.left=client.State.right=900000;
                var journal="{\"action\":{\"kind\":\"boost\",\"delta\":5},\"at\":\"2026-10-27T05:22:16Z\"}";
                if(scenario!="replayed") File.WriteAllText(store.FilePath("pending.json"),journal);
                var hash=scenario=="replayed"?"old-hash":store.PendingHash();
                if(scenario=="archive-failed") File.WriteAllText(store.FilePath("history"),"blocked");
                Worker worker=null;worker=new Worker(store,()=> {
                    store.Write("control.json",new Command {id="reset-test",action="reset",pendingHash=scenario=="changed"?"wrong-hash":hash,targetSession=worker.Status.identity.session});
                    return Task.FromResult<IAppClient>(client);
                },c=>Task.FromResult(0),ms=>{worker.RequestDisconnect();return Task.FromResult(0);},()=>100000);
                await worker.Run();
                var success=scenario=="success"||scenario=="replayed";
                Check(worker.Status.resetCommandId=="reset-test"&&(worker.Status.resetError==null)==success,scenario+" reset acknowledgement");
                Check(!worker.Status.paused==success,scenario+" reset pause state");
                Check(client.Claims==0&&client.Boosts==0&&client.Quits==0,scenario+" reset mutated account");
                Check(store.Exists("pending.json")!=success,scenario+" reset pending retention");
                if(scenario=="success") {
                    var archive=Directory.GetFiles(store.FilePath("history"),"*.json").Single(p=>!p.EndsWith(".resolution.json"));
                    Check(File.ReadAllText(archive)==journal,"reset changed original bytes");
                    Check(File.ReadAllText(archive+".resolution.json").Contains(hash),"reset missing original digest");
                }
            }
        }
        static async Task QueryRetries() {
            foreach(var scenario in new[]{"recover","manual-pause","disconnect","persistent","snapshot","invalid-state"}) {
                var store=Store();var client=new FakeClient();long now=100000;int loops=0,snapshots=0;bool pauseSent=false;
                client.State.left=client.State.right=900000;
                store.Write("preferences.json",new Preference {mode="clicker"});
                if(scenario!="snapshot") client.State.available=0;
                client.ServerResult=()=> {
                    if(scenario!="snapshot"&&scenario!="invalid-state"&&(client.Reads<=2||scenario=="persistent")) throw new InvalidOperationException("query unavailable");
                    return new ServerState {accountKey="test-key",total=scenario=="invalid-state"?null:client.State.cumulative,consumed=client.State.consumed,left=client.State.left,right=client.State.right};
                };
                client.SnapshotResult=()=>{if(++snapshots==1&&scenario=="snapshot")throw new TimeoutException("read timeout");return client.State;};
                Worker worker=null;
                worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=> {
                    now+=ms;loops++;
                    if(worker.Status.readRetryCount>0&&scenario=="manual-pause"&&!pauseSent) {store.Write("control.json",new Command {targetSession=worker.Status.identity.session,id="pause-test",action="pause"});pauseSent=true;}
                    if(worker.Status.readRetryCount>0&&scenario=="disconnect") store.Write("control.json",new Command {targetSession=worker.Status.identity.session,id="disconnect-test",action="disconnect"});
                    if(client.Boosts>0||worker.Status.fault!=null||scenario=="persistent"&&worker.Status.readRetryCount==6||
                        scenario=="manual-pause"&&pauseSent&&worker.Status.readRetryCount==0||scenario=="snapshot"&&snapshots>1&&worker.Status.readRetryCount==0||loops>260) worker.RequestDisconnect();
                    return Task.FromResult(0);
                },()=>now);
                await worker.Run();
                Check(loops<=260,scenario+" retry did not terminate");
                var log=RuntimeStore.ReadText(store.FilePath("events.jsonl"));
                var retries=log.Split('\n').Where(l=>l.Contains("\"read-query-retry\"")).Select(l=>Json.Decode<Dictionary<string,object>>(l)).ToArray();
                if(scenario=="recover"||scenario=="manual-pause") {
                    Check(retries.Length==2&&Convert.ToInt32(retries[0]["backoffSeconds"])==5&&Convert.ToInt32(retries[1]["backoffSeconds"])==10,scenario+" backoff sequence");
                    Check(log.Contains("read-query-recovered")&&worker.Status.fault==null&&worker.Status.readRetryAt==null,scenario+" did not recover");
                    Check(client.Boosts==(scenario=="recover"?1:0)&&client.Claims==0,scenario+" manual pause overridden");
                    Check(worker.Status.paused==(scenario=="manual-pause"),scenario+" pause intent lost");
                } else if(scenario=="persistent") {
                    Check(string.Join(",",retries.Select(r=>r["backoffSeconds"].ToString()))=="5,10,20,40,60,60","retry delay not capped");
                    Check(client.Boosts==0&&client.Claims==0&&worker.Status.fault==null,"read failure allowed mutation or permanent fault");
                } else if(scenario=="invalid-state") {
                    Check(retries.Length==0&&worker.Status.fault!=null&&client.Boosts==0,"malformed state retried");
                } else if(scenario=="snapshot") {
                    Check(retries.Length==1&&log.Contains("read-query-recovered")&&client.Boosts==0,"snapshot retry did not recover");
                } else Check(retries.Length==1&&client.Reads==1&&client.Boosts==0,"disconnect waited for retry");
            }
        }
        static async Task Connections() {
            long now=0;int reads=0;
            var ready=new object();
            var result=await AppClient.WaitForMain(()=>Task.FromResult(++reads<4?null:ready),ms=>{now+=ms;return Task.FromResult(0);},()=>now,2000);
            Check(result==ready&&reads==4&&now==1200,"lazy main window not awaited");
            now=0;bool expired=false;
            try {await AppClient.WaitForMain(()=>Task.FromResult<object>(null),ms=>{now+=ms;return Task.FromResult(0);},()=>now,1000);}catch(TimeoutException){expired=true;}
            Check(expired&&now==1200,"main window deadline ignored");
            reads=0;bool rejected=false;
            try {await AppClient.WaitForMain<object>(()=>{reads++;throw new InvalidOperationException("主窗口不唯一");},ms=>Task.FromResult(0),()=>0,1000);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&reads==1,"ambiguous main window retried");
            foreach(var scenario in new[]{"reconnect","unavailable","http","pending","claim-disconnect"}) {
                var store=Store();var old=new FakeClient();var fresh=new FakeClient();int initial=0,reconnections=0,loops=0;now=100000;
                old.State.left=old.State.right=fresh.State.left=fresh.State.right=900000;
                if(scenario=="pending") File.WriteAllText(store.FilePath("pending.json"),"{\"historical\":true}");
                old.SnapshotResult=()=>{if(scenario!="http")old.Connected=false;if(scenario!="claim-disconnect")throw new IOException("connection lost");return old.State;};
                if(scenario=="claim-disconnect") {old.State.left=4000;old.State.time=7000;old.ClaimResult=()=>{old.Connected=false;throw new IOException("claim connection lost");};}
                Worker worker=null;
                worker=new Worker(store,()=>{initial++;return Task.FromResult<IAppClient>(old);},c=>{throw new Exception("must not restart pet");},ms=> {
                    now+=ms;loops++;
                    if(fresh.Reads>0||fresh.Recoveries>0||scenario=="http"&&worker.Status.readRetryCount==2||loops>50)worker.RequestDisconnect();
                    return Task.FromResult(0);
                },()=>now,()=>{reconnections++;if(scenario=="unavailable"&&reconnections<3)throw new IOException("port not ready");return Task.FromResult<IAppClient>(fresh);});
                await worker.Run();
                Check(initial==1&&loops<=50,scenario+" restarted pet or stalled");
                if(scenario=="reconnect"||scenario=="unavailable") {
                    Check(reconnections==(scenario=="unavailable"?3:1)&&fresh.Reads>0&&worker.Status.fault==null,scenario+" did not restore connection");
                    Check(old.Closed==1&&fresh.Closed==1,scenario+" connection not disposed");
                } else if(scenario=="http")Check(reconnections==0,"HTTP failure restarted a live transport");
                else Check(reconnections==1,scenario+" pending outcome blocked reconnection");
                if(scenario=="claim-disconnect")Check(old.Claims==1&&store.Exists("pending.json"),"uncertain claim journal cleared");
                Check(old.Boosts==0&&fresh.Claims==0&&fresh.Boosts==0,scenario+" reconnect resent mutations");
            }
        }
        static void ResetPanels() {
            var store=Store();File.WriteAllText(store.FilePath("pending.json"),"{\"historical\":true}");
            using(var panel=new Panel(store)) {
                panel.ReadStatus=()=>new Status();panel.ConfirmReset=()=>false;
                panel.ResetPending().GetAwaiter().GetResult();Check(store.Exists("pending.json"),"cancelled reset removed journal");
                panel.ConfirmReset=()=>true;
                using(var guard=new FileStream(store.FilePath("worker.guard"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                    panel.ResetPending().GetAwaiter().GetResult();Check(store.Exists("pending.json"),"reset ignored worker guard");
                }
                panel.ResetPending().GetAwaiter().GetResult();Check(!store.Exists("pending.json"),"offline reset did not archive");
                Check(panel.E<Button>("ResetPendingButton").Visibility==Visibility.Collapsed,"resolved reset button visible");
            }
            store=Store();File.WriteAllText(store.FilePath("pending.json"),"{}");
            using(var panel=new Panel(store)) {
                var identity=RuntimeIdentity.Create(store,"worker");
                panel.ReadStatus=()=>new Status {running=true,pid=Process.GetCurrentProcess().Id,at=DateTimeOffset.UtcNow.ToString("o")};
                panel.Refresh();Check(panel.E<TextBlock>("StatusLabel").Text=="状态未知","legacy PID-only status presented as current");
                using(WorkerOwnership.Acquire(store)) {
                    panel.ReadStatus=()=>new Status {identity=identity,running=true,pid=Process.GetCurrentProcess().Id,at=DateTimeOffset.UtcNow.ToString("o")};
                    panel.Refresh();Check(!panel.E<Button>("ResetPendingButton").IsEnabled,"in-flight reset enabled");
                    bool asked=false;panel.ConfirmReset=()=>{asked=true;return true;};panel.ResetPending().GetAwaiter().GetResult();
                    Check(!asked&&store.Exists("pending.json"),"in-flight reset accepted");
                    panel.ReadStatus=()=>new Status {identity=identity,running=true,paused=true,fault="unknown",pid=Process.GetCurrentProcess().Id,at=DateTimeOffset.UtcNow.ToString("o")};
                    panel.Refresh();Check(panel.E<Button>("ResetPendingButton").IsEnabled,"blocked reset disabled");
                    string dispatched=null;panel.DispatchOverride=action=>dispatched=action;panel.ResetPending().GetAwaiter().GetResult();Check(dispatched=="reset","reset button did not dispatch");
                }
            }
        }
        static void Layouts() {
            var store=Store();
            using(var guard=WorkerOwnership.Acquire(store))
            using(var panel=new Panel(store)) {
                panel.InitializeTray();
                var state=State();state.left=RuntimeStore.Now+650000;state.right=RuntimeStore.Now-1000;
                var status=new Status {identity=RuntimeIdentity.Create(store,"worker"),at=DateTimeOffset.UtcNow.ToString("o"),pid=Process.GetCurrentProcess().Id,running=true,mode="brute",state=state,
                    chestTimes=new ChestTimes {updatedAt=RuntimeStore.Now,left=state.left,right=state.right}};
                panel.ReadStatus=()=>status;
                var images=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"previews");Directory.CreateDirectory(images);
                panel.Render(Path.Combine(images,"running.png"));
                Check(panel.Window.FindName("MainScroll")==null,"main panel still scrolls");Check(panel.E<Border>("ChestArea").ActualHeight<100,"chests too tall");
                Check(panel.E<TextBlock>("LeftTime").Text.Contains(":"),"server countdown missing");
                Check(panel.E<TextBlock>("RightTime").Text=="已就绪","ready slot missing");
                Check(panel.E<Button>("StartButton").IsEnabled&&(string)panel.E<Button>("StartButton").Content=="停止运行","running controls");
                status.readRetryAt=RuntimeStore.Now+10000;status.readRetryError="读取服务器状态失败";panel.Refresh();
                Check(panel.E<TextBlock>("StatusLabel").Text=="等待重试"&&panel.E<TextBlock>("DetailMessage").Text.Contains("秒后重试"),"retry status missing");
                Check(panel.Window.FindName("RetryButton")==null,"removed retry entry is still present");
                string retryAction=null;panel.DispatchOverride=action=>retryAction=action;
                panel.E<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(retryAction=="pause","retrying run cannot be stopped");
                status.paused=true;status.manuallyPaused=true;panel.Refresh();retryAction=null;
                panel.E<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(retryAction=="resume","restart did not dispatch the retry-capable resume action");panel.DispatchOverride=null;
                status.paused=false;status.manuallyPaused=false;
                status.paused=true;panel.Refresh();Check(panel.E<TextBlock>("StatusLabel").Text=="已暂停","retry hid manual pause");
                status.paused=false;status.readRetryAt=null;status.readRetryError=null;panel.Refresh();
                Check(panel.Window.FindName("ExitButton")==null,"unexpected title exit button");
                Check(panel.Window.FindName("ExitMenuItem")==null&&panel.E<Border>("RootSurface").ContextMenu==null&&panel.TrayExitEnabled,"exit entry unavailable");
                panel.ReadStatus=()=>{throw new UnauthorizedAccessException("模拟读取失败");};panel.Refresh();
                Check(panel.TrayExitEnabled&&panel.E<Border>("RootSurface").ContextMenu==null,"status error disabled exit");panel.ReadStatus=()=>status;panel.Refresh();
                Check(panel.E<TextBlock>("ActivityText").Visibility==Visibility.Visible&&panel.Window.FindName("ActivityScroll")==null&&panel.Window.FindName("ViewLogsButton")==null,"activity still exposes scrolling or a log window entry");
                status.paused=true;panel.Refresh();Check((string)panel.E<Button>("StartButton").Content=="启动后台","resume label");
                File.WriteAllText(store.FilePath("pending.json"),"{\"historical\":true}");status.manuallyPaused=true;panel.Refresh();Check(!panel.E<Button>("StartButton").IsEnabled,"pending controls");
                status.recovering=true;status.manuallyPaused=false;panel.Refresh();
                var trayRun=(System.Windows.Forms.ToolStripMenuItem)typeof(Panel).GetField("trayRun",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(panel);
                Check(panel.E<Button>("StartButton").IsEnabled&&(string)panel.E<Button>("StartButton").Content=="停止运行"&&trayRun.Enabled&&trayRun.Text=="停止运行","automatic recovery disabled stop");
                string pauseCommand=null;panel.DispatchOverride=action=>pauseCommand=action;
                panel.E<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(pauseCommand=="pause","pending panel requested resume instead of manual pause");
                pauseCommand=null;trayRun.PerformClick();Check(pauseCommand=="pause","pending tray requested resume instead of manual pause");panel.DispatchOverride=null;
                status.manuallyPaused=true;panel.Refresh();Check(!panel.E<Button>("StartButton").IsEnabled&&!trayRun.Enabled&&panel.E<TextBlock>("DetailMessage").Text.Contains("检查后也不会自动继续"),"pending manual pause allowed resume or hid intent");
                status.recovering=false;
                store.Log("started");store.Log("boost-confirmed",new {delta=100});store.Log("claim-confirmed",new {side="left"});panel.Render(Path.Combine(images,"paused.png"));
                Check(panel.E<TextBlock>("ActivityText").Text.Split('\n').Length==3,"activity history");
                foreach(var overwritten in new[]{false,true}) {
                    store.Log("boost-recovered",new {delta=100,after=new Snapshot {boostOverwritten=overwritten}});panel.Refresh();
                    var line=panel.E<TextBlock>("ActivityText").Text.Split('\n').First();
                    Check(line.Contains(overwritten?"本次补数被旧上报覆盖":"已补充 100 次"),"legacy boost activity hidden or misclassified");
                }
                status.chestTimes.error="timeout";panel.Refresh();Check(panel.E<TextBlock>("LeftTime").Text=="等待同步"&&panel.E<Border>("LeftFill").Width==0,"failed API displayed live time");
                status.chestTimes.error=null;status.chestTimes.updatedAt=RuntimeStore.Now-100000;panel.Refresh();Check(panel.E<TextBlock>("LeftTime").Text=="等待同步","expired time");
                status.running=false;guard.Dispose();panel.Render(Path.Combine(images,"disconnected.png"));Check(panel.E<TextBlock>("LeftTime").Text=="未连接","disconnected view");
                status.fault="桌宠主窗口尚未就绪";panel.Refresh();Check(panel.E<TextBlock>("DetailMessage").Text==status.fault,"stopped failure hidden behind restart hint");status.fault=null;
                panel.SetPath(@"C:\Long installation path\Long installation path\Long installation path\WuwaTappo.exe");panel.Render(Path.Combine(images,"minimum.png"),820,660);
                var surface=panel.E<Border>("RootSurface");var start=panel.E<Button>("StartButton").TranslatePoint(new Point(),surface);
                Check(start.X+panel.E<Button>("StartButton").ActualWidth<796&&panel.Window.FindName("PauseButton")==null&&panel.Window.FindName("StopButton")==null,"single run control missing or overflowing");
                var modes=panel.E<ListBox>("ModeSelector");modes.SelectedIndex=0;
                Check(panel.E<StackPanel>("ReserveOptions").Visibility==Visibility.Visible&&panel.E<ComboBox>("ReserveTarget").Visibility==Visibility.Collapsed&&panel.E<Border>("BruteTargetBox").Visibility==Visibility.Visible&&panel.E<Border>("BruteRateBox").Visibility==Visibility.Visible,"brute custom parameters missing");
                var customTarget=panel.E<TextBox>("BruteTarget");var customRate=panel.E<TextBox>("BruteRate");
                customTarget.Text="12345";customRate.Text="1234";
                Check(store.Read<Preference>("preferences.json").bruteReserveTarget==12345&&store.Read<Preference>("preferences.json").bruteRate==1234,"custom values not saved immediately");
                foreach(var invalid in new[]{"","0","-1","1.5","abc","2147483648","100000000"," 2000","2,000"}) {
                    customTarget.Text=invalid;
                    Check(store.Read<Preference>("preferences.json").bruteReserveTarget==12345&&panel.E<TextBlock>("BruteError").Text.Length>0,"invalid target changed saved value");
                }
                customTarget.Text="12345";
                foreach(var invalid in new[]{"","0","-1","1.5","abc","2147483648","2001"}) {
                    customRate.Text=invalid;
                    Check(store.Read<Preference>("preferences.json").bruteRate==1234,"invalid rate changed saved value");
                }
                customRate.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,0,customRate,customTarget) {RoutedEvent=Keyboard.LostKeyboardFocusEvent});
                Check(customRate.Text=="1234"&&customTarget.Text=="12345"&&panel.E<TextBlock>("BruteError").Text=="","invalid input not restored on blur");
                customRate.Text="1234";panel.SetAutoRun(true);
                Check(store.Read<Preference>("preferences.json").bruteRate==1234,"auto run lost custom values");
                using(var customReopened=new Panel(store,true))Check(customReopened.E<TextBox>("BruteTarget").Text=="12345"&&customReopened.E<TextBox>("BruteRate").Text=="1234","reopen lost custom settings");
                panel.Render(Path.Combine(images,"brute-custom.png"),820,660);
                File.Delete(store.FilePath("preferences.json"));Directory.CreateDirectory(store.FilePath("preferences.json"));
                customRate.Text="999";
                Check(customRate.Text=="1234"&&customTarget.Text=="12345","failed custom save did not restore input");
                Directory.Delete(store.FilePath("preferences.json"));customRate.Text="1235";customRate.Text="1234";
                modes.SelectedIndex=1;
                Check(panel.E<StackPanel>("ReserveOptions").Visibility==Visibility.Visible&&panel.E<StackPanel>("SpeedOptions").Visibility==Visibility.Visible,"clicker missing parameters");
                var target=panel.E<ComboBox>("ReserveTarget");var rate=panel.E<ComboBox>("ClickerRate");
                Check(!target.IsEditable&&!rate.IsEditable,"parameter entry editable");
                target.SelectedIndex=2;rate.SelectedIndex=2;
                var preference=store.Read<Preference>("preferences.json");
                Check(preference.reserveTarget==10000&&preference.clickerRate==1000,"parameters not persisted");
                target.SelectedIndex=3;Check(store.Read<Preference>("preferences.json").reserveTarget==20000,"20000 target not persisted");
                target.SelectedIndex=4;Check(store.Read<Preference>("preferences.json").reserveTarget==50000,"50000 target not persisted");
                Check(((ComboBoxItem)rate.SelectedItem).Content.ToString()=="1,000"&&(string)panel.E<Label>("SpeedLabel").Content=="补充速度（次/秒）",
                    "speed units are missing from the label or repeated in the selector");
                panel.Render(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"previews","clicker-options.png"),820,660);
                var speedPosition=rate.TranslatePoint(new Point(),panel.E<Border>("RootSurface"));
                Check(speedPosition.X>=0&&speedPosition.X+rate.ActualWidth<796&&rate.ActualHeight>=32,"parameter controls overflow");
                panel.Window.Show();panel.Window.UpdateLayout();
                rate.IsDropDownOpen=true;
                panel.Window.Dispatcher.Invoke(new Action(()=>{}),DispatcherPriority.ApplicationIdle);
                var popup=(System.Windows.Controls.Primitives.Popup)rate.Template.FindName("PART_Popup",rate);
                Check(popup.IsOpen&&popup.Child.RenderSize.Width>=rate.ActualWidth,"parameter popup missing or too narrow");
                var popupImage=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(popup.Child.RenderSize.Width),(int)Math.Ceiling(popup.Child.RenderSize.Height),96,96,System.Windows.Media.PixelFormats.Pbgra32);
                popupImage.Render(popup.Child);var popupEncoder=new System.Windows.Media.Imaging.PngBitmapEncoder();popupEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(popupImage));
                using(var output=File.Create(Path.Combine(images,"clicker-options-expanded.png")))popupEncoder.Save(output);
                rate.IsDropDownOpen=false;
                panel.Window.Hide();
                modes.SelectedIndex=2;Check(store.Read<Preference>("preferences.json").mode=="human","mode not persisted");
                Check(panel.E<StackPanel>("ReserveOptions").Visibility==Visibility.Visible&&panel.E<ComboBox>("ClickerRate").Visibility==Visibility.Collapsed&&panel.E<TextBlock>("FixedSpeed").Text=="波动慢速","human parameter visibility");
                Check((int)((ComboBoxItem)target.SelectedItem).Tag==50000,"switch lost target");
                using(var reopened=new Panel(store,true)) {
                    Check((int)((ComboBoxItem)reopened.E<ComboBox>("ReserveTarget").SelectedItem).Tag==50000&&(int)((ComboBoxItem)reopened.E<ComboBox>("ClickerRate").SelectedItem).Tag==1000,"reopen lost parameters");
                }
                File.Delete(store.FilePath("preferences.json"));Directory.CreateDirectory(store.FilePath("preferences.json"));
                target.SelectedIndex=0;Check(target.SelectedIndex==4,"failed save changed target");
                rate.SelectedIndex=0;Check(rate.SelectedIndex==2,"failed save changed speed");
                modes.SelectedIndex=1;Check((string)((ListBoxItem)modes.SelectedItem).Tag=="human","failed save changed selection");Directory.Delete(store.FilePath("preferences.json"));
            }
            using(var panel=new Panel(Store())) {
                panel.InitializeTray();panel.ReadStatus=()=>new Status {running=true,pid=Process.GetCurrentProcess().Id};string dispatched=null;panel.DispatchOverride=action=>dispatched=action;
                ((System.Windows.Forms.ToolStripMenuItem)typeof(Panel).GetField("trayExit",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(panel)).PerformClick();
                Check(dispatched=="disconnect","tray exit requested pet closure");
            }
        }
        static void UpdatePanels() {
            var store=Store();using(var panel=new Panel(store,preview:true)) {
                var now=DateTimeOffset.UtcNow;var state=State();state.time=now.ToUnixTimeMilliseconds();state.available=1800;state.cumulative=12800;state.consumed=11000;
                state.left=state.time+240000;state.right=state.time+754000;
                panel.ReadClock=()=>now.ToUnixTimeMilliseconds();
                panel.ReadStatus=()=>new Status {running=true,pid=Process.GetCurrentProcess().Id,at=now.ToString("o"),mode="brute",state=state,claimsThisRun=2,
                    chestTimes=new ChestTimes {updatedAt=state.time,left=state.left,right=state.right}};
                panel.SetPath(@"C:\WuwaTappo\WuwaTappo.exe");
                store.Log("started");store.Log("claim-confirmed",new {side="right"});store.Log("boost-confirmed",new {delta=100});
                var previewActivities=File.ReadAllText(store.FilePath("events.jsonl"));
                var versionLabel="v"+Updates.DisplayVersion+" · ";
                Check(panel.Window.FindName("VersionLabel")==null&&panel.E<Button>("UpdateButton").Content.ToString()==versionLabel+"检查更新","version and update action are not in one button");
                panel.UpdateCheck=token=>Task.FromResult(new UpdateInfo {version="0.1.1",available=false});
                panel.CheckForUpdates().GetAwaiter().GetResult();Check(panel.E<Button>("UpdateButton").Content.ToString().Contains("已是最新"),"up-to-date UI");
                Check(panel.E<Button>("UpdateButton").Content.ToString().StartsWith(versionLabel),"up-to-date UI missing local version");
                Check(panel.E<Button>("UpdateButton").ToolTip==null,"normal update retains a tooltip");
                panel.UpdateCheck=token=>{throw new IOException("offline");};
                panel.CheckForUpdates().GetAwaiter().GetResult();
                Check(panel.E<Button>("UpdateButton").Content.ToString()==versionLabel+"已是最新","failed refresh lost the last confirmed version status");
                panel.UpdateCheck=token=>Task.FromResult(new UpdateInfo {version="0.1.17",available=true,url="https://github.com/kano0222/WuwaTappo-helper/releases/tag/v0.1.17"});
                panel.CheckForUpdates().GetAwaiter().GetResult();string opened=null;panel.OpenUpdatePage=url=>opened=url;
                panel.Render(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"previews","update-available.png"),820,660);
                RenderUpdateState(panel,"update-available");
                var updateButton=panel.E<Button>("UpdateButton");var position=updateButton.TranslatePoint(new Point(),panel.E<Border>("RootSurface"));
                Check(updateButton.Content.ToString()==versionLabel+"下载新版"&&updateButton.ToolTip==null,"new release hides installed version or retains a tooltip");
                Check((string)updateButton.Tag=="update"&&((System.Windows.Media.SolidColorBrush)updateButton.Background).Color==System.Windows.Media.Color.FromRgb(246,232,210),"new update not highlighted");
                Check(position.X>=0&&position.X+updateButton.ActualWidth<=820&&position.Y+updateButton.ActualHeight<=660,"update button overflow");
                int downloads=0;panel.UpdateDownload=(info,progress,token)=>{downloads++;throw new IOException("offline");};
                panel.UpdateAction().GetAwaiter().GetResult();Check(downloads==1&&opened==null&&updateButton.Content.ToString()==versionLabel+"重试下载","download failure or browser fallback");
                RenderUpdateState(panel,"update-download-failed");
                panel.UpdateAction().GetAwaiter().GetResult();Check(downloads==2&&updateButton.IsEnabled,"download retry unavailable");
                var feedback=panel.E<Button>("FeedbackButton");
                feedback.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(opened==Updates.IssuesUrl,"feedback page not opened");
                var feedbackPosition=feedback.TranslatePoint(new Point(),panel.E<Border>("RootSurface"));
                Check(feedbackPosition.X>=0&&feedbackPosition.X+feedback.ActualWidth<=position.X,"feedback not left of updates");
                panel.OpenUpdatePage=url=>{throw new InvalidOperationException("browser failed");};
                feedback.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(feedback.ToolTip==null&&panel.E<TextBlock>("DetailMessage").Text.Contains(Updates.IssuesUrl),"feedback browser failure not visible or retains a tooltip");
                foreach(var kind in new[]{"main-not-ready","incompatible","debug-unavailable"}) {
                    store.Log("read-query-retry",new {stage="query",connectionFailure=kind});panel.Refresh();
                    Check(panel.E<TextBlock>("ActivityText").Text.Split('\n')[0].Contains(Connection.RetryMessage("query",kind)),"connection activity misclassified");
                }
                panel.UpdateCheck=token=>{throw new InvalidOperationException("GitHub 暂时限制查询");};panel.CheckForUpdates().GetAwaiter().GetResult();
                Check(panel.E<Button>("UpdateButton").IsEnabled&&panel.E<Button>("UpdateButton").Content.ToString()==versionLabel+"检查更新","update failure did not restore the check action");
                Check(updateButton.Content.ToString().StartsWith(versionLabel)&&updateButton.ToolTip==null,"failed check hid version or retains a tooltip");
                panel.UpdateCheck=token=>{throw new System.Net.Http.HttpRequestException("offline");};panel.CheckForUpdates().GetAwaiter().GetResult();
                Check(updateButton.Content.ToString().StartsWith(versionLabel)&&updateButton.ToolTip==null,"offline check hid local version or retains a tooltip");
                panel.Render(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"previews","update-failed.png"),820,660);
                int retries=0;panel.UpdateCheck=token=>{retries++;return Task.FromResult(new UpdateInfo {version="0.1.1"});};
                panel.UpdateAction().GetAwaiter().GetResult();Check(retries==1,"retry opened stale update page");
                Check(!store.Exists("control.json")&&panel.ReadStatus().running,"update check changed automation");
                var waiting=new TaskCompletionSource<UpdateInfo>();int calls=0;
                panel.UpdateCheck=token=>{calls++;return waiting.Task;};var first=panel.CheckForUpdates();
                Check(updateButton.Content.ToString()==versionLabel+"检查中…","pending check hid local version");
                Check(updateButton.ToolTip==null,"checking update retains a tooltip");
                panel.CheckForUpdates().GetAwaiter().GetResult();Check(calls==1&&!panel.E<Button>("UpdateButton").IsEnabled,"overlapping update requests");
                waiting.SetResult(new UpdateInfo {version="0.1.1"});panel.Window.Dispatcher.Invoke(new Action(()=>{}),DispatcherPriority.ApplicationIdle);first.GetAwaiter().GetResult();
                Check(panel.E<Button>("UpdateButton").IsEnabled,"update button not restored");
                File.WriteAllText(store.FilePath("events.jsonl"),previewActivities);
                panel.UpdateCheck=token=>Task.FromResult(new UpdateInfo {version="0.1.17",available=true});panel.CheckForUpdates().GetAwaiter().GetResult();
                var download=new TaskCompletionSource<string>();downloads=0;
                panel.UpdateDownload=(info,progress,token)=>{downloads++;progress(50);return download.Task;};
                var downloading=panel.UpdateAction();panel.UpdateAction().GetAwaiter().GetResult();panel.CheckForUpdates().GetAwaiter().GetResult();
                Check(downloads==1&&!updateButton.IsEnabled,"overlapping download or check");
                panel.Window.Dispatcher.Invoke(new Action(()=>{}),DispatcherPriority.ApplicationIdle);
                RenderUpdateState(panel,"update-downloading");
                download.SetResult("isolated-stage");panel.Window.Dispatcher.Invoke(new Action(()=>{}),DispatcherPriority.ApplicationIdle);downloading.GetAwaiter().GetResult();
                Check(updateButton.Content.ToString()==versionLabel+"重启更新"&&updateButton.IsEnabled,"download did not enable restart");
                Check(updateButton.ToolTip.ToString().Contains("多开窗口")&&updateButton.ToolTip.ToString().Contains("自动运行"),"ready update omits its impact on instances or the background");
                RenderUpdateState(panel,"update-ready");
                panel.CheckForUpdates().GetAwaiter().GetResult();Check(updateButton.Content.ToString()==versionLabel+"重启更新","automatic check discarded staged update");
                int installs=0;panel.InstallUpdate=stage=>{Check(stage=="isolated-stage","wrong update stage");installs++;throw new IOException("launch failed");};
                panel.UpdateAction().GetAwaiter().GetResult();Check(installs==1&&updateButton.IsEnabled&&panel.E<TextBlock>("DetailMessage").Text.Contains("启动失败")&&updateButton.ToolTip.ToString().Contains("自动运行"),"installer launch failure lost retry or update impact");
                panel.InstallUpdate=stage=>installs++;panel.UpdateAction().GetAwaiter().GetResult();Check(installs==2&&downloads==1,"restart downloaded again");
                panel.InstallUpdate=UpdateInstaller.Launch;
                panel.UpdateAction().GetAwaiter().GetResult();
                Check(updateButton.IsEnabled&&updateButton.Content.ToString()==versionLabel+"重新下载"&&updateButton.ToolTip==null,"missing update did not restore download or clear impact hint");
                Check(!panel.AllowExit&&!store.Exists("control.json")&&panel.ReadStatus().running,"invalid update stopped automation or closed panel");
                RenderUpdateState(panel,"update-invalid");
                panel.UpdateDownload=(info,progress,token)=>{downloads++;return Task.FromResult("replacement-stage");};
                panel.UpdateAction().GetAwaiter().GetResult();
                Check(downloads==2&&updateButton.Content.ToString()==versionLabel+"重启更新","invalid update could not be downloaded again");
                panel.InstallUpdate=stage=>{Check(stage=="replacement-stage","invalid stage reused after download");installs++;};
                panel.UpdateAction().GetAwaiter().GetResult();Check(installs==3,"replacement update did not reach installer");
                Check(!store.Exists("control.json"),"download or launch directly mutated automation");
            }
        }
        static void RenderUpdateState(Panel panel,string name) {
            foreach(var size in new[]{new Size(960,900),new Size(820,660)}) {
                var suffix=size.Width==960?"-full":"-minimum";
                panel.Render(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"previews",name+suffix+".png"),(int)size.Width,(int)size.Height);
                if(size.Width==960) {
                    var footer=panel.E<Border>("FooterHint");var visual=new System.Windows.Media.DrawingVisual();
                    using(var drawing=visual.RenderOpen())drawing.DrawRectangle(new System.Windows.Media.VisualBrush(footer),null,new Rect(0,0,footer.ActualWidth,footer.ActualHeight));
                    var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(footer.ActualWidth),(int)Math.Ceiling(footer.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(visual);
                    var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using(var output=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"previews",name+"-footer.png")))encoder.Save(output);
                }
                var button=panel.E<Button>("UpdateButton");var feedback=panel.E<Button>("FeedbackButton");var surface=panel.E<Border>("RootSurface");
                var position=button.TranslatePoint(new Point(),surface);var other=feedback.TranslatePoint(new Point(),surface);
                Check(button.ActualWidth==180&&button.ActualHeight==feedback.ActualHeight&&Math.Abs(position.Y-other.Y)<1,"update footer controls changed size or alignment: "+name);
                Check(button.Content.ToString().StartsWith("v"+Updates.DisplayVersion+" · ")&&(button.Content.ToString().EndsWith("重启更新")?button.ToolTip!=null:button.ToolTip==null),"update state hides version or retains an unrelated tooltip");
                Check(other.X>=0&&other.X+feedback.ActualWidth+5<=position.X&&position.X+button.ActualWidth<=size.Width-20&&position.Y+button.ActualHeight<=size.Height,"update footer overflow: "+name);
                var text=new TextBlock {Text=button.Content.ToString(),FontFamily=button.FontFamily,FontSize=button.FontSize,FontWeight=button.FontWeight};text.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
                Check(text.DesiredSize.Width<=button.ActualWidth-button.Padding.Left-button.Padding.Right-button.BorderThickness.Left-button.BorderThickness.Right+0.01,"update label clipped: "+name);
                Check(button.Opacity==1,"update progress text faded: "+name);
            }
        }
        static void CloseChoices() {
            foreach(var mini in new[]{false,true}) {
                var store=Store();
                if(mini)store.Write("panel-mini.json",true);
                using(var panel=new Panel(store,preview:true)) {
                    panel.InitializeTray();panel.Window.Show();
                    int prompts=0,commands=0;
                    panel.ClosePrompt=()=>{prompts++;return CloseChoice.Cancel;};
                    panel.DispatchOverride=action=>commands++;
                    var button=panel.E<Button>(mini?"MiniTrayButton":"TrayButton");
                    Check(button.IsVisible,"tray shortcut unavailable in current view");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!panel.Window.IsVisible&&!panel.Window.ShowInTaskbar&&!panel.AllowExit&&prompts==0&&commands==0,
                        "tray shortcut prompted, dispatched a command, exited or failed to hide");
                    panel.Show();
                    Check(panel.Window.IsVisible&&panel.Window.ShowInTaskbar&&button.IsVisible,"tray shortcut lost the window or view on restore");
                }
            }
            foreach(var choice in new[]{CloseChoice.Cancel,CloseChoice.Tray,CloseChoice.Exit}) using(var panel=new Panel(Store())) {
                panel.InitializeTray();panel.ReadStatus=()=>new Status {running=true,pid=Process.GetCurrentProcess().Id};panel.Window.Show();
                string dispatched=null;panel.DispatchOverride=action=>dispatched=action;panel.ClosePrompt=()=>choice;
                panel.E<Button>("CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(panel.Window.IsVisible==(choice==CloseChoice.Cancel),choice+" close visibility");
                Check(dispatched==(choice==CloseChoice.Exit?"disconnect":null),choice+" close automation action");
                Check(panel.AllowExit==(choice==CloseChoice.Exit),choice+" close exit permission");
                if(choice!=CloseChoice.Exit) {panel.AllowExit=true;panel.Window.Close();}
            }
            foreach(var action in new[]{"exit","tray","cancel","close-button"}) {
                var dialog=new CloseDialog(null);
                dialog.Window.ContentRendered+=(s,e)=>dialog.Window.Dispatcher.BeginInvoke(new Action(()=> {
                    if(action=="cancel") {
                        var content=(FrameworkElement)dialog.Window.Content;content.UpdateLayout();
                        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(content);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using(var output=File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"previews","close-dialog.png")))encoder.Save(output);
                    }
                    if(action=="exit")dialog.ExitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if(action=="tray")dialog.TrayButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if(action=="close-button")((Button)dialog.Window.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Window.Close();
                }));
                var result=dialog.Show();
                Check(result==(action=="exit"?CloseChoice.Exit:action=="tray"?CloseChoice.Tray:CloseChoice.Cancel),action+" dialog result");
            }
            using(var panel=new Panel(Store())) {
                panel.Window.Show();int prompts=0;panel.ClosePrompt=()=>{prompts++;return CloseChoice.Cancel;};
                panel.Window.Close();panel.Window.Dispatcher.Invoke(new Action(()=>{}),DispatcherPriority.ApplicationIdle);
                Check(prompts==1&&panel.Window.IsVisible,"window close bypassed cancellation");
                panel.AllowExit=true;panel.Window.Close();
            }
        }
        static int PanelChild(string directory) {
            try {
                var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};var store=new RuntimeStore(directory);
                using(var panel=new Panel(store)) {
                    panel.ClosePrompt=()=>CloseChoice.Tray;
                    if(!panel.ClaimInstance()) return 2;panel.InitializeTray();panel.StartTimer();int step=0;
                    var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(1)};
                    timer.Tick+=async(s,e)=>{try{step++;if(step==1)panel.Window.Close();if(step==5){Check(panel.Window.IsVisible,"duplicate did not restore");timer.Stop();await panel.Exit();}}catch(Exception error){File.WriteAllText(store.FilePath("test-error.txt"),error.ToString());Environment.Exit(1);}};
                    timer.Start();app.Run(panel.Window);
                }
                return 0;
            }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        }
        static void Lifecycle() {
            var store=Store();var executable=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"WuwaTappoHelper.exe");var copy=Path.Combine(store.Root,"WuwaTappoHelper.exe");File.Copy(executable,copy);
            for(int round=0;round<2;round++) {
                using(var first=Process.Start(new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location,"--panel-child \""+store.Root+"\""){UseShellExecute=false,CreateNoWindow=true})) {
                    var deadline=RuntimeStore.Now+12000;var log=store.FilePath("panel-lifecycle.log");int expected=round+1;
                    while(RuntimeStore.Now<deadline&&!first.HasExited) {
                        if(File.Exists(log)&&RuntimeStore.ReadText(log).Split(new[]{"hidden-to-tray"},StringSplitOptions.None).Length>expected)break;Thread.Sleep(100);
                    }
                    var duplicateInfo=new ProcessStartInfo(copy) {UseShellExecute=false,CreateNoWindow=true};
                    duplicateInfo.EnvironmentVariables["PATH"]=Environment.GetFolderPath(Environment.SpecialFolder.System);
                    using(var duplicate=Process.Start(duplicateInfo)) {Check(duplicate.WaitForExit(8000)&&duplicate.ExitCode==0,"duplicate launch without runtimes failed");}
                    if(!first.WaitForExit(12000)) {first.Kill();throw new Exception("test panel did not exit");}
                    Check(first.ExitCode==0,"test panel lifecycle failed");
                }
            }
            Check(!RuntimeStore.Alive(999999),"nonexistent process alive");
        }
    }
}
