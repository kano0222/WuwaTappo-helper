using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Tappo {
    internal static class DiagnosticEvidenceChecks {
        static int checks;
        static void Check(bool pass,string message) {checks++;if(!pass)throw new Exception(message);}
        internal static int Fixture(string root) {
            var store=new RuntimeStore(root);
            using(var session=new DiagnosticSession(store,"worker")) {
                store.Write("status.json",new Status {identity=session.Identity,pid=session.Identity.pid,running=true,at=DateTimeOffset.UtcNow.ToString("o")});
                store.Write("fixture-ready.json",new {ready=true});
                Thread.Sleep(30000);
            }
            return 0;
        }
        internal static int Run() {
            checks=0;var root=Path.Combine(Path.GetTempPath(),"tappo-evidence-"+Guid.NewGuid().ToString("N"));
            try {
                Stages(new RuntimeStore(Path.Combine(root,"stages"))).GetAwaiter().GetResult();
                BlockedWorker(new RuntimeStore(Path.Combine(root,"blocked-worker"))).GetAwaiter().GetResult();
                Exit(new RuntimeStore(Path.Combine(root,"exit")));
                WriteFailure(new RuntimeStore(Path.Combine(root,"write-failure"))).GetAwaiter().GetResult();
                PauseTimeout(new RuntimeStore(Path.Combine(root,"pause")));
                Retention(new RuntimeStore(Path.Combine(root,"retention")));
            } finally {Directory.Delete(root,true);}
            return checks;
        }
        static async Task BlockedWorker(RuntimeStore store) {
            var gate=new TaskCompletionSource<Snapshot>();var client=new FakeClient {SnapshotTask=()=>gate.Task};
            var worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),Task.Delay,()=>RuntimeStore.Now);
            var run=worker.Run();
            try {
                var path=Path.Combine(store.Runtime,"diagnostics",worker.Status.identity.session,"health.json");
                var until=DateTime.UtcNow.AddSeconds(8);DiagnosticState first=null,latest=null;
                while(DateTime.UtcNow<until) {
                    latest=DiagnosticSession.ReadBounded<DiagnosticState>(path);
                    if(latest!=null&&latest.phase=="read-snapshot") {
                        if(first==null)first=latest;
                        else if(latest.at!=first.at&&latest.phaseElapsedMs>first.phaseElapsedMs)break;
                    }
                    await Task.Delay(30);
                }
                Check(first!=null&&latest!=null&&latest.at!=first.at&&!run.IsCompleted,"independent thread heartbeat stalled with the production worker call");
                Check(latest.phase=="read-snapshot"&&latest.phaseId!=null,"production client wrapper loses blocked call identity");
                Check(client.Boosts==0&&client.Claims==0,"health sampling triggered a business operation");
            } finally {
                worker.RequestDisconnect();gate.TrySetResult(new Snapshot {loggedIn=false});run.GetAwaiter().GetResult();
            }
        }
        static async Task Stages(RuntimeStore store) {
            long now=RuntimeStore.Now;
            using(var session=new DiagnosticSession(store,"worker",false,()=>now)) {
                var status=new Status {identity=session.Identity,pid=session.Identity.pid,running=true,at=DateTimeOffset.UtcNow.ToString("o")};
                store.Write("status.json",status);session.Published(status);
                try {
                    await session.Call<int>("connect",()=>{throw new ConnectionFailureException("debug-unavailable","PRIVATE-COMMAND-LINE","port-listeners-unverified");});
                } catch(ConnectionFailureException) { }
                var evidence=Json.Encode(session.Copy());
                Check(evidence.Contains("port-listeners-unverified")&&!evidence.Contains("PRIVATE-COMMAND-LINE"),"port failure classification missing or raw exception leaked into diagnostics");
                Check(RuntimeIdentity.Inspect(session.Identity)=="matched","own PID/creation identity does not match");
                var replacement=Json.Decode<RuntimeIdentity>(Json.Encode(session.Identity));replacement.processCreated--;
                Check(RuntimeIdentity.Inspect(replacement)=="pid-reused","PID reuse is presented as a matching worker");
                Check(DiagnosticSession.Category(status,session.Copy(),now,"pid-reused")=="pid-reused","PID mismatch category lost");
                var gate=new TaskCompletionSource<int>();var call=session.Call("read-server",()=>gate.Task);
                now+=46000;session.Tick();session.ObserveSelf();
                var blocked=session.Copy();
                Check(!call.IsCompleted&&blocked.phase=="read-server"&&blocked.phaseElapsedMs>=46000,"blocked call stage or elapsed time unavailable");
                Check(DiagnosticSession.Category(status,blocked,now,"matched")=="operation-waiting","blocked operation is indistinguishable from dead heartbeat");
                Check(blocked.events.Any(e=>e.kind=="heartbeat-gap"),"scheduling gap not captured");
                session.Power("Suspend");session.Power("Resume");session.Tick();
                Check(session.Copy().powerEvent=="Resume"&&session.Copy().events.Count(e=>e.kind=="power")==2,"power evidence lost");
                gate.SetResult(7);Check(await call==7,"instrumentation changed the result");
                Check(session.Copy().events.Any(e=>e.kind=="stage-end"&&e.result=="completed"&&e.elapsedMs==46000),"stage completion correlation missing");
                var outerGate=new TaskCompletionSource<int>();
                var outer=session.Call("close-pet",async()=>{await session.Call("quit",()=>Task.FromResult(0));return await outerGate.Task;});
                Check(session.Copy().phase=="close-pet","nested quit hides the remaining close-pet wait");outerGate.SetResult(0);await outer;
                try {await session.Call<int>("read-snapshot",()=>{throw new IOException("Cookie: PRIVATE-SECRET C:\\Users\\private\\token");});}catch(IOException) { }
                store.Log("operation-state",new {operationId="test-op",stage="verify",code=70006,httpStatus=503,errorType="AppQueryException",detail="PRIVATE-SECRET",before=new {accountKey="PRIVATE-ACCOUNT"}});
                store.Log("operation-state",new {operationId="test-op",stage="verify",outcome="pending",waitReason="consumption.cooldown.cumulative"});
                session.Tick();
                var bundle=Path.Combine(store.Root,"stages.zip");session.Export(bundle);
                var text=Bundle(bundle);
                Check(text.Contains("operation-waiting")&&text.Contains("read-server")&&text.Contains("70006")&&text.Contains("503"),"ZIP cannot identify stage/business error category");
                Check(text.Contains("consumption.cooldown.cumulative"),"ZIP omitted the claim wait conditions");
                Check(!text.Contains("PRIVATE-SECRET")&&!text.Contains("PRIVATE-ACCOUNT")&&!text.Contains(store.Root)&&!text.Contains("C:\\\\Users"),"export leaked a credential, account or personal path");
                var stale=Json.Decode<DiagnosticState>(Json.Encode(blocked));stale.at=DateTimeOffset.FromUnixTimeMilliseconds(now-20000).ToString("o");
                Check(DiagnosticSession.Category(status,stale,now,"matched")=="heartbeat-stale","dead heartbeat presented as live business stall");
                var other=Json.Decode<Status>(Json.Encode(status));other.identity.session=Guid.NewGuid().ToString("N");
                Check(session.ReadWorker(other)==null&&DiagnosticSession.Category(other,blocked,now,"matched")=="session-evidence-missing","history silently treated as current health");
                other.identity.dataDirectory="other-directory";Check(!session.StatusBelongs(other),"wrong data directory accepted");
                store.Write("lock.json",other);Check(!session.StatusBelongs(status),"old status accepted after a new session reservation");
                Check(DiagnosticSession.Category(new Status(),null,now,"matched")=="legacy-status-identity-unavailable","legacy PID-only state presented as confirmed identity");
            }
            using(var panel=new DiagnosticSession(store,"panel",false)) {
                panel.Observe();panel.Export(Path.Combine(store.Root,"reopened.zip"));
                Check(Bundle(Path.Combine(store.Root,"reopened.zip")).Contains("operation-waiting"),"reopening loses previous blocked-operation snapshot");
            }
        }
        static void Exit(RuntimeStore store) {
            var info=new ProcessStartInfo(typeof(Tests).Assembly.Location,"--diagnostic-process \""+store.Root+"\"") {UseShellExecute=false,CreateNoWindow=true};
            using(var process=Process.Start(info)) {
                try {
                    var until=DateTime.UtcNow.AddSeconds(10);
                    while(!store.Exists("fixture-ready.json")&&DateTime.UtcNow<until)Thread.Sleep(20);
                    Check(store.Exists("fixture-ready.json"),"isolated worker fixture did not start");
                    process.Kill();Check(process.WaitForExit(5000),"isolated fixture failed to exit");
                } finally {if(!process.HasExited){process.Kill();process.WaitForExit(5000);}}
            }
            using(var observer=new DiagnosticSession(store,"panel",false)) {
                observer.Observe();observer.Export(Path.Combine(store.Root,"exit.zip"));
                Check(Bundle(Path.Combine(store.Root,"exit.zip")).Contains("process-exited"),"abrupt exit requires the worker's exit log to be diagnosed");
            }
            using(var observer=new DiagnosticSession(store,"panel",false)) {
                observer.Export(Path.Combine(store.Root,"reopened.zip"));
                Check(Bundle(Path.Combine(store.Root,"reopened.zip")).Contains("process-exited"),"panel restart loses abrupt-exit evidence");
            }
        }
        static async Task WriteFailure(RuntimeStore store) {
            // Force the production Publish path to fail without changing permissions or real runtime files.
            Directory.CreateDirectory(store.FilePath("status.json"));
            var client=new FakeClient();
            var worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),Task.Delay,()=>RuntimeStore.Now);
            try {await worker.Run();}catch(IOException) { }catch(UnauthorizedAccessException) { }
            using(var observer=new DiagnosticSession(store,"panel",false)) {
                observer.Observe();observer.Export(Path.Combine(store.Root,"write.zip"));
                var text=Bundle(Path.Combine(store.Root,"write.zip"));
                Check(text.Contains("status-write-failed")&&text.Contains("status-read-failed"),"status write failure lost when worker exits");
                Check(client.Boosts==0&&client.Claims==0,"write-failure diagnosis mutated account state");
            }
        }
        static void PauseTimeout(RuntimeStore store) {
            using(var worker=new DiagnosticSession(store,"worker",false)) {
                var status=new Status {identity=worker.Identity,pid=worker.Identity.pid,running=true,at=DateTimeOffset.UtcNow.ToString("o")};
                store.Write("status.json",status);worker.Published(status);worker.Tick();
                using(var panel=new Panel(store,preview:true)) {
                    panel.ControlTimeoutMs=1;
                    var previous=SynchronizationContext.Current;
                    try {
                        var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
                        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(dispatcher));
                        var task=panel.Act("pause");var frame=new System.Windows.Threading.DispatcherFrame();
                        task.ContinueWith(_=>dispatcher.BeginInvoke(new Action(()=>frame.Continue=false)));
                        System.Windows.Threading.Dispatcher.PushFrame(frame);
                        try {task.GetAwaiter().GetResult();throw new Exception("unacknowledged pause succeeded");}catch(TimeoutException) { }
                    } finally {SynchronizationContext.SetSynchronizationContext(previous);}
                    Check(store.Read<Command>("control.json").action=="pause","fixture did not send pause");
                    Check(panel.E<Button>("ExportDiagnosticsButton").IsEnabled,"timeout prevents exporting diagnostics");
                    panel.Render(Path.Combine(store.Root,"diagnostic-button.png"),740,660);
                    var button=panel.E<Button>("ExportDiagnosticsButton");
                    var point=button.TranslatePoint(new System.Windows.Point(),panel.E<System.Windows.Controls.Border>("RootSurface"));
                    Check(button.ActualWidth>=60&&point.X>=0&&point.X+button.ActualWidth<=740,"export button clipped at the minimum supported width");
                }
                using(var collector=new DiagnosticSession(store,"panel",false)) {
                    collector.Export(Path.Combine(store.Root,"pause.zip"));var text=Bundle(Path.Combine(store.Root,"pause.zip"));
                    Check(text.Contains("control-timeout")&&text.Contains("command-requested")&&text.Contains(store.Read<Command>("control.json").id),"ZIP cannot correlate unacknowledged pause");
                }
            }
        }
        static void Retention(RuntimeStore store) {
            // A recycled PID must not pin an abandoned session forever.
            var retired=RuntimeIdentity.Create(store,"worker");retired.processCreated--;
            var retiredDirectory=Path.Combine(store.Runtime,"diagnostics",retired.session);Directory.CreateDirectory(retiredDirectory);
            RuntimeStore.AtomicWrite(Path.Combine(retiredDirectory,"health.json"),new DiagnosticState {identity=retired,ended=false});
            Directory.SetLastWriteTimeUtc(retiredDirectory,DateTime.UtcNow.AddDays(-1));
            for(int i=0;i<DiagnosticSession.MaxSessions+3;i++)using(var session=new DiagnosticSession(store,"worker",false)) {
                for(int j=0;j<DiagnosticSession.MaxSnapshots+2;j++)session.Capture("fixture");
            }
            using(var session=new DiagnosticSession(store,"panel",false)) {
                session.Tick();
                var root=store.FilePath("diagnostics");
                Check(Directory.GetDirectories(root).Length<=DiagnosticSession.MaxSessions,"inactive session history not rotated");
                Check(!Directory.Exists(retiredDirectory),"PID reuse prevented abandoned session rotation");
                Check(Directory.GetDirectories(root).All(d=>Directory.GetFiles(d,"fault-*.json").Length<=DiagnosticSession.MaxSnapshots),"fault snapshot rotation exceeded limit");
                store.Write("config.json",new {token="PRIVATE-CONFIG"});
                session.Export(Path.Combine(store.Root,"retention.zip"));
                Check(!Bundle(Path.Combine(store.Root,"retention.zip")).Contains("PRIVATE-CONFIG"),"non-allowlisted config included");
                using(var zip=ZipFile.OpenRead(Path.Combine(store.Root,"retention.zip")))Check(zip.Entries.Sum(e=>e.Length)<=DiagnosticSession.MaxBundleBytes,"ZIP uncompressed content exceeds limit");
            }
        }
        static string Bundle(string path) {
            using(var zip=ZipFile.OpenRead(path))return string.Join("\n",zip.Entries.Select(entry=>{using(var reader=new StreamReader(entry.Open()))return reader.ReadToEnd();}));
        }
    }
}
