using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Tappo {
    internal sealed class RuntimeIdentity {
        public string session,role,version,fingerprint,startedAt,instance,dataDirectory,programDirectory,target;
        public int pid; public long processCreated;
        internal static RuntimeIdentity Create(RuntimeStore store,string role) {
            using(var process=Process.GetCurrentProcess()) {
                var assembly=Assembly.GetExecutingAssembly();string fingerprint;
                try {using(var sha=SHA256.Create())using(var stream=File.OpenRead(assembly.Location))fingerprint=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
                catch {fingerprint="unavailable";}
                string target=null;try {var path=Instances.SavedPath(store.Root);if(path!=null)target=Instances.Key(path);}catch { }
                return new RuntimeIdentity {session=Guid.NewGuid().ToString("N"),role=role,version=Updates.DisplayVersion,target=target,
                    fingerprint=fingerprint,startedAt=process.StartTime.ToUniversalTime().ToString("o"),pid=process.Id,
                    processCreated=process.StartTime.ToUniversalTime().ToFileTimeUtc(),instance=store.InstanceId??"primary",
                    dataDirectory=Instances.Key(store.Runtime),programDirectory=Instances.Key(Path.GetDirectoryName(assembly.Location))};
            }
        }
        // Unknown access is deliberately distinct from both a match and an exit.
        internal static string Inspect(RuntimeIdentity expected) {
            if(expected==null)return "identity-unavailable";
            using(var actual=ProcessIdentity.Open(expected.pid)) {
                if(actual==null)return RuntimeStore.Alive(expected.pid)?"identity-unverifiable":"process-exited";
                return actual.Created==expected.processCreated?"matched":"pid-reused";
            }
        }
    }
    internal sealed class DiagnosticEvent {
        public string at,kind,stage,id,result,errorType,failureReason; public long elapsedMs; public int code,httpStatus;
    }
    internal sealed class DiagnosticPeer {
        public int pid; public long processCreated; public string executable;
    }
    internal sealed class DiagnosticState {
        public int schema=1; public RuntimeIdentity identity; public string at,phase,phaseId,phaseAt,progressAt,statusAt,writeError,powerEvent,powerAt;
        public long phaseElapsedMs,heartbeatGapMs; public bool ended,connected;
        public int port; public string connectionOwner,connectionAt,peerSession,commandId,commandAction; public DiagnosticPeer[] connectionPeers;
        public DiagnosticEvent[] events;
    }
    internal sealed class DiagnosticSnapshot {
        public string at,category,commandId,observedProcess,evidenceLimit="Evidence identifies the observed stage/category, not an ultimate root cause.";
        public DiagnosticState observer,worker; public RuntimeIdentity statusIdentity; public string statusAt,statusReadError;
    }
    // Only fixed, credential-free facts enter this store. No arbitrary exception messages,
    // request/response bodies, account snapshots, command payloads or filesystem paths.
    internal sealed class DiagnosticSession : IDisposable {
        internal const int MaxSessions=8,MaxSnapshots=12,MaxFileBytes=384*1024,MaxBundleBytes=8*1024*1024;
        readonly RuntimeStore store; readonly object gate=new object(); readonly Queue<DiagnosticEvent> events=new Queue<DiagnosticEvent>();
        readonly AutoResetEvent wake=new AutoResetEvent(false); readonly Thread thread;
        readonly DiagnosticState state; readonly string directory; readonly Action<string,object> logSink;
        readonly Func<long> now; long phaseTick,lastBeat; string lastIncident,pendingIncident,lastBusinessIncident; volatile bool stopping;
        internal readonly RuntimeIdentity Identity;
        internal Action Sample;
        internal DiagnosticSession(RuntimeStore store,string role,bool background=true,Func<long> now=null) {
            this.store=store;this.now=now??(()=>RuntimeStore.Now);Identity=RuntimeIdentity.Create(store,role);
            directory=Path.Combine(store.Runtime,"diagnostics",Identity.session);
            state=new DiagnosticState {identity=Identity,phase="initializing",phaseAt=Identity.startedAt,progressAt=Identity.startedAt};
            phaseTick=this.now();lastBeat=phaseTick;
            logSink=(kind,data)=>BusinessEvent(kind,data);store.DiagnosticEvent+=logSink;
            try {SystemEvents.PowerModeChanged+=PowerChanged;}catch(Exception error) {Record("power-monitor-unavailable",errorType:error.GetType().Name);}
            Record("session-start");
            if(background) {thread=new Thread(Loop) {IsBackground=true,Name="Tappo diagnostics "+role};thread.Start();}
        }
        void PowerChanged(object sender,PowerModeChangedEventArgs args) {Power(args.Mode.ToString());}
        internal void Power(string mode) {
            lock(gate) {state.powerEvent=mode;state.powerAt=DateTimeOffset.UtcNow.ToString("o");}
            Record("power",result:mode);Signal();
        }
        void Signal() {try {wake.Set();}catch(ObjectDisposedException) { }}
        internal void Record(string kind,string stage=null,string id=null,string result=null,string errorType=null,long elapsed=0,int code=0,int httpStatus=0,string failureReason=null) {
            lock(gate) {
                events.Enqueue(new DiagnosticEvent {at=DateTimeOffset.UtcNow.ToString("o"),kind=Label(kind),stage=Label(stage),id=Label(id),result=Label(result),errorType=Label(errorType),elapsedMs=elapsed,code=code,httpStatus=httpStatus,failureReason=Label(failureReason)});
                while(events.Count>192)events.Dequeue();
            }
        }
        static string Label(string value) {return value==null?null:value.Length<=96&&value.All(c=>c>='a'&&c<='z'||c>='A'&&c<='Z'||c>='0'&&c<='9'||c=='-'||c=='_'||c=='.')?value:"unavailable";}
        void BusinessEvent(string kind,object data) {
            try {
                var values=data==null?new Dictionary<string,object>():Json.Decode<Dictionary<string,object>>(Json.Encode(data));
                Func<string,string> field=key=>values.ContainsKey(key)?System.Convert.ToString(values[key]):null;
                int code,http;int.TryParse(field("code"),out code);int.TryParse(field("httpStatus"),out http);
                // Event names and classifications are generated by this program; no detail/message fields.
                Record(kind,field("stage"),field("operationId"),field("outcome"),field("errorType"),code:code,httpStatus:http,failureReason:field("waitReason"));
                if(kind=="paused-on-error"||kind=="stopped-on-error"||kind=="read-query-retry"||kind=="operation-state"&&field("errorType")!=null) {
                    var key=kind+":"+field("stage")+":"+field("errorType")+":"+code+":"+http;
                    lock(gate)if(key!=lastBusinessIncident) {lastBusinessIncident=key;pendingIncident=kind;}
                    Signal();
                }
            } catch { }
        }
        internal async Task<T> Call<T>(string phase,Func<Task<T>> operation) {
            var id=Guid.NewGuid().ToString("N");var started=now();
            string parentPhase,parentId,parentAt;long parentTick;
            lock(gate) {parentPhase=state.phase;parentId=state.phaseId;parentAt=state.phaseAt;parentTick=phaseTick;
                state.phase=phase;state.phaseId=id;state.phaseAt=DateTimeOffset.UtcNow.ToString("o");phaseTick=started;}
            Record("stage-start",phase,id);Signal();
            try {var value=await operation().ConfigureAwait(false);Record("stage-end",phase,id,"completed",elapsed:Math.Max(0,now()-started));return value;}
            catch(Exception error) {
                var failure=error as ConnectionFailureException;
                Record("stage-end",phase,id,"failed",error.GetType().Name,Math.Max(0,now()-started),failureReason:failure==null?null:failure.Reason);throw;
            }
            finally {lock(gate) {state.phase=parentId==null?"between-operations":parentPhase;state.phaseId=parentId;state.phaseAt=parentAt;
                state.progressAt=DateTimeOffset.UtcNow.ToString("o");phaseTick=parentId==null?now():parentTick;}Signal();}
        }
        internal Task Call(string phase,Func<Task> operation) {return Call<int>(phase,async()=>{await operation().ConfigureAwait(false);return 0;});}
        string completedCommand;
        internal void Published(Status status) {
            lock(gate) {
                state.statusAt=status.at;state.writeError=null;state.connected=status.connected==true;
                if(state.phaseId==null) {
                    state.phase=status.readRetryAt.HasValue?"retry-backoff":status.manuallyPaused?"paused":status.recovering?"verification-wait":status.running?"policy-wait":"stopped";
                    state.phaseAt=status.at;phaseTick=now();state.progressAt=status.at;
                }
                if(status.commandId!=null&&completedCommand!=status.commandId) {
                    completedCommand=status.commandId;Record("command-completed",id:status.commandId,result:status.resetError==null?"status-published":"reset-rejected");
                }
            }
        }
        internal void WriteFailed(Exception error) {lock(gate)state.writeError=error.GetType().Name;Record("status-write-failed",errorType:error.GetType().Name);Signal();}
        internal void Connection(bool connected,int port=0,string owner=null) {lock(gate) {state.connected=connected;state.port=port;state.connectionOwner=owner;}}
        internal void ConnectionEvidence(AppClient app) {lock(gate) {state.connectionAt=app.DiagnosticConnectionAt;state.connectionPeers=app.DiagnosticPeers;}}
        internal void Command(string kind,Command command,string result=null) {
            lock(gate) {state.commandId=command.id;state.commandAction=command.action;}
            Record(kind,command.action,command.id,result);Signal();
        }
        internal DiagnosticState Copy() {
            lock(gate) {state.at=DateTimeOffset.FromUnixTimeMilliseconds(now()).ToString("o");state.phaseElapsedMs=Math.Max(0,now()-phaseTick);state.events=events.ToArray();return Json.Decode<DiagnosticState>(Json.Encode(state));}
        }
        void Loop() {
            try {while(!stopping) {Tick();wake.WaitOne(5000);}Tick();}
            finally {wake.Dispose();}
        }
        internal void Tick() {
            try {
                var tick=now();var gap=Math.Max(0,tick-lastBeat);lastBeat=tick;
                if(gap>15000) {lock(gate)state.heartbeatGapMs=gap;Record("heartbeat-gap",result:"sleep-or-scheduling-gap",elapsed:gap);}
                string incident;lock(gate) {incident=pendingIncident;pendingIncident=null;}
                if(incident!=null)Capture(incident,worker:Copy(),process:"self");
                if(Sample!=null)Sample();
                Directory.CreateDirectory(directory);
                RuntimeStore.AtomicWrite(Path.Combine(directory,"health.json"),Copy());
                Prune();
            } catch(Exception error) {Record("diagnostic-write-or-sample-failed",errorType:error.GetType().Name);}
        }
        internal static string Category(Status status,DiagnosticState worker,long utcNow,string process) {
            if(status==null)return "status-missing";
            if(status.identity==null)return "legacy-status-identity-unavailable";
            if(process!="matched")return process;
            if(worker==null||worker.identity==null||worker.identity.session!=status.identity.session)return "session-evidence-missing";
            DateTimeOffset at;
            if(!DateTimeOffset.TryParse(worker.at,out at)||utcNow-at.ToUnixTimeMilliseconds()>15000)return "heartbeat-stale";
            if(worker.writeError!=null)return "status-write-failed";
            if(worker.phaseId!=null&&worker.phaseElapsedMs>45000)return "operation-waiting";
            if(!DateTimeOffset.TryParse(status.at,out at)||utcNow-at.ToUnixTimeMilliseconds()>45000)return "business-status-stale";
            return "healthy";
        }
        internal DiagnosticState ReadWorker(Status status) {
            if(status==null||status.identity==null||!ValidSession(status.identity.session))return null;
            var path=Path.Combine(store.Runtime,"diagnostics",status.identity.session,"health.json");
            return ReadBounded<DiagnosticState>(path);
        }
        internal void Observe() {
            Status status=null;DiagnosticState worker=null;string error=null,process="identity-unavailable",category;
            try {if(Directory.Exists(store.FilePath("status.json")))throw new IOException("Status path is a directory.");
                status=store.Read<Status>("status.json");worker=ReadWorker(status);process=RuntimeIdentity.Inspect(status==null?null:status.identity);category=Category(status,worker,now(),process);
                var reservation=store.Read<Status>("lock.json");
                if(reservation!=null&&reservation.identity!=null&&(status==null||status.identity==null||reservation.identity.session!=status.identity.session)) {
                    worker=ReadWorker(reservation);process=RuntimeIdentity.Inspect(reservation.identity);
                    category=worker!=null&&worker.phaseElapsedMs>45000?"operation-waiting":"reserved-session-awaiting-status";
                }
                if(!StatusBelongs(status))category="status-session-or-directory-mismatch";}
            catch(Exception failure) {error=failure.GetType().Name;category="status-read-failed";}
            lock(gate)state.peerSession=status==null||status.identity==null?null:status.identity.session;
            if(status!=null&&!status.running&&category=="process-exited")category="worker-stopped";
            if(category=="healthy"||category=="status-missing"||category=="worker-stopped") {lastIncident=null;return;}
            var key=category+":"+(status==null||status.identity==null?"legacy":status.identity.session);
            if(key==lastIncident)return;
            lastIncident=key;Capture(category,null,status,worker,process,error);
        }
        internal bool StatusBelongs(Status status) {
            if(status==null||status.identity==null)return true; // Legacy evidence is explicitly classified as unverifiable.
            if(status.identity.dataDirectory!=Identity.dataDirectory)return false;
            var owner=store.Read<Status>("lock.json");
            return owner==null||owner.identity==null||owner.identity.session==status.identity.session;
        }
        internal void ObserveSelf() {
            var current=Copy();string category=null;
            if(current.writeError!=null)category="status-write-failed";
            else if(current.phaseId!=null&&current.phaseElapsedMs>45000)category="operation-waiting";
            if(category==null) {lastIncident=null;return;}
            var key=category+":"+current.phaseId;
            if(lastIncident==key)return;lastIncident=key;
            Capture(category,worker:current,process:"self");
        }
        internal void Capture(string category,string commandId=null,Status status=null,DiagnosticState worker=null,string process=null,string error=null) {
            Record("fault-snapshot",id:commandId,result:category);
            var snapshot=new DiagnosticSnapshot {at=DateTimeOffset.UtcNow.ToString("o"),category=category,commandId=commandId,
                observer=Copy(),worker=worker,statusIdentity=status==null?null:status.identity,statusAt=status==null?null:status.at,statusReadError=error,observedProcess=process};
            try {
                Directory.CreateDirectory(directory);
                RuntimeStore.AtomicWrite(Path.Combine(directory,"fault-"+Guid.NewGuid().ToString("N")+".json"),snapshot);
                Prune();
            } catch(Exception failure) {Record("snapshot-write-failed",errorType:failure.GetType().Name);}
        }
        internal void CaptureControlTimeout(string id) {
            Status status=null;DiagnosticState worker=null;string error=null;
            try {status=store.Read<Status>("status.json");worker=ReadWorker(status);}catch(Exception failure){error=failure.GetType().Name;}
            Capture("control-timeout",id,status,worker,RuntimeIdentity.Inspect(status==null?null:status.identity),error);
        }
        static bool ValidSession(string value) {Guid id;return value!=null&&value.Length==32&&Guid.TryParseExact(value,"N",out id);}
        static bool SafeFile(string path) {return (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0;}
        internal static T ReadBounded<T>(string path) where T:class {
            if(!File.Exists(path))return null;
            if(!SafeFile(path)||new FileInfo(path).Length>MaxFileBytes)throw new IOException("Diagnostic file is unavailable or exceeds the size limit.");
            return Json.Decode<T>(RuntimeStore.ReadText(path));
        }
        IEnumerable<DirectoryInfo> Sessions() {
            var root=Path.Combine(store.Runtime,"diagnostics");
            if(!Directory.Exists(root)||!SafeFile(root))return new DirectoryInfo[0];
            return new DirectoryInfo(root).GetDirectories().Where(d=>ValidSession(d.Name)&&(d.Attributes&FileAttributes.ReparsePoint)==0).OrderByDescending(d=>d.LastWriteTimeUtc);
        }
        void Prune() {
            foreach(var session in Sessions()) {
                var files=session.GetFiles("fault-*.json").OrderByDescending(f=>f.LastWriteTimeUtc).ToArray();
                foreach(var file in files.Skip(MaxSnapshots))if(SafeFile(file.FullName))file.Delete();
            }
            // Never delete a live peer's session. Bound inactive history only.
            foreach(var session in Sessions().Skip(MaxSessions)) {
                var health=ReadBounded<DiagnosticState>(Path.Combine(session.FullName,"health.json"));
                if(health!=null&&!health.ended) {
                    var process=RuntimeIdentity.Inspect(health.identity);
                    if(process!="process-exited"&&process!="pid-reused")continue;
                }
                foreach(var file in session.GetFiles())if(SafeFile(file.FullName)&&(file.Name=="health.json"||file.Name.StartsWith("fault-",StringComparison.Ordinal)&&file.Extension==".json"))file.Delete();
                if(!session.EnumerateFileSystemInfos().Any())session.Delete();
            }
        }
        internal void Export(string output) {
            var manifest=new List<object>();int total=0;
            using(var stream=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create)) {
                Action<string,object> add=(name,value)=>{var bytes=Encoding.UTF8.GetBytes(Json.Encode(value));if(total+bytes.Length>MaxBundleBytes)throw new IOException("Diagnostic bundle limit exceeded.");total+=bytes.Length;using(var entry=zip.CreateEntry(name).Open())entry.Write(bytes,0,bytes.Length);};
                add("collector.json",Copy());
                try {
                    var status=store.Read<Status>("status.json");var worker=ReadWorker(status);
                    var process=RuntimeIdentity.Inspect(status==null?null:status.identity);
                    var category=StatusBelongs(status)?Category(status,worker,now(),process):"status-session-or-directory-mismatch";
                    add("assessment.json",new {observedAt=DateTimeOffset.UtcNow.ToString("o"),category=category,
                        workerSession=status==null||status.identity==null?null:status.identity.session,phase=worker==null?null:worker.phase,
                        phaseElapsedMs=worker==null?0:worker.phaseElapsedMs,observedProcess=process,
                        insufficientEvidence="This is an evidence category, not a root-cause verdict. Exit cause, internal call stacks, remote responses, missing pre-upgrade history and activity outside this instance may remain unknown."});
                }catch(Exception error) {add("assessment.json",new {category="status-or-health-unreadable",errorType=error.GetType().Name,insufficientEvidence="Current stage cannot be established from the readable files."});}
                DirectoryInfo[] sessions;
                try {sessions=Sessions().Take(MaxSessions).ToArray();}
                catch(Exception failure) {sessions=new DirectoryInfo[0];manifest.Add(new {file="sessions",missingReason=failure.GetType().Name});}
                foreach(var session in sessions) {
                    string[] files;
                    try {files=new[]{Path.Combine(session.FullName,"health.json")}.Concat(session.GetFiles("fault-*.json").OrderByDescending(f=>f.LastWriteTimeUtc).Take(MaxSnapshots).Select(f=>f.FullName)).ToArray();}
                    catch(Exception failure) {manifest.Add(new {file="sessions/"+session.Name,session=session.Name,missingReason=failure.GetType().Name});continue;}
                    foreach(var path in files) {
                        var name="sessions/"+session.Name+"/"+Path.GetFileName(path);string reason=null;
                        try {
                            object value=Path.GetFileName(path)=="health.json"?(object)ReadBounded<DiagnosticState>(path):ReadBounded<DiagnosticSnapshot>(path);
                            if(value==null)reason="missing";
                            else if(total+Encoding.UTF8.GetByteCount(Json.Encode(value))>MaxBundleBytes-MaxFileBytes)reason="bundle-size-limit";
                            else add(name,value);
                        }catch(Exception failure) {reason=failure.GetType().Name;}
                        manifest.Add(new {file=name,session=session.Name,missingReason=reason});
                    }
                }
                // Existing raw files are intentionally excluded: they contain account keys,
                // paths or payloads. The new events retain safe business error codes instead.
                add("manifest.json",new {schema=1,collectedAt=DateTimeOffset.UtcNow.ToString("o"),collectorSession=Identity.session,
                    instance=Identity.instance,dataDirectory=Identity.dataDirectory,files=manifest,sessionLimit=MaxSessions,snapshotLimit=MaxSnapshots,
                    excluded=new[]{"raw events/status/pending/config/debug startup/launcher files: outside credential-free allowlist","older sessions beyond retention; pre-upgrade diagnostic evidence unavailable"},
                    limits="No dumps, credentials, account state or personal paths. A missing heartbeat cannot establish the underlying cause. Power events/gaps are evidence, not proof of sleep causing a failure."});
            }
        }
        public void Dispose() {
            if(stopping)return;
            store.DiagnosticEvent-=logSink;try {SystemEvents.PowerModeChanged-=PowerChanged;}catch { }
            Record("session-end");lock(gate)state.ended=true;stopping=true;Signal();
            if(thread==null) {Tick();wake.Dispose();}
            else thread.Join(1000); // A blocked disk must not prevent application shutdown.
        }
    }
    internal sealed class DiagnosticClient : IAppClient {
        readonly IAppClient inner;readonly DiagnosticSession diagnostics;
        internal DiagnosticClient(IAppClient inner,DiagnosticSession diagnostics) {this.inner=inner;this.diagnostics=diagnostics;
            var app=inner as AppClient;diagnostics.Connection(inner.Connected,app==null?0:app.DiagnosticPort,app==null?"test-client":"validated-loopback-at-connect; peers are TCP observations, not a fresh ownership validation");
            if(app!=null)diagnostics.ConnectionEvidence(app);}
        public bool Connected {get{return inner.Connected;}}
        public Task<string> CheckVersion(){return diagnostics.Call("version",inner.CheckVersion);}
        public Task<Snapshot> Snapshot(){return diagnostics.Call("read-snapshot",inner.Snapshot);}
        public Task<ServerState> Server(){return diagnostics.Call("read-server",inner.Server);}
        public Task<Snapshot> Boost(int delta,Snapshot before){return diagnostics.Call("boost",()=>inner.Boost(delta,before));}
        public Task<Snapshot> Claim(string side,Snapshot before){return diagnostics.Call("claim-"+side,()=>inner.Claim(side,before));}
        public bool SupportsClaimPair {get{return inner.SupportsClaimPair;}}
        public Task<PairResult> ClaimPair(Journal journal,string command){return diagnostics.Call("claim-pair-"+command,()=>inner.ClaimPair(journal,command));}
        public Task<Snapshot> Recover(Journal journal){return diagnostics.Call("verify-"+journal.action.kind,()=>inner.Recover(journal));}
        public Task<int> RetryQuery(){return diagnostics.Call("retry-query",inner.RetryQuery);}
        public Task Quit(){return diagnostics.Call("quit",inner.Quit);}
        public void Dispose(){inner.Dispose();diagnostics.Connection(false);}
    }
}
