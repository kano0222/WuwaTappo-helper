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
using System.Windows.Threading;

namespace Tappo {
    internal sealed class FakeClient : IAppClient {
        bool connected=true;
        public bool Connected {get{return connected;}set{connected=value;}}
        public Snapshot State=Tests.State(); public int Reads,Boosts,Claims,Quits,Closed; public bool ThrowClaim;
        public Func<Snapshot> ClaimResult,SnapshotResult;
        public Func<ServerState> ServerResult;
        public Func<int,Snapshot,Snapshot> BoostResult;
        public Task<Snapshot> Snapshot() {return Task.FromResult(SnapshotResult==null?State:SnapshotResult());}
        public Task<ServerState> Server() {Reads++;return Task.FromResult(ServerResult==null?new ServerState {total=State.cumulative,consumed=State.consumed,left=State.left,right=State.right}:ServerResult());}
        public Task<Snapshot> Boost(int delta,Snapshot before) {Boosts++;if(BoostResult!=null)return Task.FromResult(BoostResult(delta,before));var result=Tests.Copy(before);result.cumulative+=delta;result.available+=delta;State=result;return Task.FromResult(result);}
        public Task<Snapshot> Claim(string side,Snapshot before) {Claims++;if(ThrowClaim) throw new TimeoutException("调用超时，禁止自动重发操作");var result=ClaimResult==null?Tests.Copy(before):ClaimResult();State=result;return Task.FromResult(result);}
        public Task Quit() {Quits++;return Task.FromResult(0);}
        public void Dispose() {Closed++;}
    }
    internal static class Tests {
        static int checks;static string root;
        public static Snapshot State() {return new Snapshot {time=5000,loggedIn=true,available=2000,cumulative=4000,consumed=2000,left=6000,right=7000,buttons=new Dictionary<string,bool>{{"left",true},{"right",true}}};}
        public static Snapshot Copy(Snapshot s) {return Json.Decode<Snapshot>(Json.Encode(s));}
        static void Check(bool value,string message) {if(!value) throw new Exception(message);checks++;}
        static void Reject(Action action,string message) {bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,message);}
        static RuntimeStore Store() {return new RuntimeStore(Path.Combine(root,Guid.NewGuid().ToString("N")));}
        [STAThread] public static int Main(string[] args) {
            if(args.Length==2&&args[0]=="--update-probe") {
                try {var update=Updates.Check(CancellationToken.None).GetAwaiter().GetResult();File.WriteAllText(args[1],Json.Encode(new {current=Updates.CurrentVersion,build=Updates.BuildVersion,latest=update.version,available=update.available,url=update.url}));return 0;}
                catch(Exception error){Console.Error.WriteLine("TLS="+System.Net.ServicePointManager.SecurityProtocol);while(error!=null){Console.Error.WriteLine(error.GetType().Name+": "+error.Message);error=error.InnerException;}return 1;}
            }
            if(args.Length==2&&args[0]=="--process-check") {
                var alive=RuntimeStore.Alive(int.Parse(args[1]));Console.WriteLine("Query-only process check: "+alive);return alive?0:2;
            }
            if(args.Length==2&&args[0]=="--panel-child") return PanelChild(args[1]);
            root=Path.Combine(Path.GetTempPath(),"tappo-native-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try {
                Policies();Files();Clocks().GetAwaiter().GetResult();Workers().GetAwaiter().GetResult();BoostObservations().GetAwaiter().GetResult();Resets().GetAwaiter().GetResult();QueryRetries().GetAwaiter().GetResult();Connections().GetAwaiter().GetResult();ResetPanels();Layouts();CloseChoices();Lifecycle();
                checks+=TransportChecks.Run().GetAwaiter().GetResult();checks+=UpdateChecks.Run().GetAwaiter().GetResult();UpdatePanels();
                Console.WriteLine("PASS: "+checks+" native checks; no account mutations, app restarts, or system policy changes.");return 0;
            } catch(Exception error) {Console.Error.WriteLine(error);return 1;}
            finally {
                var target=Path.GetFullPath(root);var temp=Path.GetFullPath(Path.GetTempPath());
                if(target.StartsWith(temp,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(target).StartsWith("tappo-native-tests-")) Directory.Delete(target,true);
            }
        }
        static void Policies() {
            var s=State();Func<double> random=()=>0.5;
            Check(Policy.Choose(s,"brute",true,random).kind=="wait","early claim");
            s.left=5000;s.available=721;Check(Policy.Choose(s,"brute",true,random).delta==1279,"brute delta");
            s=State();s.available=0;s.buttons["left"]=false;s.buttons["right"]=false;
            Check(Policy.Choose(s,"brute",true,random).kind=="wait","brute behavior changed");
            Check(Policy.Choose(s,"clicker",true,random).delta==100,"clicker rate");
            Check(Policy.Choose(s,"human",true,random).delta==5,"human rate");
            Check(Policy.Choose(s,"human",true,()=>0).delayMs==2000,"human rest");
            Check(Policy.Choose(s,"clicker",false,random).kind=="wait","boost throttling");
            s.available=1999;Check(Policy.Choose(s,"clicker",true,random).delta==1,"target clamp");
            s=State();s.available=1000;s.left=s.right=4000;Check(Policy.Choose(s,"brute",false,random).side=="left","serial slots");
            s.claimBusy=true;Check(Policy.Choose(s,"clicker",true,random).kind=="wait","animation lock");
            s=State();s.available=null;Reject(()=>Policy.Choose(s,"brute",true,random),"missing count accepted");
            s=State();s.left=null;Reject(()=>Policy.Choose(s,"brute",true,random),"missing cooldown accepted");
            s=State();s.loggedIn=false;Check(Policy.Choose(s,"brute",true,random).kind=="wait","logged out");s.loggedIn=true;s.locked=true;Check(Policy.Choose(s,"brute",true,random).kind=="wait","update lock");
            var before=State();before.left=before.right=4000;var after=Copy(before);after.consumed+=1000;after.available-=1000;after.left=9000;
            var a=new ServerState {total=4000,consumed=2000,left=4000,right=4000};var b=new ServerState {total=4000,consumed=3000,left=9000,right=4000};
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
                for(int i=0;i<500;i++) store.Write("preferences.json",new Preference {mode=i%2==0?"brute":"clicker"});
            }
            Check(store.Read<Preference>("preferences.json").mode=="clicker","atomic status replacement");
            Check(Platform.InstallExecutable("\"Z:\\Missing App\"")==@"Z:\Missing App\WuwaTappo.exe","quoted install path");
            Check(!Platform.ValidPath(@"Z:\Missing App\WuwaTappo.exe"),"missing app accepted");
            Check(!Platform.ValidPath("WuwaTappo.exe"),"relative app accepted");
            var missing=Json.Decode<Snapshot>("{\"loggedIn\":true}");Reject(()=>Policy.Choose(missing,"brute",true,()=>0.5),"missing JSON counts accepted");
            Check(Resources.Text("bridge.claim.js").Contains("button.click()")&&!Resources.Text("bridge.claim.js").Contains("chests/open"),"claim bypasses original handler");
            Check(Resources.Text("bridge.discover.js").Contains("inspectModule")&&Resources.Text("bridge.discover.js").Contains("inspectSources"),"missing capability checks");
            store.Write("control.json",new Command {id="previous-stop",action="disconnect"});Panel.PrepareStart(store);
            Check(store.Read<Command>("control.json").action=="resume","new start consumes old disconnect");
            store.Write("lock.json",new {pid=Process.GetCurrentProcess().Id});
            var command=RuntimeStore.ReadText(store.FilePath("control.json"));Reject(()=>Panel.PrepareStart(store),"duplicate worker start accepted");
            Check(RuntimeStore.ReadText(store.FilePath("control.json"))==command,"duplicate start resumed active worker");
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
            foreach(var action in new[]{"disconnect","stop","poll","timeout","reject","success","boost","skip","wrong-slot","unknown-rejection"}) {
                var store=Store();var client=new FakeClient();long now=100000;int loops=0,restores=0;
                bool historical=action=="disconnect"||action=="stop"||action=="poll";
                if(historical) File.WriteAllText(store.FilePath("pending.json"),"{\"historical\":true}");
                if(action=="disconnect"||action=="stop") store.Write("control.json",new Command {id="fixture",action=action});
                if(!historical) {client.State.left=4000;client.State.time=5000;}
                if(action=="timeout") client.ThrowClaim=true;
                if(action=="reject") client.ClaimResult=()=>{var s=Copy(client.State);s.rejected=true;s.code=70006;return s;};
                if(action=="success") client.ClaimResult=()=>{var s=Copy(client.State);s.left=90000;s.consumed+=1000;s.available-=1000;return s;};
                if(action=="boost") {client.State.left=900000;client.State.right=900000;client.State.available=0;store.Write("preferences.json",new Preference {mode="clicker"});}
                if(action=="skip") client.ClaimResult=()=>{var s=Copy(client.State);s.skipped=true;s.reason="条件已变化";s.buttons["left"]=false;return s;};
                if(action=="wrong-slot") client.ClaimResult=()=>{var s=Copy(client.State);s.right=90000;s.consumed+=1000;s.available-=1000;return s;};
                if(action=="unknown-rejection") client.ClaimResult=()=>{var s=Copy(client.State);s.rejected=true;s.code=12345;return s;};
                Worker worker=null;
                worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),async c=>{restores++;await c.Quit();},ms=>{now+=30000;if(++loops==2)worker.RequestDisconnect();return Task.FromResult(0);},()=>now);
                await worker.Run();
                Check(restores==(action=="stop"?1:0),action+" restore intent");Check(client.Quits==restores,action+" quit intent");Check(client.Closed==1,action+" connection cleanup");
                Check(!worker.Status.running&&worker.Status.automationDisconnected,action+" disconnect confirmation");Check(!store.Exists("lock.json"),action+" stale lock");
                if(historical) {Check(File.ReadAllText(store.FilePath("pending.json"))=="{\"historical\":true}","historical record changed");Check(client.Claims==0&&client.Boosts==0,"pending allowed mutations");}
                if(action=="poll") Check(client.Reads==2,"paused historical polling");
                if(action=="timeout") Check(store.Exists("pending.json")&&client.Claims==1,"uncertain claim retried");
                if(action=="reject") Check(!store.Exists("pending.json")&&worker.Status.fault!=null&&client.Claims==1,"rejection confirmation");
                if(action=="success") Check(!store.Exists("pending.json")&&worker.Status.claimsThisRun==1,"confirmed claim not cleared");
                if(action=="boost") Check(!store.Exists("pending.json")&&client.Boosts==2&&client.State.available==200,"native boost loop");
                if(action=="skip") Check(!store.Exists("pending.json")&&client.Claims==1,"skipped action left journal");
                if(action=="wrong-slot"||action=="unknown-rejection") Check(store.Exists("pending.json")&&client.Claims==1,"unconfirmed result retried");
            }
        }
        static async Task BoostObservations() {
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
                store.Write("control.json",new Command {id="reset-test",action="reset",pendingHash=scenario=="changed"?"wrong-hash":hash});
                Worker worker=null;worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=>{worker.RequestDisconnect();return Task.FromResult(0);},()=>100000);
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
            foreach(var scenario in new[]{"recover","manual-pause","disconnect","persistent","snapshot","post-claim","invalid-state"}) {
                var store=Store();var client=new FakeClient();long now=100000;int loops=0,snapshots=0;bool pauseSent=false;
                client.State.left=client.State.right=900000;
                store.Write("preferences.json",new Preference {mode="clicker"});
                if(scenario!="snapshot") client.State.available=0;
                if(scenario=="post-claim") {client.State.available=2000;client.State.left=4000;client.ClaimResult=()=>{var s=Copy(client.State);s.left=900000;s.consumed+=1000;s.available-=1000;return s;};}
                client.ServerResult=()=> {
                    if(scenario=="post-claim"?client.Reads==3:scenario!="snapshot"&&scenario!="invalid-state"&&client.Reads>=2&&(client.Reads<=3||scenario=="persistent")) throw new InvalidOperationException("HTTP 403");
                    return new ServerState {total=scenario=="invalid-state"?null:client.State.cumulative,consumed=client.State.consumed,left=client.State.left,right=client.State.right};
                };
                client.SnapshotResult=()=>{if(++snapshots==1&&scenario=="snapshot")throw new TimeoutException("read timeout");return client.State;};
                Worker worker=null;
                worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=> {
                    now+=ms;loops++;
                    if(worker.Status.readRetryCount>0&&scenario=="manual-pause"&&!pauseSent) {store.Write("control.json",new Command {id="pause-test",action="pause"});pauseSent=true;}
                    if(worker.Status.readRetryCount>0&&scenario=="disconnect") store.Write("control.json",new Command {id="disconnect-test",action="disconnect"});
                    if(client.Boosts>0||worker.Status.fault!=null||scenario=="persistent"&&worker.Status.readRetryCount==6||
                        scenario=="manual-pause"&&pauseSent&&worker.Status.readRetryCount==0||scenario=="snapshot"&&snapshots>1&&worker.Status.readRetryCount==0||loops>260) worker.RequestDisconnect();
                    return Task.FromResult(0);
                },()=>now);
                await worker.Run();
                Check(loops<=260,scenario+" retry did not terminate");
                var log=RuntimeStore.ReadText(store.FilePath("events.jsonl"));
                var retries=log.Split('\n').Where(l=>l.Contains("\"read-query-retry\"")).Select(l=>Json.Decode<Dictionary<string,object>>(l)).ToArray();
                if(scenario=="recover"||scenario=="manual-pause") {
                    Check(retries.Length==2&&Convert.ToInt32(retries[0]["retryAfterSeconds"])==5&&Convert.ToInt32(retries[1]["retryAfterSeconds"])==10,scenario+" backoff sequence");
                    Check(log.Contains("read-query-recovered")&&worker.Status.fault==null&&worker.Status.readRetryAt==null,scenario+" did not recover");
                    Check(client.Boosts==(scenario=="recover"?1:0)&&client.Claims==0,scenario+" manual pause overridden");
                    Check(worker.Status.paused==(scenario=="manual-pause"),scenario+" pause intent lost");
                } else if(scenario=="persistent") {
                    Check(string.Join(",",retries.Select(r=>r["retryAfterSeconds"].ToString()))=="5,10,20,40,60,60","retry delay not capped");
                    Check(client.Boosts==0&&client.Claims==0&&worker.Status.fault==null,"read failure allowed mutation or permanent fault");
                } else if(scenario=="post-claim") {
                    Check(client.Claims==1&&store.Exists("pending.json")&&retries.Length==0&&worker.Status.fault!=null,"post-claim query failure resent or cleared action");
                } else if(scenario=="invalid-state") {
                    Check(retries.Length==0&&worker.Status.fault!=null&&client.Boosts==0,"malformed state retried");
                } else if(scenario=="snapshot") {
                    Check(retries.Length==1&&log.Contains("read-query-recovered")&&client.Boosts==0,"snapshot retry did not recover");
                } else Check(retries.Length==1&&client.Reads==2&&client.Boosts==0,"disconnect waited for retry");
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
                if(scenario=="claim-disconnect") {old.State.left=4000;old.ClaimResult=()=>{old.Connected=false;throw new IOException("claim connection lost");};}
                Worker worker=null;
                worker=new Worker(store,()=>{initial++;return Task.FromResult<IAppClient>(old);},c=>{throw new Exception("must not restart pet");},ms=> {
                    now+=ms;loops++;
                    if(fresh.Reads>0||worker.Status.fault!=null||scenario=="http"&&worker.Status.readRetryCount==2||loops>50)worker.RequestDisconnect();
                    return Task.FromResult(0);
                },()=>now,()=>{reconnections++;if(scenario=="unavailable"&&reconnections<3)throw new IOException("port not ready");return Task.FromResult<IAppClient>(fresh);});
                await worker.Run();
                Check(initial==1&&loops<=50,scenario+" restarted pet or stalled");
                if(scenario=="reconnect"||scenario=="unavailable") {
                    Check(reconnections==(scenario=="unavailable"?3:1)&&fresh.Reads>0&&worker.Status.fault==null,scenario+" did not restore connection");
                    Check(old.Closed==1&&fresh.Closed==1,scenario+" connection not disposed");
                } else Check(reconnections==0,scenario+" reconnected while outcome pending or transport alive");
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
                panel.ReadStatus=()=>new Status {running=true,pid=Process.GetCurrentProcess().Id,at=DateTimeOffset.UtcNow.ToString("o")};
                panel.Refresh();Check(!panel.E<Button>("ResetPendingButton").IsEnabled,"in-flight reset enabled");
                bool asked=false;panel.ConfirmReset=()=>{asked=true;return true;};panel.ResetPending().GetAwaiter().GetResult();
                Check(!asked&&store.Exists("pending.json"),"in-flight reset accepted");
                panel.ReadStatus=()=>new Status {running=true,paused=true,fault="unknown",pid=Process.GetCurrentProcess().Id,at=DateTimeOffset.UtcNow.ToString("o")};
                panel.Refresh();Check(panel.E<Button>("ResetPendingButton").IsEnabled,"blocked reset disabled");
                string dispatched=null;panel.DispatchOverride=action=>dispatched=action;panel.ResetPending().GetAwaiter().GetResult();Check(dispatched=="reset","reset button did not dispatch");
            }
        }
        static void Layouts() {
            var store=Store();using(var panel=new Panel(store)) {
                panel.InitializeTray();
                var state=State();state.left=RuntimeStore.Now+650000;state.right=RuntimeStore.Now-1000;
                var status=new Status {at=DateTimeOffset.UtcNow.ToString("o"),pid=Process.GetCurrentProcess().Id,running=true,mode="brute",state=state,
                    chestTimes=new ChestTimes {updatedAt=RuntimeStore.Now,left=state.left,right=state.right}};
                panel.ReadStatus=()=>status;
                var images=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"previews");Directory.CreateDirectory(images);
                panel.Render(Path.Combine(images,"running.png"));
                Check(panel.E<ScrollViewer>("MainScroll").ScrollableHeight<1,"default layout scrolls");Check(panel.E<Border>("ChestArea").ActualHeight<100,"chests too tall");
                Check(panel.E<TextBlock>("LeftTime").Text.Contains(":"),"server countdown missing");
                Check(panel.E<TextBlock>("RightTime").Text=="已就绪","ready slot missing");
                Check(!panel.E<Button>("StartButton").IsEnabled&&panel.E<Button>("PauseButton").IsEnabled,"running controls");
                status.readRetryAt=RuntimeStore.Now+10000;status.readRetryError="读取服务器状态失败";panel.Refresh();
                Check(panel.E<TextBlock>("StatusLabel").Text=="等待重试"&&panel.E<TextBlock>("DetailMessage").Text.Contains("不补数、不领取"),"retry status missing");
                status.paused=true;panel.Refresh();Check(panel.E<TextBlock>("StatusLabel").Text=="已暂停","retry hid manual pause");
                status.paused=false;status.readRetryAt=null;status.readRetryError=null;panel.Refresh();
                Check(panel.Window.FindName("ExitButton")==null,"unexpected title exit button");
                Check(panel.E<MenuItem>("ExitMenuItem").IsEnabled&&panel.TrayExitEnabled,"exit entry unavailable");
                panel.ReadStatus=()=>{throw new UnauthorizedAccessException("模拟读取失败");};panel.Refresh();
                Check(panel.TrayExitEnabled&&panel.E<MenuItem>("ExitMenuItem").IsEnabled,"status error disabled exit");panel.ReadStatus=()=>status;panel.Refresh();
                Check(panel.E<TextBlock>("ActivityText").Visibility==Visibility.Collapsed,"empty activities not collapsed");
                status.paused=true;panel.Refresh();Check((string)panel.E<Button>("PauseButton").Content=="继续运行","pause label");
                File.WriteAllText(store.FilePath("pending.json"),"{\"historical\":true}");panel.Refresh();Check(!panel.E<Button>("PauseButton").IsEnabled&&panel.E<Button>("StopButton").IsEnabled,"pending controls");
                store.Log("started");store.Log("boost-confirmed",new {delta=100});store.Log("claim-confirmed",new {side="left"});panel.Render(Path.Combine(images,"paused.png"));
                Check(panel.E<TextBlock>("ActivityText").Text.Split('\n').Length==3,"activity history");
                status.chestTimes.error="timeout";panel.Refresh();Check(panel.E<TextBlock>("LeftTime").Text=="等待同步"&&panel.E<Border>("LeftFill").Width==0,"failed API displayed live time");
                status.chestTimes.error=null;status.chestTimes.updatedAt=RuntimeStore.Now-100000;panel.Refresh();Check(panel.E<TextBlock>("LeftTime").Text=="等待同步","expired time");
                status.running=false;panel.Render(Path.Combine(images,"disconnected.png"));Check(panel.E<TextBlock>("LeftTime").Text=="未连接","disconnected view");
                status.fault="桌宠主窗口尚未就绪";panel.Refresh();Check(panel.E<TextBlock>("DetailMessage").Text==status.fault,"stopped failure hidden behind restart hint");status.fault=null;
                panel.E<Expander>("UsageHint").IsExpanded=true;panel.SetPath(@"C:\Long installation path\Long installation path\Long installation path\WuwaTappo.exe");panel.Render(Path.Combine(images,"minimum.png"),820,660);
                Check(panel.E<ScrollViewer>("MainScroll").ScrollableHeight>0,"minimum expanded window does not scroll");
                var surface=panel.E<Border>("RootSurface");var start=panel.E<Button>("StartButton").TranslatePoint(new Point(),surface);var stop=panel.E<Button>("StopButton").TranslatePoint(new Point(),surface);
                Check(start.Y==stop.Y&&stop.X+panel.E<Button>("StopButton").ActualWidth<796,"control row overflow");
                var modes=panel.E<ListBox>("ModeSelector");modes.SelectedIndex=1;modes.SelectedIndex=2;Check(store.Read<Preference>("preferences.json").mode=="human","mode not persisted");
                File.Delete(store.FilePath("preferences.json"));Directory.CreateDirectory(store.FilePath("preferences.json"));
                modes.SelectedIndex=1;Check((string)((ListBoxItem)modes.SelectedItem).Tag=="human","failed save changed selection");Directory.Delete(store.FilePath("preferences.json"));
            }
            foreach(var entry in new[]{"context","tray"}) using(var panel=new Panel(Store())) {
                panel.InitializeTray();panel.ReadStatus=()=>new Status {running=true,pid=Process.GetCurrentProcess().Id};string dispatched=null;panel.DispatchOverride=action=>dispatched=action;
                if(entry=="context") panel.E<MenuItem>("ExitMenuItem").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                else ((System.Windows.Forms.ToolStripMenuItem)typeof(Panel).GetField("trayExit",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(panel)).PerformClick();
                Check(dispatched=="disconnect",entry+" exit requested restore");
            }
        }
        static void UpdatePanels() {
            var store=Store();using(var panel=new Panel(store)) {
                panel.ReadStatus=()=>new Status {running=true,pid=Process.GetCurrentProcess().Id};
                panel.UpdateCheck=token=>Task.FromResult(new UpdateInfo {version="0.1.1",available=false});
                panel.CheckForUpdates().GetAwaiter().GetResult();Check(panel.E<Button>("UpdateButton").Content.ToString().Contains("已是最新"),"up-to-date UI");
                panel.UpdateCheck=token=>Task.FromResult(new UpdateInfo {version="9.9.9",available=true,url="https://github.com/kano0222/ww-tappo/releases/tag/v9.9.9"});
                panel.CheckForUpdates().GetAwaiter().GetResult();string opened=null;panel.OpenUpdatePage=url=>opened=url;
                panel.Render(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"previews","update-available.png"),820,660);
                var updateButton=panel.E<Button>("UpdateButton");var position=updateButton.TranslatePoint(new Point(),panel.E<Border>("RootSurface"));
                Check(position.X>=0&&position.X+updateButton.ActualWidth<=820&&position.Y+updateButton.ActualHeight<=660,"update button overflow");
                panel.UpdateAction().GetAwaiter().GetResult();Check(opened!=null&&opened.EndsWith("v9.9.9"),"update page not opened");
                panel.UpdateCheck=token=>{throw new InvalidOperationException("GitHub 暂时限制查询");};panel.CheckForUpdates().GetAwaiter().GetResult();
                Check(panel.E<Button>("UpdateButton").IsEnabled&&panel.E<Button>("UpdateButton").Content.ToString().Contains("失败"),"update failure disabled controls");
                int retries=0;panel.UpdateCheck=token=>{retries++;return Task.FromResult(new UpdateInfo {version="0.1.1"});};
                panel.UpdateAction().GetAwaiter().GetResult();Check(retries==1,"retry opened stale update page");
                Check(!store.Exists("control.json")&&panel.ReadStatus().running,"update check changed automation");
                var waiting=new TaskCompletionSource<UpdateInfo>();int calls=0;
                panel.UpdateCheck=token=>{calls++;return waiting.Task;};var first=panel.CheckForUpdates();
                panel.CheckForUpdates().GetAwaiter().GetResult();Check(calls==1&&!panel.E<Button>("UpdateButton").IsEnabled,"overlapping update requests");
                waiting.SetResult(new UpdateInfo {version="0.1.1"});panel.Window.Dispatcher.Invoke(new Action(()=>{}),DispatcherPriority.ApplicationIdle);first.GetAwaiter().GetResult();
                Check(panel.E<Button>("UpdateButton").IsEnabled,"update button not restored");
            }
        }
        static void CloseChoices() {
            foreach(var choice in new[]{CloseChoice.Cancel,CloseChoice.Tray,CloseChoice.Exit}) using(var panel=new Panel(Store())) {
                panel.InitializeTray();panel.ReadStatus=()=>new Status {running=true,pid=Process.GetCurrentProcess().Id};panel.Window.Show();
                string dispatched=null;panel.DispatchOverride=action=>dispatched=action;panel.ClosePrompt=()=>choice;
                panel.E<Button>("CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(panel.Window.IsVisible==(choice==CloseChoice.Cancel),choice+" close visibility");
                Check(dispatched==(choice==CloseChoice.Exit?"disconnect":null),choice+" close automation action");
                Check(panel.AllowExit==(choice==CloseChoice.Exit),choice+" close exit permission");
                if(choice!=CloseChoice.Exit) {panel.AllowExit=true;panel.Window.Close();}
            }
            foreach(var action in new[]{"exit","tray","cancel","escape"}) {
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
                    else if(action=="escape") dialog.Window.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(dialog.Window),0,System.Windows.Input.Key.Escape){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent});
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
