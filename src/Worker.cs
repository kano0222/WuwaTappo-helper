using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tappo {
    internal sealed class ReadQueryException : Exception {
        public readonly string Operation;
        public ReadQueryException(string operation,Exception inner) : base(operation+"失败",inner) {Operation=operation;}
    }
    internal sealed class Worker {
        readonly RuntimeStore store; readonly Func<Task<IAppClient>> connect,reconnect;
        readonly Func<IAppClient,Task> restore; readonly Func<int,Task> delay; readonly Func<long> now;
        readonly Random random=new Random(); readonly ChestClock clock;
        public Status Status=new Status {running=true,mode="brute"};
        bool stop,restoreRequested,paused; string lastCommand,lastAppVersion;
        IAppClient client; long nextBoost,serverCacheUntil; int claimRejections; ServerState serverCache; string lastOperationState;
        public Worker(RuntimeStore store,Func<Task<IAppClient>> connect,Func<IAppClient,Task> restore,Func<int,Task> delay,Func<long> now,Func<Task<IAppClient>> reconnect=null) {
            this.store=store;this.connect=connect;this.reconnect=reconnect??connect;this.restore=restore;this.delay=delay;this.now=now;clock=new ChestClock(now);
        }
        public void RequestDisconnect() { stop=true; }
        void Publish(string message) {
            Status.connected=client!=null&&client.Connected;
            if(Status.connected==false&&!Status.disconnectedSince.HasValue) Status.disconnectedSince=now();
            Status.at=DateTimeOffset.UtcNow.ToString("o"); Status.pid=Process.GetCurrentProcess().Id;
            Status.manuallyPaused=paused;
            Status.paused=paused||Status.fault!=null||store.Exists("pending.json"); Status.message=Status.fault??message; Status.chestTimes=clock.Value;
            store.Write("status.json",Status);
        }
        async Task<Snapshot> ReadSnapshot() {
            try {return await client.Snapshot().ConfigureAwait(false);}
            catch(Exception error) {throw new ReadQueryException("读取桌宠状态",error);}
        }
        async Task<ServerState> ReadServer(bool fresh=false) {
            if(!fresh&&serverCache!=null&&now()<serverCacheUntil&&Status.state!=null&&serverCache.accountKey==Status.state.accountKey)return serverCache;
            ServerState result;
            try {result=await client.Server().ConfigureAwait(false);}
            catch(Exception error) {throw new ReadQueryException("读取服务器状态",error);}
            result.Validate();serverCache=result;serverCacheUntil=now()+5000;clock.Accept(result);return result;
        }
        void ResetRetry() {Status.readRetryAt=null;Status.readRetryNotBefore=null;Status.readRetryCount=0;Status.readRetryError=null;Status.readRetryStage=null;}
        void ConnectionFailed(Exception error) {
            Status.connectionFailure=Connection.Classify(error);
            if(!Status.disconnectedSince.HasValue) Status.disconnectedSince=now();
        }
        void Retry(string operation,string stage,string errorType,int code=0,int httpStatus=0,int minimumMs=0,int requiredMs=-1) {
            Status.readRetryCount++;
            Status.readRetryStage=stage;
            var seconds=Math.Min(60,5*(1<<Math.Min(4,Status.readRetryCount-1)));
            var jitter=random.Next(0,1001);var milliseconds=seconds==60?60000-jitter:seconds*1000+jitter;
            milliseconds=Math.Max(milliseconds,Math.Max(minimumMs,(httpStatus==403||httpStatus==429)?60000:0));
            Status.readRetryAt=now()+milliseconds;Status.readRetryError=operation+"，等待自动重试";
            Status.readRetryNotBefore=now()+Math.Max(requiredMs<0?minimumMs:requiredMs,(httpStatus==403||httpStatus==429)?60000:0);
            store.Log("read-query-retry",new {operation=operation,stage=stage,errorType=errorType,connectionFailure=Status.connectionFailure,code=code,httpStatus=httpStatus,attempt=Status.readRetryCount,backoffSeconds=seconds,retryAfterSeconds=(milliseconds+999)/1000,retryDelayMs=milliseconds});
            Publish(Status.readRetryError);
        }
        void BoundAccount(Journal journal,Snapshot after,ServerState server) {
            if(journal.before==null||string.IsNullOrEmpty(journal.before.accountKey)||journal.server==null||
               journal.server.accountKey!=journal.before.accountKey||after.accountKey!=journal.before.accountKey||server.accountKey!=journal.before.accountKey)
                throw new InvalidOperationException("账号已变化或记录缺少账号信息，保留记录等待核对。");
        }
        async Task AcceptClaim(Journal journal,Snapshot after,bool recovery) {
            var action=journal.action;var before=journal.before;
            if((after.outcome=="confirmed"||after.outcome==null&&!after.skipped&&!after.rejected)&&
               (!after.rewardVerified||after.reward==null||after.reward.assetId<=0||after.reward.quantityAfter<=after.reward.quantityBefore)) {
                after.outcome="unknown";after.failureStage="reward-receipt";
            }
            if(after.operationId!=journal.id) {after=new Snapshot {outcome="unknown",failureStage="receipt-missing"};}
            var key=journal.id+":"+after.outcome+":"+after.failureStage+":"+after.code+":"+after.httpStatus;
            if(lastOperationState!=key&&after.outcome!=null) {
                store.Log("operation-state",new {operationId=journal.id,side=action.side,outcome=after.outcome,stage=after.failureStage,errorType=after.errorType,code=after.code,httpStatus=after.httpStatus});lastOperationState=key;
            }
            if(after.skipped||after.outcome=="skipped") {
                if(string.IsNullOrEmpty(before.accountKey)||after.accountKey!=before.accountKey)throw new InvalidOperationException("账号已变化，保留操作记录。");
                if(after.confirmedServer!=null&&after.confirmedServer.accountKey==before.accountKey) {
                    after.confirmedServer.Validate();serverCache=after.confirmedServer;serverCacheUntil=now()+5000;clock.Accept(serverCache);
                }
                store.Log("skipped",new {reason=after.reason});store.ClearPending();Status.recovering=false;Status.fault=null;ResetRetry();Publish("领取条件已变化，已重新同步");return;
            }
            if(after.rejected||after.outcome=="rejected") {
                var serverAfter=after.confirmedServer??await ReadServer(true).ConfigureAwait(false);
                BoundAccount(journal,after,serverAfter);
                Policy.ConfirmRejected(after.code,before,after,journal.server,serverAfter);
                store.Log("claim-rejected",new {side=action.side,code=after.code,operationId=journal.id,before=before,after=after,serverBefore=journal.server,serverAfter=serverAfter});
                store.ClearPending();Status.state=after;Status.fault=null;Status.recovering=false;ResetRetry();
                claimRejections++;Status.readRetryCount=claimRejections-1;
                Retry("服务器暂未允许领取，稍后重新同步","rejected","BusinessError",after.code,0,after.code==70006?5000:10000);return;
            }
            if((after.outcome=="confirmed"||(!recovery&&after.outcome==null))&&after.rewardVerified&&after.reward!=null&&after.reward.assetId>0&&after.reward.quantityAfter>after.reward.quantityBefore) {
                var serverAfter=after.confirmedServer??await ReadServer(true).ConfigureAwait(false);
                BoundAccount(journal,after,serverAfter);
                Policy.Confirm(action.side,before,after,journal.server,serverAfter);
                store.Log("claim-confirmed",new {side=action.side,operationId=journal.id,recovered=recovery,before=before,after=after,serverBefore=journal.server,serverAfter=serverAfter});
                store.ClearPending();Status.state=after;Status.claimsThisRun++;Status.fault=null;Status.recovering=false;claimRejections=0;ResetRetry();
                serverCache=serverAfter;serverCacheUntil=now()+5000;clock.Accept(serverAfter);Publish("领取已确认");return;
            }
            Status.recovering=true;Status.recoveryStage=after.failureStage;
            if(after.outcome=="unknown"||after.outcome=="confirmed")Status.fault="奖励到账尚未确认，已保留记录并自动核对；可核对桌宠后点击“确认并继续”。";
            else Status.fault=null;
            if(after.errorType!=null||after.outcome=="unknown")Retry("核对领取结果",after.failureStage,after.errorType??"Unverifiable",after.code,after.httpStatus,Math.Max(after.retryAfterMs,after.outcome=="unknown"?30000:0));
            else {Status.readRetryAt=now()+1000;Status.readRetryError="正在核对领取结果";Publish("正在核对领取结果");}
        }
        async Task RecoverPending() {
            var journal=store.Read<Journal>("pending.json");
            Status.recovering=true;
            if(journal!=null&&journal.action!=null&&journal.action.kind=="boost"&&journal.before!=null&&!string.IsNullOrEmpty(journal.id)&&!string.IsNullOrEmpty(journal.before.accountKey)) {
                Snapshot observed;
                try {observed=await client.Recover(journal).ConfigureAwait(false);}
                catch(Exception error) {throw new ReadQueryException("核对补数结果",error);}
                await AcceptBoost(journal,observed,true).ConfigureAwait(false);return;
            }
            if(journal==null||journal.action==null||journal.action.kind!="claim"||journal.before==null||string.IsNullOrEmpty(journal.before.accountKey)||string.IsNullOrEmpty(journal.id)) {
                await ReadServer().ConfigureAwait(false);Status.fault=PendingMessage();Retry("核对历史操作","legacy-journal","Unverifiable",0,0,30000);return;
            }
            if(journal.before.accountKey!=Status.state.accountKey) {
                Status.fault="当前账号与待确认操作不一致，已保留记录。";Retry("等待原账号恢复","account","AccountChanged",0,0,30000);return;
            }
            Snapshot after;
            try {after=await client.Recover(journal).ConfigureAwait(false);}
            catch(Exception error) {throw new ReadQueryException("核对领取结果",error);}
            await AcceptClaim(journal,after,true).ConfigureAwait(false);
        }
        Task AcceptBoost(Journal journal,Snapshot after,bool recovery=false) {
            if(after.operationId==journal.id&&after.accountKey==journal.before.accountKey&&after.outcome=="skipped") {
                store.Log("skipped",new {operationId=journal.id,reason=after.reason});store.ClearPending();Status.fault=null;Status.recovering=false;ResetRetry();Publish("补数条件已变化");
            } else if(after.operationId==journal.id&&after.accountKey==journal.before.accountKey&&after.outcome=="confirmed"&&
               (Policy.BoostConfirmed(journal.action.delta,journal.before,after)||Policy.BoostOverwritten(journal.action.delta,journal.before,after))) {
                var overwritten=Policy.BoostOverwritten(journal.action.delta,journal.before,after);
                store.Log(overwritten?"boost-overwritten":"boost-confirmed",new {operationId=journal.id,mode=Status.mode,recovered=recovery,delta=journal.action.delta,before=journal.before,after=after});
                store.ClearPending();Status.state=after;Status.recovering=false;Status.fault=null;ResetRetry();Publish(overwritten?"本次补数被旧上报覆盖，按最新计数继续":"次数补充已确认");
            } else {
                Status.recovering=true;Status.recoveryStage=after.failureStage??"boost-receipt";
                Status.fault=after.outcome=="pending"?null:"补充 "+journal.action.delta+" 次的结果尚无法归属，已保留记录；核对桌宠后点击“确认并继续”。";
                Retry("核对补数结果",Status.recoveryStage,after.errorType??"Unverifiable",0,0,after.outcome=="pending"?1000:30000);
            }
            return Task.FromResult(0);
        }
        async Task<string> ReadVersion() {
            try {return await client.CheckVersion().ConfigureAwait(false);}
            catch(Exception error) {throw new ReadQueryException("校验桌宠版本",error);}
        }
        async Task<Snapshot> ObserveBoost(int delta,Snapshot before,Snapshot after) {
            if(Policy.BoostOverwritten(delta,before,after)) return after;
            // The event is sent once. Only read snapshots while its result propagates.
            int reads=0;
            while(!Policy.BoostConfirmed(delta,before,after)&&Policy.BoostContextMatches(before,after)&&reads<12) {
                await delay(250).ConfigureAwait(false);
                after=await client.Snapshot().ConfigureAwait(false);reads++;
            }
            if(!Policy.BoostConfirmed(delta,before,after)) {
                store.Log("boost-unconfirmed",new {delta=delta,before=before,after=after,confirmationReads=reads});
                throw new InvalidOperationException("补充 "+delta+" 次的结果未确认，已暂停；核对桌宠后点击“确认并继续”。");
            }
            return after;
        }
        string PendingMessage() {
            var journal=store.Read<Journal>("pending.json");DateTimeOffset at;
            var when=journal!=null&&DateTimeOffset.TryParse(journal.at,out at)?at.ToLocalTime().ToString("MM-dd HH:mm:ss"):"此前";
            var action=journal==null?null:journal.action;
            var description=action==null?"操作":action.kind=="boost"?"补充 "+action.delta+" 次":action.kind=="claim"?"领取"+(action.side=="left"?"宝箱一":"宝箱二"):"操作";
            return when+" 的"+description+"结果未确认，已暂停；核对桌宠后点击“确认并继续”。";
        }
        public async Task<int> Run() {
            // This guard serializes native workers; lock.json also blocks an older Node worker.
            using(var guard=new FileStream(store.FilePath("worker.guard"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                var old=store.Read<Status>("lock.json");
                if(old!=null&&RuntimeStore.Alive(old.pid)) throw new InvalidOperationException("自动化已运行，请先暂停或停止现有后台。");
                if(store.Exists("lock.json")) File.Delete(store.FilePath("lock.json"));
                using(var reservation=new FileStream(store.FilePath("lock.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)) {
                    var bytes=System.Text.Encoding.UTF8.GetBytes(Json.Encode(new {pid=Process.GetCurrentProcess().Id}));reservation.Write(bytes,0,bytes.Length);reservation.Flush(true);
                }
                try {
                    try {client=await connect().ConfigureAwait(false);store.Log("started",new {mode="background"});}
                    catch(StartupConfigurationException) {throw;}
                    catch(Exception error) {ConnectionFailed(error);Retry("重新连接桌宠","connect",error.GetType().Name);}
                    if(store.Exists("pending.json")) Status.fault=PendingMessage();
                    Publish(client!=null?"已连接":"正在等待桌宠重新连接");
                    while(!stop) {
                        int loopDelay=1000;
                        string retryCommand=null;
                        var command=store.Read<Command>("control.json");
                        if(command!=null&&command.id!=lastCommand) {
                            lastCommand=command.id;
                            if(command.action=="stop"||command.action=="stop-background"||command.action=="disconnect") { stop=true;restoreRequested=command.action=="stop"&&client!=null&&client.Connected; }
                            else if(command.action=="pause") paused=true;
                            else if(command.action=="resume"&&!store.Exists("pending.json")) { paused=false;Status.fault=null; }
                            else if(command.action=="retry") {
                                if(Status.readRetryAt.HasValue&&(!Status.readRetryNotBefore.HasValue||now()>=Status.readRetryNotBefore.Value)) retryCommand=command.id;
                                else Status.retryCommandId=command.id;
                            }
                            else if(command.action=="reset") {
                                Status.resetCommandId=command.id;Status.resetError=null;
                                try {
                                    if(store.Exists("pending.json")) {
                                        if(Status.fault==null) throw new InvalidOperationException("操作仍在确认中，请等待完成后再处理。");
                                        store.ResolvePending(command.pendingHash);
                                        paused=false;Status.fault=null;Status.recovering=false;ResetRetry();
                                    }
                                } catch(Exception error) {Status.resetError=error.Message;}
                            }
                        }
                        if(stop) break;
                        if(retryCommand==null&&Status.readRetryAt.HasValue&&now()<Status.readRetryAt.Value) {
                            Publish(Connection.RetryMessage(Status.readRetryStage,Status.connectionFailure));await delay(1000).ConfigureAwait(false);continue;
                        }
                        try {
                            if(retryCommand!=null) {
                                Status.retrying=true;Publish("正在重试连接与状态查询…");
                                Status.readRetryAt=now();serverCache=null;
                            }
                            if(client==null||!client.Connected) {
                                if(!Status.disconnectedSince.HasValue) Status.disconnectedSince=now();
                                if(client!=null) {client.Dispose();client=null;}
                                try {client=await reconnect().ConfigureAwait(false);Status.connectionFailure=null;serverCache=null;store.Log("connection-restored");}
                                catch(Exception error) {ConnectionFailed(error);throw new ReadQueryException("重新连接桌宠",error);}
                            }
                            var preference=store.Read<Preference>("preferences.json");
                            if(preference!=null) {
                                if(!Policy.Modes.Contains(preference.mode)) throw new InvalidOperationException("增加模式配置无效，请在面板重新选择。");
                                if(Status.mode!=preference.mode) { Status.mode=preference.mode; nextBoost=0;store.Log("mode-changed",new {mode=Status.mode}); }
                            }
                            if(retryCommand!=null)await ReadVersion().ConfigureAwait(false);
                            Status.state=await ReadSnapshot().ConfigureAwait(false);
                            Status.disconnectedSince=null;Status.connectionFailure=null;
                            if(Status.state.appVersion!=null&&lastAppVersion!=Status.state.appVersion) {
                                store.Log("app-version-validated",new {previous=lastAppVersion,version=Status.state.appVersion});
                                lastAppVersion=Status.state.appVersion;
                            }
                            if(retryCommand!=null) {
                                int wait;
                                try {wait=await client.RetryQuery().ConfigureAwait(false);}
                                catch(Exception error) {throw new ReadQueryException("准备重新查询",error);}
                                if(wait>0) {Status.readRetryNotBefore=now()+wait;Status.readRetryAt=now()+wait;Publish("等待服务器查询间隔结束");continue;}
                            }
                            if(store.Exists("pending.json")) {
                                if(Status.state.loggedIn&&!Status.state.locked)await RecoverPending().ConfigureAwait(false);
                                else Publish("等待登录或更新完成后核对领取结果");
                                await delay(loopDelay).ConfigureAwait(false);continue;
                            }
                            ServerState retryServer=null;
                            if(Status.readRetryCount>0) {
                                retryServer=await ReadServer(true).ConfigureAwait(false);
                                store.Log("read-query-recovered",new {attempts=Status.readRetryCount});
                                ResetRetry();
                            }
                            if(Status.state.loggedIn&&!Status.state.locked) {if(now()>=clock.NextPoll)await ReadServer(true).ConfigureAwait(false);} else {clock.Reset();serverCache=null;}
                            if(!paused&&Status.fault==null&&!store.Exists("pending.json")) {
                                var decision=Status.state;
                                if(clock.Value.updatedAt.HasValue) {
                                    decision=Json.Decode<Snapshot>(Json.Encode(Status.state));
                                    decision.left=clock.Value.left;decision.right=clock.Value.right;
                                }
                                var action=Policy.Choose(decision,Status.mode,now()>=nextBoost,random.NextDouble);
                                if(action.kind=="wait") { if(action.delayMs>0) nextBoost=now()+action.delayMs;Publish(action.reason); }
                                else {
                                    if(await ReadVersion().ConfigureAwait(false)!=Status.state.appVersion) {
                                        Publish("桌宠版本已变化，等待重新同步");await delay(loopDelay).ConfigureAwait(false);continue;
                                    }
                                    var server=retryServer??await ReadServer().ConfigureAwait(false);server.Validate();
                                    if(string.IsNullOrEmpty(Status.state.accountKey)||server.accountKey!=Status.state.accountKey) {serverCache=null;Publish("账号状态已变化，等待重新同步");}
                                    else if(server.consumed!=Status.state.consumed) Publish("等待桌宠同步服务器余额");
                                    else if(action.kind=="claim"&&server.Slot(action.side)>now()) Publish("等待服务器确认槽位到期");
                                    else {
                                        if(action.kind=="boost") nextBoost=now()+action.delayMs;
                                        var before=Status.state;
                                        if(action.kind=="claim") {
                                            before=Json.Decode<Snapshot>(Json.Encode(before));
                                            before.left=server.left;before.right=server.right;
                                        }
                                        var journal=store.Begin(action,before,server);
                                        var after=action.kind=="boost"?await client.Boost(action.delta,before).ConfigureAwait(false):await client.Claim(action.side,before).ConfigureAwait(false);
                                        if(action.kind=="claim")await AcceptClaim(journal,after,false).ConfigureAwait(false);
                                        else if(after.outcome!=null)await AcceptBoost(journal,after).ConfigureAwait(false);
                                        else if(after.skipped) {store.Log("skipped",new {reason=after.reason});store.ClearPending();Publish("补数条件已变化");}
                                        else {
                                            after=await ObserveBoost(action.delta,before,after).ConfigureAwait(false);
                                            store.Log(Policy.BoostOverwritten(action.delta,before,after)?"boost-overwritten":"boost-confirmed",new {mode=Status.mode,delta=action.delta,before=before,after=after});store.ClearPending();Status.state=after;
                                            Publish(Policy.BoostOverwritten(action.delta,before,after)?"本次补数被旧上报覆盖，按最新计数继续":"次数补充已确认");
                                        }
                                        if(action.kind=="boost") loopDelay=(int)Math.Max(100,Math.Min(1000,nextBoost-now()));
                                    }
                                }
                            } else Publish(Status.fault??"已暂停");
                        } catch(Exception error) {
                            if(error is ReadQueryException) {
                                var kind=Connection.Classify(error);
                                if(client==null||!client.Connected||kind!="connection-failed") ConnectionFailed(error);
                                var api=error.InnerException as AppQueryException;
                                Status.recovering=store.Exists("pending.json");
                                Retry(((ReadQueryException)error).Operation,Status.recovering?"verify":"query",api==null?error.InnerException.GetType().Name:api.ErrorType,
                                    api==null?0:api.Code,api==null?0:api.HttpStatus,api==null?0:api.RetryAfterMs,api==null?0:api.RequiredRetryAfterMs);
                            } else {
                                var pending=store.Exists("pending.json")?store.Read<Journal>("pending.json"):null;
                                if(pending!=null&&pending.action!=null&&(pending.action.kind=="claim"||pending.action.kind=="boost")&&!string.IsNullOrEmpty(pending.id)) {
                                    Status.recovering=true;Status.fault="操作结果尚未确认，已保留记录并自动核对。";
                                    Retry("核对操作结果","observer",error.GetType().Name);
                                } else {
                                    if(Status.fault!=error.Message)store.Log("paused-on-error",new {message=error.Message});
                                    Status.fault=error.Message;Publish(Status.fault);
                                }
                            }
                        }
                        finally {
                            if(retryCommand!=null) {Status.retrying=false;Status.retryCommandId=retryCommand;Publish(Status.message);}
                        }
                        await delay(loopDelay).ConfigureAwait(false);
                    }
                } catch(Exception error) { Status.fault=error.Message;store.Log("stopped-on-error",new {message=error.Message}); }
                try {
                    Status.stoppedWithoutRestore=stop&&(!restoreRequested||client==null||!client.Connected);
                    if(restoreRequested&&!Status.stoppedWithoutRestore) {
                        try { await restore(client).ConfigureAwait(false);Status.normalStartupRestored=true; }
                        catch(Exception error) { Status.fault=error.Message;store.Log("cleanup-needs-attention",new {message=error.Message}); }
                    }
                } finally {
                    if(client!=null) client.Dispose();
                    Status.running=false;Status.recovering=false;Status.automationDisconnected=true;Publish("已停止");File.Delete(store.FilePath("lock.json"));
                }
            }
            return Status.fault==null?0:1;
        }
    }
}
