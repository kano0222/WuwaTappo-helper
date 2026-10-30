using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Tappo {
    internal static class RecoveryChecks {
        static int checks;
        static void Check(bool value,string message) {if(!value)throw new Exception(message);checks++;}
        static ServerState Server(Snapshot s) {return new ServerState {accountKey=s.accountKey,total=s.cumulative,consumed=s.consumed,left=s.left,right=s.right};}
        static Snapshot Confirm(FakeClient client,Journal journal) {
            var after=Tests.Copy(journal.before);after.operationId=journal.id;after.outcome="confirmed";after.consumed+=1000;after.available-=1000;
            after.rewardVerified=true;after.reward=new RewardReceipt {assetId=123,assetName="test",assetType="accessory",quantityBefore=0,quantityAfter=1};
            if(journal.action.side=="left")after.left=900000;else after.right=900000;
            after.confirmedServer=Server(after);client.State=Tests.Copy(after);return after;
        }
        public static async Task<int> Run() {
            checks=0;var root=Path.Combine(Path.GetTempPath(),"tappo-recovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try {
                foreach(var scenario in new[]{"query","post-query","save","disconnect","manual-pause","account","slot","wrong-id","receipt-lost","restart","startup-disconnect"}) {
                    var store=new RuntimeStore(Path.Combine(root,scenario));var old=new FakeClient();var fresh=new FakeClient();long now=100000;int loops=0,reconnections=0;
                    old.State.left=4000;old.State.appVersion="1.0.2";fresh.State=Tests.Copy(old.State);
                    var restarted=scenario=="restart"||scenario=="startup-disconnect";
                    if(restarted)store.Begin(new Plan {kind="claim",side="left"},Tests.Copy(old.State),Server(old.State));
                    old.ClaimResult=()=> {
                        if(scenario=="disconnect") {old.Connected=false;throw new IOException("response lost");}
                        if(scenario=="post-query") {var result=Tests.Copy(old.State);result.left=900000;result.consumed+=1000;result.available-=1000;return result;}
                        var pending=Tests.Copy(old.State);pending.outcome="pending";pending.failureStage="request";return pending;
                    };
                    if(scenario=="post-query")old.ServerResult=()=>{if(old.Reads==2)throw new IOException("query failed after claim");return Server(old.State);};
                    Func<FakeClient,Journal,Snapshot> recover=(client,journal)=> {
                        if(scenario=="query"&&client.Recoveries==1)throw new AppQueryException(new QueryReply {httpStatus=503,retryAfterMs=8000,errorType="Error"});
                        if(scenario=="save"&&client.Recoveries==1)return new Snapshot {accountKey="test-key",operationId=journal.id,outcome="pending",failureStage="local-save",errorType="Error"};
                        if(scenario=="receipt-lost")return new Snapshot {accountKey="test-key",operationId=journal.id,outcome="unknown",failureStage="receipt-missing"};
                        var after=Confirm(client,journal);
                        if(scenario=="slot"){after.right=950000;after.confirmedServer.right=950000;}
                        if(scenario=="wrong-id")after.operationId="unrelated-operation";
                        return after;
                    };
                    old.RecoveryResult=j=>recover(old,j);fresh.RecoveryResult=j=>recover(fresh,j);
                    Worker worker=null;
                    worker=new Worker(store,()=> {
                        if(scenario=="startup-disconnect")throw new IOException("initial connection failed");
                        return Task.FromResult<IAppClient>(old);
                    },c=>{throw new Exception("must not restart the pet");},ms=> {
                        loops++;now+=Math.Max(ms,10000);
                        if(loops==1&&scenario=="manual-pause")store.Write("control.json",new Command {id="pause",action="pause"});
                        if(loops==1&&scenario=="account")old.State.accountKey="another-key";
                        if(worker.Status.claimsThisRun==1||new[]{"account","slot","wrong-id","receipt-lost"}.Contains(scenario)&&loops>=2||loops>25)worker.RequestDisconnect();
                        return Task.FromResult(0);
                    },()=>now,()=>{reconnections++;return Task.FromResult<IAppClient>(fresh);});
                    await worker.Run();
                    var blocked=new[]{"account","slot","wrong-id","receipt-lost"}.Contains(scenario);
                    Check(loops<=25,scenario+" recovery stalled");
                    Check(old.Claims==(restarted?0:1)&&fresh.Claims==0,scenario+" mutation resent during recovery");
                    Check(store.Exists("pending.json")==blocked,scenario+" pending record ownership");
                    Check(worker.Status.claimsThisRun==(blocked?0:1),scenario+" confirmation attribution");
                    if(scenario=="account")Check(old.Recoveries==0,"recovery ran for another account");
                    if(scenario=="manual-pause")Check(worker.Status.paused&&worker.Status.manuallyPaused,"automatic recovery overrode manual pause");
                    if(scenario=="disconnect"||scenario=="startup-disconnect")Check(reconnections==1,"pending prevented reconnect");
                    if(scenario=="query"||scenario=="save")Check(old.Recoveries==2,"transient recovery stage did not retry");
                }
                foreach(var pending in new[]{false,true}) {
                    var store=new RuntimeStore(Path.Combine(root,"startup-fatal-"+pending));var client=new FakeClient();int persists=0,reconnections=0,waits=0;
                    if(pending)store.Begin(new Plan {kind="claim",side="left"},Tests.Copy(client.State),Server(client.State));
                    StartupConfigurationException failure=null;
                    var worker=new Worker(store,()=> {
                        try {Platform.SaveStartupRecord(()=>{persists++;throw new IOException("record failed");});}
                        catch(StartupConfigurationException error) {failure=error;throw;}
                        return Task.FromResult<IAppClient>(client);
                    },c=>{throw new Exception("fatal startup must not restart the pet");},ms=>{waits++;return Task.FromResult(0);},()=>100000,
                    ()=>{reconnections++;return Task.FromResult<IAppClient>(client);});
                    var exit=await worker.Run();
                    Check(exit==1&&!worker.Status.running&&worker.Status.fault==failure.Message,"startup configuration failure did not stop worker");
                    Check(persists==1&&waits==0&&reconnections==0,"startup configuration failure entered retry");
                    Check(client.Boosts==0&&client.Claims==0&&client.Reads==0,"fatal startup allowed account access");
                    Check(store.Exists("pending.json")==pending,"fatal startup changed pending record");
                    Check(failure.InnerException is IOException,"startup failure lost original cause");
                }
                {
                    int callbacks=0;Platform.SaveStartupRecord(()=>callbacks++);Check(callbacks==1,"successful startup record failed");
                }
                {
                    var store=new RuntimeStore(Path.Combine(root,"startup-without-pending"));var client=new FakeClient();long now=100000;int attempts=0,loops=0;
                    Worker worker=null;worker=new Worker(store,()=>{throw new IOException("not ready");},c=>Task.FromResult(0),ms=> {
                        now+=ms;if(++loops>40||attempts>=3&&worker.Status.state!=null)worker.RequestDisconnect();return Task.FromResult(0);
                    },()=>now,()=>{attempts++;if(attempts<3)throw new IOException("still not ready");return Task.FromResult<IAppClient>(client);});
                    await worker.Run();Check(attempts==3&&worker.Status.state!=null,"initial connection failure did not keep retrying without pending");
                    Check(!store.Exists("pending.json"),"startup retry created a mutation record");
                }
                foreach(var scenario in new[]{"boost-disconnect","boost-delayed","boost-account","boost-wrong-id","boost-restart","boost-pause","boost-overwritten"}) {
                    var store=new RuntimeStore(Path.Combine(root,scenario));store.Write("preferences.json",new Preference {mode="clicker"});
                    var old=new FakeClient();old.State.available=0;old.State.cumulative=2000;old.State.left=old.State.right=900000;
                    var fresh=new FakeClient();fresh.State=Tests.Copy(old.State);long now=100000;int loops=0,recoveries=0;
                    if(scenario=="boost-restart")store.Begin(new Plan {kind="boost",delta=100},Tests.Copy(old.State),Server(old.State));
                    old.BoostResult=(delta,before)=>{old.Connected=false;throw new IOException("event response lost");};
                    fresh.RecoveryResult=journal=> {
                        recoveries++;
                        var after=Tests.Copy(journal.before);after.operationId=journal.id;after.outcome="confirmed";after.cumulative+=journal.action.delta;after.available+=journal.action.delta;
                        if(scenario=="boost-overwritten") {
                            after.boostOverwritten=true;after.boostAppliedTotal=after.cumulative;after.boostReportTotal=journal.before.cumulative;
                            after.cumulative=journal.before.cumulative;after.available=journal.before.available;after.serverTotal=after.cumulative;after.local=0;
                        }
                        if(scenario=="boost-delayed"&&recoveries==1)after.outcome="pending";
                        if(scenario=="boost-account")after.accountKey="other";
                        if(scenario=="boost-wrong-id")after.operationId="other";
                        return after;
                    };
                    old.RecoveryResult=fresh.RecoveryResult;
                    Worker worker=null;worker=new Worker(store,()=>Task.FromResult<IAppClient>(old),c=>Task.FromResult(0),ms=> {
                        now+=Math.Max(ms,10000);loops++;
                        if(loops==1&&scenario=="boost-pause")store.Write("control.json",new Command {id="pause",action="pause"});
                        if(recoveries>0&&!store.Exists("pending.json")||loops>=6)worker.RequestDisconnect();return Task.FromResult(0);
                    },()=>now,()=>Task.FromResult<IAppClient>(fresh));
                    await worker.Run();var blocked=scenario=="boost-account"||scenario=="boost-wrong-id";
                    Check(old.Boosts==(scenario=="boost-restart"?0:1)&&fresh.Boosts==0,scenario+" boost was resent");
                    Check(store.Exists("pending.json")==blocked,scenario+" receipt ownership was not preserved");
                    Check(recoveries>0,scenario+" boost was never checked");
                    if(!blocked) {
                        var events=RuntimeStore.ReadText(store.FilePath("events.jsonl"));
                        Check(events.Contains("\"event\":\""+(scenario=="boost-overwritten"?"boost-overwritten":"boost-confirmed")+"\"")&&events.Contains("\"recovered\":true"),scenario+" boost activity missing or misclassified");
                        Check(!events.Contains("\"event\":\"boost-recovered\""),"new boost used unclassified legacy event");
                    }
                    if(scenario=="boost-delayed")Check(recoveries==2,"pending boost did not recover later");
                    if(scenario=="boost-pause")Check(worker.Status.manuallyPaused,"boost recovery overrode manual pause");
                }
                {
                    var store=new RuntimeStore(Path.Combine(root,"cache"));store.Write("preferences.json",new Preference {mode="clicker"});
                    var client=new FakeClient();client.State.available=0;client.State.left=client.State.right=900000;long now=100000;
                    Worker worker=null;worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=> {
                        now+=ms;if(client.Boosts>=20)worker.RequestDisconnect();return Task.FromResult(0);
                    },()=>now);
                    await worker.Run();Check(client.Boosts==20,"cache blocked boost progress");Check(client.Reads<=5,"server queried for each boost batch");
                }
                {
                    var store=new RuntimeStore(Path.Combine(root,"limited"));var client=new FakeClient();long now=100000;
                    client.ServerResult=()=>{throw new AppQueryException(new QueryReply {httpStatus=429,retryAfterMs=120000,errorType="Error"});};
                    Worker worker=null;worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=>{now+=ms;worker.RequestDisconnect();return Task.FromResult(0);},()=>now);
                    await worker.Run();Check(worker.Status.readRetryAt>=219000,"Retry-After ignored");Check(client.Claims==0&&client.Boosts==0,"rate limit allowed mutation");
                    Check(RuntimeStore.ReadText(store.FilePath("events.jsonl")).Contains("\"httpStatus\":429"),"rate limit diagnostic missing");
                }
                {
                    var store=new RuntimeStore(Path.Combine(root,"cooldown"));var client=new FakeClient();client.State.left=4000;long now=100000;int loops=0;
                    client.ClaimResult=()=> {
                        var after=Tests.Copy(client.State);
                        if(client.Claims<=2){after.rejected=true;after.code=70006;}
                        else {after.rejected=false;after.code=0;after.left=900000;after.consumed+=1000;after.available-=1000;}
                        return after;
                    };
                    Worker worker=null;worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>Task.FromResult(0),ms=> {
                        now+=10000;if(worker.Status.claimsThisRun==1||++loops>10)worker.RequestDisconnect();return Task.FromResult(0);
                    },()=>now);
                    await worker.Run();
                    var attempts=RuntimeStore.ReadText(store.FilePath("events.jsonl")).Split('\n').Where(l=>l.Contains("\"stage\":\"rejected\"")).Select(l=>Json.Decode<System.Collections.Generic.Dictionary<string,object>>(l)).ToArray();
                    Check(client.Claims==3&&worker.Status.claimsThisRun==1,"cooldown did not recover automatically");
                    Check(attempts.Length==2&&Convert.ToInt32(attempts[0]["backoffSeconds"])==5&&Convert.ToInt32(attempts[1]["backoffSeconds"])==10,"cooldown rejection backoff reset each attempt");
                    Check(!store.Exists("pending.json"),"confirmed cooldown recovery retained pending record");
                }
                return checks;
            } finally {
                var target=Path.GetFullPath(root);
                if(target.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(target).StartsWith("tappo-recovery-"))Directory.Delete(target,true);
            }
        }
    }
}
