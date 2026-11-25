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
        const int PairWaitWindowMs=5000;
        readonly RuntimeStore store; readonly Func<Task<IAppClient>> connect,reconnect;
        readonly Func<IAppClient,Task> closePet; readonly Func<int,Task> delay; readonly Func<long> now;
        readonly Func<Task> prepareResume;
        readonly Random random=new Random(); readonly ChestClock clock;
        DiagnosticSession diagnostics;
        public Status Status=new Status {running=true,mode=Preference.DefaultMode};
        bool stop,closeRequested,paused; string lastCommand,lastAppVersion;
        IAppClient client; long nextBoost,serverCacheUntil; int claimRejections; ServerState serverCache; string lastOperationState;
        Journal pendingClaim;
        string lastClaimReceipt;
        string lastClaimBlock;
        string lastClaimTiming;
        public Worker(RuntimeStore store,Func<Task<IAppClient>> connect,Func<IAppClient,Task> closePet,Func<int,Task> delay,Func<long> now,Func<Task<IAppClient>> reconnect=null,Func<Task> prepareResume=null) {
            this.store=store;this.connect=connect;this.reconnect=reconnect??connect;this.closePet=closePet;this.delay=delay;this.now=now;clock=new ChestClock(now);
            this.prepareResume=prepareResume??(()=>Task.FromResult(0));
        }
        public void RequestDisconnect() { stop=true; }
        void Publish(string message) {
            Status.connected=client!=null&&client.Connected;
            if(Status.connected==false&&!Status.disconnectedSince.HasValue) Status.disconnectedSince=now();
            Status.at=DateTimeOffset.UtcNow.ToString("o"); Status.pid=Process.GetCurrentProcess().Id;
            Status.manuallyPaused=paused;
            var hasPending=store.Exists("pending.json");
            if(!hasPending)pendingClaim=null;
            Status.paused=paused||Status.fault!=null||hasPending; Status.message=Status.fault??message; Status.chestTimes=DisplayChestTimes();
            try {store.Write("status.json",Status);if(diagnostics!=null)diagnostics.Published(Status);}
            catch(Exception error) {if(diagnostics!=null)diagnostics.WriteFailed(error);throw;}
        }
        void LogClaimBlock(string stage,string side,ServerState server=null,string policyReason=null) {
            var key=stage+":"+side+":"+policyReason;
            if(key==lastClaimBlock)return;
            lastClaimBlock=key;
            try {
                var local=Status.state;
                var clockState=clock.Value;
                store.Log("claim-preflight-blocked",new {stage,side,policyReason,snapshotAt=local.time,accountReadAt=clockState.updatedAt,local.loggedIn,local.locked,
                    localLeft=local.left,localRight=local.right,checkedLeft=clockState.updatedAt.HasValue?clockState.left:local.left,
                    checkedRight=clockState.updatedAt.HasValue?clockState.right:local.right,
                    localBalanceEnough=local.available>=1000,
                    totalDelta=server==null?(long?)null:server.total-local.serverTotal,
                    consumedDelta=server==null?(long?)null:server.consumed-local.consumed});
            } catch(IOException) { }
            catch(UnauthorizedAccessException) { }
        }
        void LogClaimTiming(Snapshot local,ServerState server,string result) {
            if(local.clickReportTiming==null)return;
            try {
                local.clickReportTiming.Sanitize();
                bool behind=server.total<local.serverTotal;
                bool usable=local.reportedClickTotal.HasValue&&local.reportedClickTotal==local.serverTotal;
                var key=Json.Encode(local.clickReportTiming)+":"+behind+":"+usable+":"+result;
                if(key==lastClaimTiming)return;
                var stage=lastClaimTiming==null?"first-claim-check":"claim-check-updated";
                lastClaimTiming=key;
                store.Log("claim-startup-timing",new {stage,result,timing=local.clickReportTiming,decisionAt=now(),
                    snapshotAt=local.time,queryAt=server.observedAt??clock.Value.updatedAt,
                    queryBehind=behind,reportEvidenceUsable=usable});
            } catch(IOException) { }
            catch(UnauthorizedAccessException) { }
        }
        ChestTimes DisplayChestTimes() {
            var current=clock.Value;var claim=pendingClaim;
            if(claim==null||claim.before==null||claim.server==null||Status.state==null||!Status.state.loggedIn||
               string.IsNullOrEmpty(claim.before.accountKey)||claim.before.accountKey!=Status.state.accountKey||
               claim.server.accountKey!=claim.before.accountKey||!current.updatedAt.HasValue)return current;
            var left=claim.action.kind=="claim-pair"?PairCooldown(claim.left,claim.server,"left"):
                claim.action.side=="left"?claim.receiptCooldown:null;
            var right=claim.action.kind=="claim-pair"?PairCooldown(claim.right,claim.server,"right"):
                claim.action.side=="right"?claim.receiptCooldown:null;
            var displayLeft=ReceiptDisplayTime(left,current.left,claim.server.left);
            var displayRight=ReceiptDisplayTime(right,current.right,claim.server.right);
            if(displayLeft==current.left&&displayRight==current.right)return current;
            return new ChestTimes {updatedAt=current.updatedAt,left=displayLeft,right=displayRight,error=current.error};
        }
        long? ReceiptDisplayTime(long? receipt,long? current,long? baseline) {
            return receipt.HasValue&&current.HasValue&&baseline.HasValue&&receipt>baseline&&
                receipt>now()&&receipt<=253402300799999L&&receipt>current?receipt:current;
        }
        static long? PairCooldown(PairSlot slot,ServerState baseline,string side) {
            return PairReceipt(slot,baseline)&&slot.applied&&slot.receiptCooldown>baseline.Slot(side)?slot.receiptCooldown:null;
        }
        async Task<Snapshot> ReadSnapshot() {
            try {return await client.Snapshot().ConfigureAwait(false);}
            catch(Exception error) {throw new ReadQueryException("读取桌宠状态",error);}
        }
        async Task<ServerState> ReadServer(bool fresh=false) {
            if(!fresh&&serverCache!=null&&now()<serverCacheUntil&&Status.state!=null&&serverCache.accountKey==Status.state.accountKey)return serverCache;
            ServerState result;
            try {
                result=await client.Server().ConfigureAwait(false);
                if(Status.state==null||string.IsNullOrEmpty(Status.state.accountKey)||result.accountKey!=Status.state.accountKey)
                    throw new InvalidOperationException("查询期间账号已变化");
            }
            catch(Exception error) {throw new ReadQueryException("读取服务器状态",error);}
            AcceptServer(result);return result;
        }
        void AcceptServer(ServerState server,long? observedAt=null) {
            server.Validate();
            long current=now(), at=server.observedAt??observedAt??current;
            if(at>current||at<0||clock.Value.updatedAt>at)return;
            serverCache=server;serverCacheUntil=at+5000;clock.Accept(server,at);
        }
        void AcceptSkippedServer(Journal journal,ServerState server) {
            if(server!=null&&journal.before!=null&&!string.IsNullOrEmpty(journal.before.accountKey)&&
               server.accountKey==journal.before.accountKey&&Status.state!=null&&Status.state.accountKey==server.accountKey)
                AcceptServer(server);
        }
        void ResetRetry() {Status.readRetryAt=null;Status.readRetryNotBefore=null;Status.readRetryCount=0;Status.readRetryError=null;Status.readRetryStage=null;}
        void ConnectionFailed(Exception error) {
            Status.connectionFailure=Connection.Classify(error);
            if(!Status.disconnectedSince.HasValue) Status.disconnectedSince=now();
        }
        void Retry(string operation,string stage,string errorType,int code=0,int httpStatus=0,int minimumMs=0,int requiredMs=-1,Exception failure=null) {
            Status.readRetryCount++;
            Status.readRetryStage=stage;
            var seconds=Math.Min(60,5*(1<<Math.Min(4,Status.readRetryCount-1)));
            var jitter=random.Next(0,1001);var milliseconds=seconds==60?60000-jitter:seconds*1000+jitter;
            milliseconds=Math.Max(milliseconds,Math.Max(minimumMs,(httpStatus==403||httpStatus==429)?60000:0));
            Status.readRetryAt=now()+milliseconds;Status.readRetryError=operation+"，等待自动重试";
            Status.readRetryNotBefore=now()+Math.Max(requiredMs<0?minimumMs:requiredMs,(httpStatus==403||httpStatus==429)?60000:0);
            store.Log("read-query-retry",new {operation=operation,stage=stage,errorType=errorType,connectionFailure=Status.connectionFailure,code=code,httpStatus=httpStatus,attempt=Status.readRetryCount,backoffSeconds=seconds,retryAfterSeconds=(milliseconds+999)/1000,retryDelayMs=milliseconds,message=failure==null?null:failure.GetBaseException().Message,detail=failure==null?null:failure.ToString()});
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
            if(!string.IsNullOrEmpty(before.accountKey)&&after.accountKey==before.accountKey&&
               journal.server!=null&&journal.server.accountKey==before.accountKey) {
                LogClaimReceipt(journal,after.claimReceivedAt);
                bool changed=false;
                if(!journal.clickNum.HasValue&&after.clickNum>=0&&after.clickNum<=9007199254740991L) {
                    journal.clickNum=after.clickNum;changed=true;
                }
                if((after.outcome=="pending"||after.outcome=="unknown")&&
                   !journal.receiptCooldown.HasValue&&after.receiptCooldown>journal.server.Slot(action.side)&&
                   after.receiptCooldown>now()&&after.receiptCooldown<=253402300799999L&&after.Slot(action.side)==after.receiptCooldown) {
                    journal.receiptCooldown=after.receiptCooldown;changed=true;
                }
                // Losing optional evidence must not turn a submitted request into a new failure.
                if(changed) {
                    try {store.Write("pending.json",journal);}
                    catch(IOException) { }
                    catch(UnauthorizedAccessException) { }
                }
            }
            pendingClaim=journal;
            var key=journal.id+":"+after.outcome+":"+after.failureStage+":"+after.code+":"+after.httpStatus+":"+after.waitReason;
            if(lastOperationState!=key&&after.outcome!=null) {
                store.Log("operation-state",new {operationId=journal.id,side=action.side,outcome=after.outcome,stage=after.failureStage,errorType=after.errorType,code=after.code,httpStatus=after.httpStatus,waitReason=after.waitReason,journal.clickNum});lastOperationState=key;
            }
            if(after.skipped||after.outcome=="skipped") {
                if(string.IsNullOrEmpty(before.accountKey)||after.accountKey!=before.accountKey||journal.clickNum.HasValue)
                    throw new InvalidOperationException("无法确认请求尚未发出，保留操作记录。");
                AcceptSkippedServer(journal,after.confirmedServer);
                store.Log("skipped",new {reason=after.reason});store.ClearPending();Status.recovering=false;Status.fault=null;
                if(after.errorType!=null)
                    Retry("领取前检查失败",after.failureStage,after.errorType,after.code,after.httpStatus,after.retryAfterMs,after.requiredRetryAfterMs);
                else {ResetRetry();Publish("领取条件已变化，已重新同步");}
                return;
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
                if(serverAfter.total<journal.clickNum)throw new InvalidOperationException("领取累计值尚未确认，保留操作记录。");
                store.Log("claim-confirmed",new {side=action.side,operationId=journal.id,receivedAt=after.claimReceivedAt,recovered=recovery,before=before,after=after,serverBefore=journal.server,serverAfter=serverAfter,journal.clickNum});
                new ClaimHistory(store,Instances.SavedPath(store.Root)).Record(journal.id);
                store.ClearPending();Status.state=after;Status.claimsThisRun++;Status.fault=null;Status.recovering=false;claimRejections=0;ResetRetry();
                AcceptServer(serverAfter);Publish("领取已确认");return;
            }
            // A read can refresh the panel without proving the pending claim succeeded.
            var observed=after.observedServer;
            var observedAt=after.serverObservedAt;
            if(observed!=null&&observedAt.HasValue&&observedAt.Value>0&&observedAt.Value<=now()&&
               (!clock.Value.updatedAt.HasValue||observedAt.Value>clock.Value.updatedAt.Value)&&
               !string.IsNullOrEmpty(before.accountKey)&&journal.server!=null&&journal.server.accountKey==before.accountKey&&
               after.accountKey==before.accountKey&&observed.accountKey==before.accountKey&&Status.state.accountKey==before.accountKey) {
                AcceptServer(observed,observedAt);
            }
            Status.recovering=true;Status.recoveryStage=after.failureStage;
            if(after.outcome=="unknown"||after.outcome=="confirmed")Status.fault="奖励到账尚未确认，已保留记录并自动核对；可核对桌宠后点击“确认并继续”。";
            else Status.fault=null;
            if(after.errorType!=null||after.outcome=="unknown")Retry("核对领取结果",after.failureStage,after.errorType??"Unverifiable",after.code,after.httpStatus,Math.Max(after.retryAfterMs,after.outcome=="unknown"?30000:0));
            else {Status.readRetryAt=now()+1000;Status.readRetryError="正在核对领取结果";Publish("正在核对领取结果");}
        }
        void LogClaimReceipt(Journal journal,long? receivedAt) {
            if(!receivedAt.HasValue||lastClaimReceipt==journal.id)return;
            try {
                store.Log("claim-received",new {side=journal.action.side,operationId=journal.id,receivedAt});
                lastClaimReceipt=journal.id;
            } catch(IOException) { }
              catch(UnauthorizedAccessException) { }
        }
        async Task RecoverPending() {
            var journal=store.Read<Journal>("pending.json");
            pendingClaim=journal!=null&&journal.action!=null&&(journal.action.kind=="claim"||journal.action.kind=="claim-pair")?journal:null;
            Status.recovering=true;
            if(journal!=null&&journal.action!=null&&journal.action.kind=="claim-pair") {
                await RecoverClaimPair(journal).ConfigureAwait(false);return;
            }
            if(journal!=null&&journal.action!=null&&journal.action.kind=="boost"&&journal.before!=null&&!string.IsNullOrEmpty(journal.id)&&!string.IsNullOrEmpty(journal.before.accountKey)) {
                Snapshot observed;
                try {observed=await client.Recover(journal).ConfigureAwait(false);}
                catch(Exception error) {throw new ReadQueryException("核对补数结果",error);}
                await AcceptBoost(journal,observed,true).ConfigureAwait(false);
                await RefreshPendingClock(observed).ConfigureAwait(false);return;
            }
            if(journal==null||journal.action==null||journal.action.kind!="claim"||journal.before==null||string.IsNullOrEmpty(journal.before.accountKey)||string.IsNullOrEmpty(journal.id)) {
                await ReadServer().ConfigureAwait(false);Status.fault=PendingMessage();Retry("核对历史操作","legacy-journal","Unverifiable",0,0,30000);return;
            }
            if(journal.before.accountKey!=Status.state.accountKey) {
                Status.fault="当前账号与待确认操作不一致，已保留记录。";Retry("等待原账号恢复","account","AccountChanged",0,0,30000);
                await RefreshPendingClock().ConfigureAwait(false);return;
            }
            Snapshot after;
            try {after=await client.Recover(journal).ConfigureAwait(false);}
            catch(Exception error) {throw new ReadQueryException("核对领取结果",error);}
            await AcceptClaim(journal,after,true).ConfigureAwait(false);
            await RefreshPendingClock(after).ConfigureAwait(false);
        }
        static bool PairReceipt(PairSlot slot,ServerState baseline) {
            return slot!=null&&!string.IsNullOrEmpty(slot.id)&&slot.outcome=="success"&&slot.cost==1000&&slot.assetId>0&&
                slot.clickNum>=baseline.total&&(!slot.queryTotal.HasValue||slot.queryTotal>=baseline.total&&slot.queryTotal<=slot.clickNum)&&
                slot.sentAt>0&&slot.receivedAt>=slot.sentAt;
        }
        bool PairAccount(Journal journal,PairResult result) {
            return result!=null&&result.operationId==journal.id&&journal.before!=null&&journal.server!=null&&
                !string.IsNullOrEmpty(journal.before.accountKey)&&journal.before.accountKey==journal.server.accountKey&&
                journal.before.accountKey==Status.state.accountKey&&result.accountKey==journal.before.accountKey;
        }
        async Task<bool> SendPairSlot(Journal journal,string side,PairSlot slot) {
            var result=await client.ClaimPair(journal,side).ConfigureAwait(false);
            var accounted=PairAccount(journal,result);
            if(accounted&&result.outcome=="skipped"&&
               (result.slot==null||result.slot.id==slot.id&&!result.slot.sentAt.HasValue))slot.outcome="skipped";
            else if(accounted&&result.slot!=null&&result.slot.id==slot.id&&
                    (result.outcome=="success"&&PairReceipt(result.slot,journal.server)||
                     result.outcome=="rejected"&&result.slot.outcome=="rejected"&&new[]{70004,70005,70006}.Contains(result.slot.code)||
                     result.outcome=="unknown"&&result.slot.outcome=="unknown"))
                slot=result.slot;
            else slot.outcome="unknown";
            if(side=="left") {
                journal.left=slot;
                if(accounted&&PairReceipt(slot,journal.server))journal.bagBefore=result.bagBefore;
            } else journal.right=slot;
            store.Write("pending.json",journal);
            pendingClaim=journal;
            var preflight=accounted&&result.server!=null&&result.server.accountKey==journal.server.accountKey?result.server:null;
            store.Log("claim-pair-slot",new {side,operationId=slot.id,pairId=journal.id,slot.outcome,slot.clickNum,slot.queryTotal,slot.sentAt,slot.receivedAt,slot.code,
                stage=accounted?result.stage:null,
                timing=accounted?result.timing:null,left=preflight==null?null:preflight.left,right=preflight==null?null:preflight.right});
            if(slot.outcome=="skipped") {
                AcceptSkippedServer(journal,preflight);
                if(result.timing!=null&&result.timing.errorType!=null)
                    Retry("领取前检查失败",result.stage,result.timing.errorType,0,result.httpStatus,result.retryAfterMs,result.requiredRetryAfterMs);
            }
            if(accounted&&result.outcome=="unknown"&&(result.httpStatus>=400||result.retryAfterMs>0||result.requiredRetryAfterMs>0)) {
                Status.recovering=true;Status.fault="双箱领取结果尚未确认，已保留记录。";
                Retry("核对双箱领取结果",result.stage,"Unknown",0,result.httpStatus,result.retryAfterMs,result.requiredRetryAfterMs);
            }
            return PairReceipt(slot,journal.server);
        }
        async Task StartClaimPair(Snapshot before,ServerState server) {
            var journal=store.Begin(new Plan{kind="claim-pair",side="left"},before,server);
            Publish("正在领取宝箱一");
            var firstSucceeded=await SendPairSlot(journal,"left",journal.left).ConfigureAwait(false);
            if(journal.left.outcome=="skipped") {
                store.ClearPending();
                Publish(Status.readRetryAt.HasValue?Status.message:"领取条件已变化，已重新同步");return;
            }
            var command=store.Read<Command>("control.json");
            if(firstSucceeded&&journal.left.applied&&journal.bagBefore!=null&&client.Connected&&!stop&&
               (command==null||command.id==lastCommand)&&!UpdateInstaller.Requested(AppDomain.CurrentDomain.BaseDirectory)) {
                journal.right=new PairSlot{id=Guid.NewGuid().ToString("N"),outcome="armed"};
                store.Write("pending.json",journal);
                Publish("宝箱一已收到回执，正在领取宝箱二");
                await SendPairSlot(journal,"right",journal.right).ConfigureAwait(false);
            }
            if(!Status.readRetryAt.HasValue)await RecoverClaimPair(journal).ConfigureAwait(false);
        }
        async Task RecoverClaimPair(Journal journal) {
            if(journal.before==null||journal.server==null||journal.before.accountKey!=Status.state.accountKey||
               string.IsNullOrEmpty(journal.before.accountKey)||journal.server.accountKey!=journal.before.accountKey||
               journal.left==null||journal.left.id!=journal.id||journal.right!=null&&
               (string.IsNullOrEmpty(journal.right.id)||journal.right.id==journal.id)) {
                Status.fault="双箱记录与当前账号或操作不匹配，已保留记录。";
                Retry("核对双箱领取结果","account","Unverifiable",0,0,30000);
                await RefreshPendingClock().ConfigureAwait(false);return;
            }
            var observeStartedAt=now();
            PairResult result;
            try {result=await client.ClaimPair(journal,"observe").ConfigureAwait(false);}
            catch {
                LogPairVerification(journal.id,null,null,observeStartedAt,now(),null);
                throw;
            }
            var observeFinishedAt=now();
            var accounted=PairAccount(journal,result);
            var server=accounted&&result.server!=null&&result.server.accountKey==journal.server.accountKey?result.server:null;
            if(server!=null)AcceptServer(server);
            LogPairVerification(journal.id,accounted?result:null,server,observeStartedAt,observeFinishedAt,server==null?null:clock.Value.updatedAt);
            if(server!=null) {
                if(result.outcome=="rejected"&&journal.left.outcome=="rejected"&&journal.right==null&&
                   server.consumed==journal.server.consumed&&server.left==journal.server.left&&server.right==journal.server.right) {
                    store.ClearPending();Status.fault=null;Status.recovering=false;ResetRetry();
                    Retry("服务器暂未允许领取","rejected","BusinessError",journal.left.code,0,5000);return;
                }
                var both=PairReceipt(journal.right,journal.server);
                var count=both?2:1;
                if(result.outcome=="confirmed"&&PairReceipt(journal.left,journal.server)&&
                   (both||journal.right==null||journal.right.outcome=="skipped"||journal.right.outcome=="rejected")&&
                   server.consumed==journal.server.consumed+1000*count&&server.left>journal.server.left&&
                   server.total>=journal.left.clickNum&&(!both||server.total>=journal.right.clickNum)&&
                   (both?server.right>journal.server.right:server.right==journal.server.right)&&
                   result.state!=null&&result.state.accountKey==server.accountKey&&result.state.consumed==server.consumed&&
                   result.state.left==server.left&&result.state.right==server.right) {
                    var history=new ClaimHistory(store,Instances.SavedPath(store.Root));
                    history.Record(journal.left.id);
                    if(both)history.Record(journal.right.id);
                    store.Log("claim-confirmed",new {side="left",operationId=journal.left.id,pairId=journal.id,journal.left.receivedAt,serverAfter=server});
                    if(both)store.Log("claim-confirmed",new {side="right",operationId=journal.right.id,pairId=journal.id,journal.right.receivedAt,serverAfter=server});
                    store.ClearPending();Status.state=result.state;Status.claimsThisRun+=count;Status.fault=null;Status.recovering=false;claimRejections=0;ResetRetry();
                    if(journal.right!=null&&journal.right.outcome=="rejected")
                        Retry("第二箱暂未允许领取","rejected","BusinessError",journal.right.code,0,5000);
                    else Publish("领取已确认");
                    return;
                }
            }
            Status.recovering=true;Status.recoveryStage=result==null?"observer":result.stage;
            var lastReceiptAt=Math.Max(journal.left.receivedAt??0,journal.right==null?0:journal.right.receivedAt??0);
            if(accounted&&result.outcome=="pending"&&now()-lastReceiptAt<=60000) {
                Status.fault=null;Status.readRetryAt=now()+1000;Publish("正在核对双箱领取结果");
            } else {
                Status.fault="双箱领取结果尚未确认，已保留记录并自动核对。";
                Retry("核对双箱领取结果",Status.recoveryStage,"Unverifiable",0,result==null?0:result.httpStatus,
                    Math.Max(30000,result==null?0:result.retryAfterMs));
                await RefreshPendingClock(result==null?null:new Snapshot {retryAfterMs=result.retryAfterMs,httpStatus=result.httpStatus}).ConfigureAwait(false);
            }
        }
        void LogPairVerification(string pairId,PairResult result,ServerState server,long observeStartedAt,long observeFinishedAt,long? panelClockAt) {
            try {
                result=result??new PairResult();
                server=server??new ServerState();
                store.Log("claim-pair-observe",new {operationId=pairId,result.outcome,result.stage,
                    result.httpStatus,result.retryAfterMs,observeStartedAt,observeFinishedAt,panelClockAt,
                    server.left,server.right,result.timing});
            } catch(IOException) { }
              catch(UnauthorizedAccessException) { }
        }
        async Task RefreshPendingClock(Snapshot observation=null) {
            if(now()<clock.NextPoll||observation!=null&&(observation.retryAfterMs>0||observation.httpStatus==403||observation.httpStatus==429))return;
            await ReadServer(true).ConfigureAwait(false);
            Publish(Status.message);
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
            var description=action==null?"操作":action.kind=="boost"?"补充 "+action.delta+" 次":action.kind=="claim"?"领取"+(action.side=="left"?"宝箱一":"宝箱二"):action.kind=="claim-pair"?"双箱领取":"操作";
            return when+" 的"+description+"结果未确认，已暂停；核对桌宠后点击“确认并继续”。";
        }
        public async Task<int> Run() {
            // This guard serializes native workers; lock.json also blocks an older Node worker.
            using(var guard=new FileStream(store.FilePath("worker.guard"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                var old=store.Read<Status>("lock.json");
                if(old!=null&&RuntimeStore.Alive(old.pid)) throw new InvalidOperationException("自动化已运行，请先暂停或停止现有后台。");
                if(store.Exists("lock.json")) File.Delete(store.FilePath("lock.json"));
                using(diagnostics=new DiagnosticSession(store,"worker")) {
                diagnostics.Sample=diagnostics.ObserveSelf;
                Status.identity=diagnostics.Identity;
                using(var reservation=new FileStream(store.FilePath("lock.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)) {
                    var bytes=System.Text.Encoding.UTF8.GetBytes(Json.Encode(new Status {pid=Process.GetCurrentProcess().Id,identity=Status.identity}));reservation.Write(bytes,0,bytes.Length);reservation.Flush(true);
                }
                try {
                    try {client=new DiagnosticClient(await diagnostics.Call("connect",connect).ConfigureAwait(false),diagnostics);store.Log("started",new {mode="background"});}
                    catch(StartupConfigurationException) {throw;}
                    catch(Exception error) {ConnectionFailed(error);Retry("重新连接桌宠","connect",error.GetType().Name,failure:error);}
                    if(store.Exists("pending.json")) Status.fault=PendingMessage();
                    Publish(client!=null?"已连接":"正在等待桌宠重新连接");
                    while(!stop) {
                        // Stop between operations; never interrupt an in-flight transaction or erase its journal.
                        if(UpdateInstaller.Requested(AppDomain.CurrentDomain.BaseDirectory)) {stop=true;closeRequested=false;break;}
                        int loopDelay=1000;
                        string retryCommand=null;
                        bool resumeRequested=false;
                        var command=store.Read<Command>("control.json");
                        if(command!=null&&command.id!=lastCommand) {
                            diagnostics.Command("command-received",command);
                            lastCommand=command.id;
                            if(command.action=="stop"||command.action=="stop-background"||command.action=="disconnect") { stop=true;closeRequested=command.action=="stop"&&client!=null&&client.Connected; }
                            else if(command.action=="pause") {
                                if(!paused)store.Log("manual-paused");
                                paused=true;
                            }
                            else if(command.action=="resume"&&!store.Exists("pending.json")) {
                                resumeRequested=true;
                                lastClaimBlock=null;
                                if(paused||Status.fault!=null)store.Log("manual-resumed");
                                paused=false;Status.fault=null;
                                if(!Status.readRetryNotBefore.HasValue||now()>=Status.readRetryNotBefore.Value) retryCommand=command.id;
                                else Status.readRetryAt=Status.readRetryNotBefore;
                            }
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
                            Status.commandId=command.id;
                            diagnostics.Command("command-applied",command);
                        }
                        if(stop) break;
                        if(!resumeRequested&&retryCommand==null&&Status.readRetryAt.HasValue&&now()<Status.readRetryAt.Value) {
                            Publish(Connection.RetryMessage(Status.readRetryStage,Status.connectionFailure));await delay(1000).ConfigureAwait(false);continue;
                        }
                        try {
                            if(resumeRequested) {
                                Status.retrying=true;Publish("正在准备桌宠连接…");
                                try {await diagnostics.Call("prepare-resume",prepareResume).ConfigureAwait(false);}
                                catch(StartupConfigurationException) {throw;}
                                catch(Exception error) {ConnectionFailed(error);throw new ReadQueryException("准备桌宠连接",error);}
                            }
                            if(retryCommand!=null) {
                                Status.retrying=true;Publish(resumeRequested?"正在同步桌宠与账号状态…":"正在重试连接与状态查询…");
                                Status.readRetryAt=now();serverCache=null;
                            }
                            if(client==null||!client.Connected) {
                                if(!Status.disconnectedSince.HasValue) Status.disconnectedSince=now();
                                if(client!=null) {client.Dispose();client=null;}
                                try {client=new DiagnosticClient(await diagnostics.Call("reconnect",reconnect).ConfigureAwait(false),diagnostics);Status.connectionFailure=null;serverCache=null;clock.Reset();store.Log("connection-restored");}
                                catch(Exception error) {ConnectionFailed(error);throw new ReadQueryException("重新连接桌宠",error);}
                            }
                            if(retryCommand==null&&Status.readRetryAt.HasValue&&now()<Status.readRetryAt.Value) {
                                Publish(Connection.RetryMessage(Status.readRetryStage,Status.connectionFailure));await delay(1000).ConfigureAwait(false);continue;
                            }
                            var preference=store.Read<Preference>("preferences.json");
                            if(preference!=null) {
                                preference.Validate();
                                if(Status.mode!=preference.mode) { Status.mode=preference.mode; nextBoost=0;store.Log("mode-changed",new {mode=Status.mode}); }
                                Status.reserveTarget=preference.mode=="brute"?preference.bruteReserveTarget:preference.reserveTarget;Status.clickerRate=preference.mode=="brute"?preference.bruteRate:preference.clickerRate;
                            }
                            if(retryCommand!=null)await ReadVersion().ConfigureAwait(false);
                            var previousAccount=Status.state==null?null:Status.state.accountKey;
                            Status.state=await ReadSnapshot().ConfigureAwait(false);
                            if(previousAccount!=Status.state.accountKey) {lastClaimBlock=null;lastClaimTiming=null;}
                            var canQuery=Status.state.loggedIn&&!Status.state.locked;
                            if(!canQuery||previousAccount!=Status.state.accountKey) {clock.Reset();serverCache=null;}
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
                                if(canQuery)await RecoverPending().ConfigureAwait(false);
                                else Publish("等待登录或更新完成后核对领取结果");
                                await delay(loopDelay).ConfigureAwait(false);continue;
                            }
                            var firstClock=!clock.Value.updatedAt.HasValue;
                            ServerState retryServer=null;
                            if(Status.readRetryCount>0||Status.readRetryAt.HasValue||retryCommand!=null) {
                                retryServer=await ReadServer(true).ConfigureAwait(false);
                                if(Status.readRetryCount>0)store.Log("read-query-recovered",new {attempts=Status.readRetryCount});
                                ResetRetry();
                            }
                            if(canQuery&&now()>=clock.NextPoll)await ReadServer(true).ConfigureAwait(false);
                            if(firstClock&&clock.Value.updatedAt.HasValue)Publish("已同步宝箱冷却");
                            if(!paused&&Status.fault==null&&!store.Exists("pending.json")) {
                                var decision=Status.state;
                                if(clock.Value.updatedAt.HasValue) {
                                    decision=Json.Decode<Snapshot>(Json.Encode(Status.state));
                                    decision.left=clock.Value.left;decision.right=clock.Value.right;
                                }
                                var action=Policy.Choose(decision,Status.mode,now()>=nextBoost,random.NextDouble,Status.reserveTarget,Status.clickerRate);
                                if(action.kind=="wait") {
                                    if(action.delayMs>0) nextBoost=now()+action.delayMs;
                                    if(action.reason=="等待原有活跃计时及宝箱入口"||action.reason=="等待登录或更新完成"||action.reason=="等待当前宝箱领取完成")
                                        LogClaimBlock("policy-wait",null,policyReason:action.reason);
                                    Publish(action.reason);
                                }
                                else {
                                    if(await ReadVersion().ConfigureAwait(false)!=Status.state.appVersion) {
                                        if(action.kind=="claim")LogClaimBlock("version-changed",action.side);
                                        Publish("桌宠版本已变化，等待重新同步");await delay(loopDelay).ConfigureAwait(false);continue;
                                    }
                                    var server=retryServer??await ReadServer().ConfigureAwait(false);server.Validate();
                                    if(string.IsNullOrEmpty(Status.state.accountKey)||server.accountKey!=Status.state.accountKey) {
                                        if(action.kind=="claim")LogClaimBlock("account-changed",action.side);
                                        serverCache=null;Publish("账号状态已变化，等待重新同步");
                                    }
                                    else if(server.consumed!=Status.state.consumed) {
                                        if(action.kind=="claim") {
                                            LogClaimTiming(Status.state,server,"consumed-mismatch");
                                            LogClaimBlock("consumed-mismatch",action.side,server);
                                        }
                                        Publish("等待桌宠同步服务器余额");
                                    }
                                    else {
                                        string blocked=null;
                                        if(action.kind=="claim") {
                                            var side=Policy.ClaimSide(decision.time,server.left,server.right);
                                            if(side!=null)action.side=side;
                                            if(server.total-server.consumed<1000) blocked="server-balance-low";
                                            else if(server.total<Status.state.serverTotal&&Status.state.reportedClickTotal!=Status.state.serverTotal)
                                                blocked="server-total-behind";
                                            else if(side==null||server.Slot(action.side)>now()) blocked="server-slot-not-due";
                                            LogClaimTiming(Status.state,server,blocked??"preflight-passed");
                                        }
                                        if(blocked!=null) {
                                            LogClaimBlock(blocked,action.side,server);
                                            action=Policy.ChooseBoost(decision,Status.mode,now()>=nextBoost,random.NextDouble,Status.reserveTarget,Status.clickerRate);
                                        }
                                        if(action.kind=="wait") {
                                            if(action.delayMs>0) nextBoost=now()+action.delayMs;
                                            Publish(blocked=="server-slot-not-due"?"等待服务器确认槽位到期":"等待桌宠同步服务器余额");
                                        }
                                        else {
                                            if(UpdateInstaller.Requested(AppDomain.CurrentDomain.BaseDirectory)) {stop=true;closeRequested=false;break;}
                                            bool startPair=false;
                                            if(action.kind=="claim"&&client.SupportsClaimPair&&server.total-server.consumed>=2000&&
                                               Status.state.left==server.left&&Status.state.right==server.right) {
                                                var firstDue=Math.Min(server.left.Value,server.right.Value);
                                                var lastDue=Math.Max(server.left.Value,server.right.Value);
                                                var remaining=lastDue+Policy.ClaimSafetyMarginMs-now();
                                                startPair=remaining<=0;
                                                // Anchor the window to the slot deadlines so each pass uses the same cutoff.
                                                if(!startPair&&remaining<=PairWaitWindowMs&&lastDue-firstDue<=PairWaitWindowMs) {
                                                    Publish("等待另一宝箱就绪");
                                                    await delay((int)Math.Min(loopDelay,remaining)).ConfigureAwait(false);continue;
                                                }
                                            }
                                            if(action.kind=="boost") nextBoost=now()+action.delayMs;
                                            var before=Status.state;
                                            if(action.kind=="claim") {
                                                lastClaimBlock=null;
                                                before=Json.Decode<Snapshot>(Json.Encode(before));
                                                before.left=server.left;before.right=server.right;
                                            }
                                            if(startPair) {
                                                await StartClaimPair(before,server).ConfigureAwait(false);
                                                await delay(loopDelay).ConfigureAwait(false);continue;
                                            }
                                            var journal=store.Begin(action,before,server);
                                            var after=action.kind=="boost"?await client.Boost(action.delta,before).ConfigureAwait(false):await client.Claim(journal).ConfigureAwait(false);
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
                                }
                            } else Publish(Status.fault??"已暂停");
                        } catch(Exception error) {
                            if(error is StartupConfigurationException) {Status.fault=error.Message;throw;}
                            if(error is ReadQueryException) {
                                var kind=Connection.Classify(error);
                                if(client==null||!client.Connected||kind!="connection-failed") ConnectionFailed(error);
                                var api=error.InnerException as AppQueryException;
                                Status.recovering=store.Exists("pending.json");
                                Retry(((ReadQueryException)error).Operation,Status.recovering?"verify":"query",api==null?error.InnerException.GetType().Name:api.ErrorType,
                                    api==null?0:api.Code,api==null?0:api.HttpStatus,api==null?0:api.RetryAfterMs,api==null?0:api.RequiredRetryAfterMs,error);
                            } else {
                                var pending=store.Exists("pending.json")?store.Read<Journal>("pending.json"):null;
                                if(pending!=null&&pending.action!=null&&(pending.action.kind=="claim"||pending.action.kind=="claim-pair"||pending.action.kind=="boost")&&!string.IsNullOrEmpty(pending.id)) {
                                    Status.recovering=true;Status.fault="操作结果尚未确认，已保留记录并自动核对。";
                                    Retry("核对操作结果","observer",error.GetType().Name,failure:error);
                                } else {
                                    if(Status.fault!=error.Message)store.Log("paused-on-error",new {message=error.Message,errorType=error.GetType().Name,detail=error.ToString()});
                                    Status.fault=error.Message;Publish(Status.fault);
                                }
                            }
                        }
                        finally {
                            if(resumeRequested||retryCommand!=null) {
                                Status.retrying=false;
                                if(retryCommand!=null)Status.retryCommandId=retryCommand;
                                Publish(Status.message);
                            }
                        }
                        await delay(loopDelay).ConfigureAwait(false);
                    }
                } catch(Exception error) { Status.fault=error.Message;store.Log("stopped-on-error",new {message=error.Message,errorType=error.GetType().Name,detail=error.ToString()}); }
                try {
                    Status.stoppedWithoutClose=stop&&(!closeRequested||client==null||!client.Connected);
                    if(closeRequested&&!Status.stoppedWithoutClose) {
                        try { await diagnostics.Call("close-pet",()=>closePet(client)).ConfigureAwait(false);Status.petClosed=true; }
                        catch(Exception error) { Status.fault=error.Message;store.Log("cleanup-needs-attention",new {message=error.Message,errorType=error.GetType().Name,detail=error.ToString()}); }
                    }
                } finally {
                    if(client!=null) client.Dispose();
                    Status.running=false;Status.recovering=false;Status.automationDisconnected=true;Publish("已停止");File.Delete(store.FilePath("lock.json"));
                }
                }
            }
            return Status.fault==null?0:1;
        }
    }
}
