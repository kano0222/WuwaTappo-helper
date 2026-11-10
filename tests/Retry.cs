using System;
using System.IO;
using System.Threading.Tasks;

namespace Tappo {
    internal static class RetryChecks {
        public static async Task<int> Run() {
            int checks=0;
            var root=Path.Combine(Path.GetTempPath(),"tappo-retry-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                foreach(var scenario in new[]{"query","connect-pending","limited","retry-after","bridge-wait","failure","stale-mapping","incompatible","cached-mapping"}) {
                    var store=new RuntimeStore(Path.Combine(root,scenario));
                    var client=new FakeClient();client.State.left=client.State.right=900000;
                    bool pending=scenario=="connect-pending",sent=false;long now=100000,deadline=0;int loops=0,connections=0;
                    if(pending) {store.Write("pending.json",new {historical=true});client.State.loggedIn=false;}
                    store.Write("control.json",new Command {id="pause",action="pause"});
                    int snapshots=0;bool mappingReady=false;
                    string desktopVersion="1.0.1";int versionChecks=0,discoveries=0,staleReads=0;
                    if(scenario=="cached-mapping") {
                        var capabilities=new Capabilities((source,timeout)=> {
                            if(source.Contains("plugin:app|version")) {versionChecks++;return Task.FromResult<object>(desktopVersion);}
                            discoveries++;return Task.FromResult(Json.Decode<object>(Json.Encode(new {profile=new AppProfile {commonPath="/assets/common-"+desktopVersion+".js"}})));
                        },()=>now);
                        client.VersionResult=()=> {capabilities.Ensure(true).GetAwaiter().GetResult();return capabilities.Profile.version;};
                        client.SnapshotResult=()=> {
                            capabilities.Ensure().GetAwaiter().GetResult();snapshots++;
                            if(capabilities.Profile.version!=desktopVersion) {staleReads++;throw new InvalidOperationException("stale module mapping");}
                            client.State.appVersion=capabilities.Profile.version;return client.State;
                        };
                    }
                    if(scenario=="stale-mapping"||scenario=="incompatible") {
                        client.SnapshotResult=()=> {
                            snapshots++;
                            if(snapshots==1||scenario=="incompatible")throw new ConnectionFailureException("incompatible","page resources not ready");
                            mappingReady=true;return client.State;
                        };
                        client.RetryQueryResult=()=> {
                            if(!mappingReady)throw new InvalidOperationException("stale module mapping");
                            return 0;
                        };
                    }
                    client.ServerResult=()=> {
                        if(client.Reads==1&&scenario!="stale-mapping"||scenario=="failure") {
                            if(scenario=="limited"||scenario=="retry-after")throw new AppQueryException(new QueryReply {httpStatus=scenario=="limited"?429:503,retryAfterMs=120000,requiredRetryAfterMs=120000});
                            throw new IOException("offline");
                        }
                        return new ServerState {accountKey=client.State.accountKey,total=client.State.cumulative,consumed=client.State.consumed,left=900000,right=900000};
                    };
                    if(scenario=="bridge-wait")client.RetryQueryResult=()=>120000;
                    Worker worker=null;
                    worker=new Worker(store,()=> {
                        if(pending)throw new IOException("offline");
                        return Task.FromResult<IAppClient>(client);
                    },c=>{throw new Exception("retry restored pet");},ms=> {
                        now+=ms;loops++;
                        if(!sent&&worker.Status.readRetryAt.HasValue) {
                            deadline=worker.Status.readRetryAt.Value;sent=true;
                            if(scenario=="cached-mapping")desktopVersion="1.0.2";
                            store.Write("control.json",new Command {id="retry",action="retry"});
                        } else if(worker.Status.retryCommandId=="retry"||loops>10)worker.RequestDisconnect();
                        return Task.FromResult(0);
                    },()=>now,()=>{connections++;return Task.FromResult<IAppClient>(client);});
                    await worker.Run();
                    if(worker.Status.retryCommandId!="retry"||worker.Status.retrying||loops>10)throw new Exception(scenario+" retry not acknowledged");checks++;
                    if(client.Boosts!=0||client.Claims!=0||client.Quits!=0||!worker.Status.manuallyPaused)throw new Exception(scenario+" retry changed pause or submitted mutation");checks++;
                    if(scenario=="query"&&(client.Reads!=2||worker.Status.readRetryAt!=null||now>=deadline))throw new Exception("manual retry did not bypass countdown");
                    if(pending&&(!store.Exists("pending.json")||connections!=1||client.Recoveries!=0))throw new Exception("retry lost or replayed pending operation");
                    if((scenario=="limited"||scenario=="retry-after")&&(client.Reads!=1||client.QueryRetries!=0||worker.Status.readRetryAt!=deadline))throw new Exception("mandatory server wait bypassed");
                    if(scenario=="bridge-wait"&&(client.Reads!=1||!worker.Status.readRetryNotBefore.HasValue||worker.Status.readRetryNotBefore.Value<=now))throw new Exception("page mandatory wait bypassed");
                    if(scenario=="failure"&&(client.Reads!=2||worker.Status.readRetryCount!=2||worker.Status.fault!=null))throw new Exception("failed manual retry did not return to backoff");
                    if(scenario=="stale-mapping"&&(snapshots!=2||client.QueryRetries!=1||client.Reads!=1||worker.Status.readRetryAt.HasValue||now>=deadline))throw new Exception("manual retry did not refresh stale mapping before querying");
                    if(scenario=="incompatible"&&(snapshots!=2||client.QueryRetries!=0||client.Reads!=0||worker.Status.readRetryCount!=2||worker.Status.connectionFailure!="incompatible"))throw new Exception("failed capability validation allowed retry script");
                    if(scenario=="cached-mapping"&&(snapshots!=2||staleReads!=0||versionChecks!=2||discoveries!=2||client.State.appVersion!="1.0.2"||client.Reads!=2||worker.Status.readRetryAt.HasValue||now>=deadline))throw new Exception("manual retry reused mapping inside version cache interval");
                    checks++;
                }
                foreach(var scenario in new[]{"resume-query","resume-required-wait","resume-bridge-wait","resume-failure","resume-no-backoff"}) {
                    var store=new RuntimeStore(Path.Combine(root,scenario));var client=new FakeClient();
                    client.State.left=client.State.right=900000;
                    long now=100000,deadline=0,resumedAt=0;int loops=0;bool sent=false;long secondRead=0;
                    store.Write("control.json",new Command {id="pause",action="pause"});
                    client.ServerResult=()=> {
                        if(client.Reads==1&&scenario!="resume-no-backoff") {
                            if(scenario=="resume-required-wait")throw new AppQueryException(new QueryReply {httpStatus=503,retryAfterMs=120000,requiredRetryAfterMs=120000});
                            throw new IOException("offline");
                        }
                        secondRead=now;
                        if(scenario=="resume-failure")throw new IOException("still offline");
                        return new ServerState {accountKey="test-key",total=4000,consumed=2000,left=900000,right=900000};
                    };
                    if(scenario=="resume-bridge-wait")client.RetryQueryResult=()=>120000;
                    Worker worker=null;
                    worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>{throw new Exception("resume closed the pet");},ms=> {
                        now+=ms;loops++;
                        if(!sent&&(worker.Status.readRetryAt.HasValue||scenario=="resume-no-backoff")) {
                            sent=true;deadline=worker.Status.readRetryAt??now;resumedAt=now;
                            store.Write("control.json",new Command {id="resume",action="resume"});
                        } else if(client.Reads>=2||loops>140)worker.RequestDisconnect();
                        return Task.FromResult(0);
                    },()=>now);
                    await worker.Run();
                    if(!sent||loops>140||client.Reads!=2||worker.Status.manuallyPaused||client.Claims!=0||client.Boosts!=0||client.Quits!=0)throw new Exception(scenario+" did not safely resume and recheck");checks++;
                    if(scenario=="resume-query"&&(secondRead>=deadline||client.QueryRetries!=1||worker.Status.readRetryAt.HasValue))throw new Exception("resume did not bypass ordinary backoff");
                    if(scenario=="resume-required-wait"&&(secondRead<deadline||client.QueryRetries!=0))throw new Exception("resume bypassed a required server wait");
                    if(scenario=="resume-bridge-wait"&&(secondRead<resumedAt+120000||client.QueryRetries!=1))throw new Exception("resume bypassed the page query wait");
                    if(scenario=="resume-failure"&&(worker.Status.readRetryCount!=2||!worker.Status.readRetryAt.HasValue))throw new Exception("failed resume did not return to automatic backoff");
                    if(scenario=="resume-no-backoff"&&client.QueryRetries!=1)throw new Exception("resume reused cached state without rechecking");checks++;
                    if(scenario!="resume-failure"&&(worker.Status.readRetryAt.HasValue||worker.Status.readRetryNotBefore.HasValue||worker.Status.readRetryCount!=0||worker.Status.readRetryError!=null||worker.Status.readRetryStage!=null))throw new Exception(scenario+" successful recheck left a false retry status");checks++;
                }
                foreach(var scenario in new[]{"automatic-required-wait","pending-verification"}) {
                    var store=new RuntimeStore(Path.Combine(root,scenario));var client=new FakeClient();
                    client.State.left=client.State.right=900000;long now=100000,deadline=0;int loops=0;bool sent=false,earlyRequest=false;
                    store.Write("control.json",new Command {id="pause",action="pause"});
                    if(scenario=="pending-verification") {
                        store.Begin(new Plan {kind="claim",side="left"},Tests.Copy(client.State),new ServerState {accountKey="test-key",total=4000,consumed=2000,left=900000,right=900000});
                        client.RecoveryResult=journal=> {
                            if(client.Recoveries>1&&now<deadline)earlyRequest=true;
                            return new Snapshot {accountKey=journal.before.accountKey,operationId=journal.id,outcome="pending"};
                        };
                    } else client.ServerResult=()=> {
                        if(client.Reads==1)throw new AppQueryException(new QueryReply {httpStatus=503,retryAfterMs=120000,requiredRetryAfterMs=120000});
                        if(now<deadline)earlyRequest=true;
                        return new ServerState {accountKey="test-key",total=4000,consumed=2000,left=900000,right=900000};
                    };
                    Worker worker=null;
                    worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=> {
                        if(!sent&&worker.Status.readRetryAt.HasValue) {
                            sent=true;deadline=worker.Status.readRetryAt.Value;
                            if(scenario=="pending-verification")store.Write("control.json",new Command {id="retry",action="retry"});
                            else if(!worker.Status.readRetryNotBefore.HasValue||worker.Status.readRetryNotBefore.Value>deadline)throw new Exception("required wait exceeds automatic retry deadline");
                        }
                        now+=ms;loops++;
                        if(client.Reads==2||client.Recoveries==2||loops>130)worker.RequestDisconnect();
                        return Task.FromResult(0);
                    },()=>now);
                    await worker.Run();
                    if(worker.Status.fault!=null||!sent||earlyRequest||loops>130||client.Boosts!=0||client.Claims!=0)throw new Exception(scenario+" failed");checks++;
                    if(scenario=="automatic-required-wait"&&(client.Reads!=2||now<deadline))throw new Exception("automatic required wait not observed");
                    if(scenario=="pending-verification"&&(client.Recoveries!=2||!store.Exists("pending.json")||worker.Status.retryCommandId!="retry"||now<deadline))throw new Exception("queued retry skipped pending wait");checks++;
                }
                {
                    var store=new RuntimeStore(Path.Combine(root,"manual-activity"));
                    var client=new FakeClient();client.State.left=client.State.right=900000;
                    long now=100000;int stage=0;Worker worker=null;
                    store.Write("control.json",new Command {id="pause",action="pause"});
                    worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=> {
                        now+=ms;
                        if(stage==0)store.Write("control.json",new Command {id="repeat-pause",action="pause"});
                        else if(stage==1)store.Write("control.json",new Command {id="resume",action="resume"});
                        else worker.RequestDisconnect();
                        stage++;return Task.FromResult(0);
                    },()=>now);
                    await worker.Run();
                    if(worker.Status.mode!="clicker")throw new Exception("worker without preferences does not default to clicker mode");checks++;
                    var rows=File.ReadAllLines(store.FilePath("events.jsonl"));int paused=0,resumed=0;
                    foreach(var row in rows) {
                        if(row.Contains("\"event\":\"manual-paused\""))paused++;
                        if(row.Contains("\"event\":\"manual-resumed\""))resumed++;
                    }
                    if(paused!=1||resumed!=1)throw new Exception("manual pause/resume records missing or duplicated");checks++;
                    if(worker.Status.fault!=null||client.Claims!=0||client.Boosts!=0||client.Quits!=0)throw new Exception("manual activity logging changed automation behavior");checks++;
                }
                {
                    var store=new RuntimeStore(Path.Combine(root,"diagnostic-retry"));var client=new FakeClient();client.State.left=client.State.right=900000;
                    client.ServerResult=()=>{throw new AppQueryException(new QueryReply {httpStatus=503,code=70006,errorType="test-query-error"});};
                    long now=100000;Worker worker=null;
                    worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=>{now+=ms;worker.RequestDisconnect();return Task.FromResult(0);},()=>now);
                    await worker.Run();var log=File.ReadAllText(store.FilePath("events.jsonl"));
                    if(!log.Contains("503")||!log.Contains("70006")||!log.Contains("AppQueryException")||!log.Contains("服务器状态查询失败"))throw new Exception("query retry lost its diagnostic cause");checks++;
                    if(client.Claims!=0||client.Boosts!=0||client.Quits!=0)throw new Exception("retry diagnostics mutated the pet");checks++;
                }
            } finally {Directory.Delete(root,true);}
            return checks;
        }
    }
}
