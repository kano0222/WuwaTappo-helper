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
        readonly RuntimeStore store; readonly Func<Task<IAppClient>> connect;
        readonly Func<IAppClient,Task> restore; readonly Func<int,Task> delay; readonly Func<long> now;
        readonly Random random=new Random(); readonly ChestClock clock;
        public Status Status=new Status {running=true,mode="brute"};
        bool stop,restoreRequested,paused; string lastCommand;
        IAppClient client; long nextBoost;
        public Worker(RuntimeStore store,Func<Task<IAppClient>> connect,Func<IAppClient,Task> restore,Func<int,Task> delay,Func<long> now) {
            this.store=store;this.connect=connect;this.restore=restore;this.delay=delay;this.now=now;clock=new ChestClock(now);
        }
        public void RequestDisconnect() { stop=true; }
        void Publish(string message) {
            Status.at=DateTimeOffset.UtcNow.ToString("o"); Status.pid=Process.GetCurrentProcess().Id;
            Status.paused=paused||Status.fault!=null||store.Exists("pending.json"); Status.message=Status.fault??message; Status.chestTimes=clock.Value;
            store.Write("status.json",Status);
        }
        async Task<Snapshot> ReadSnapshot() {
            try {return await client.Snapshot().ConfigureAwait(false);}
            catch(Exception error) {throw new ReadQueryException("读取桌宠状态",error);}
        }
        async Task<ServerState> ReadServer() {
            ServerState result;
            try {result=await client.Server().ConfigureAwait(false);}
            catch(Exception error) {throw new ReadQueryException("读取服务器状态",error);}
            result.Validate();clock.Accept(result);return result;
        }
        async Task<Snapshot> ObserveBoost(int delta,Snapshot before,Snapshot after) {
            // The event is sent once. Only read snapshots while its result propagates.
            int reads=0;
            while(!Policy.BoostConfirmed(delta,before,after)&&Policy.BoostContextMatches(before,after)&&reads<12) {
                await delay(250).ConfigureAwait(false);
                after=await client.Snapshot().ConfigureAwait(false);reads++;
            }
            if(!Policy.BoostConfirmed(delta,before,after)) {
                store.Log("boost-unconfirmed",new {delta=delta,before=before,after=after,confirmationReads=reads});
                throw new InvalidOperationException("补充 "+delta+" 次的结果未确认，已暂停且不会重发；可点击“结束待确认操作”归档并恢复。");
            }
            return after;
        }
        string PendingMessage() {
            var journal=store.Read<Journal>("pending.json");DateTimeOffset at;
            var when=journal!=null&&DateTimeOffset.TryParse(journal.at,out at)?at.ToLocalTime().ToString("MM-dd HH:mm:ss"):"此前";
            var action=journal==null?null:journal.action;
            var description=action==null?"操作":action.kind=="boost"?"补充 "+action.delta+" 次":action.kind=="claim"?"领取"+(action.side=="left"?"宝箱一":"宝箱二"):"操作";
            return when+" 的"+description+"结果未确认；不会自动重发，可点击“结束待确认操作”处理。";
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
                    client=await connect().ConfigureAwait(false); store.Log("started",new {mode="background"});
                    if(store.Exists("pending.json")) Status.fault=PendingMessage();
                    Publish("已连接");
                    while(!stop) {
                        int loopDelay=1000;
                        var command=store.Read<Command>("control.json");
                        if(command!=null&&command.id!=lastCommand) {
                            lastCommand=command.id;
                            if(command.action=="stop"||command.action=="disconnect") { stop=true;restoreRequested=command.action=="stop"; }
                            else if(command.action=="pause") paused=true;
                            else if(command.action=="resume"&&!store.Exists("pending.json")) { paused=false;Status.fault=null; }
                            else if(command.action=="reset") {
                                Status.resetCommandId=command.id;Status.resetError=null;
                                try {
                                    if(store.Exists("pending.json")) {
                                        if(Status.fault==null) throw new InvalidOperationException("操作仍在确认中，请等待完成后再处理。");
                                        store.ResolvePending(command.pendingHash);
                                        paused=false;Status.fault=null;
                                    }
                                } catch(Exception error) {Status.resetError=error.Message;}
                            }
                        }
                        if(stop) break;
                        if(Status.readRetryAt.HasValue&&now()<Status.readRetryAt.Value) {
                            Publish("状态查询失败，等待自动重试");await delay(1000).ConfigureAwait(false);continue;
                        }
                        try {
                            var preference=store.Read<Preference>("preferences.json");
                            if(preference!=null) {
                                if(!Policy.Modes.Contains(preference.mode)) throw new InvalidOperationException("增加模式配置无效，请在面板重新选择。");
                                if(Status.mode!=preference.mode) { Status.mode=preference.mode; nextBoost=0;store.Log("mode-changed",new {mode=Status.mode}); }
                            }
                            Status.state=await ReadSnapshot().ConfigureAwait(false);
                            ServerState retryServer=null;
                            if(Status.readRetryCount>0) {
                                retryServer=await ReadServer().ConfigureAwait(false);
                                store.Log("read-query-recovered",new {attempts=Status.readRetryCount});
                                Status.readRetryAt=null;Status.readRetryCount=0;Status.readRetryError=null;
                            }
                            if(Status.state.loggedIn&&!Status.state.locked) await clock.Poll(client.Server).ConfigureAwait(false); else clock.Reset();
                            if(!paused&&Status.fault==null&&!store.Exists("pending.json")) {
                                var action=Policy.Choose(Status.state,Status.mode,now()>=nextBoost,random.NextDouble);
                                if(action.kind=="wait") { if(action.delayMs>0) nextBoost=now()+action.delayMs;Publish(action.reason); }
                                else {
                                    var server=retryServer??await ReadServer().ConfigureAwait(false);server.Validate();
                                    if(server.consumed!=Status.state.consumed) Publish("等待桌宠同步服务器余额");
                                    else if(action.kind=="claim"&&(server.Slot(action.side)>now()||server.Slot(action.side)!=Status.state.Slot(action.side))) Publish("等待服务器确认槽位到期");
                                    else {
                                        if(action.kind=="boost") nextBoost=now()+action.delayMs;
                                        var before=Status.state;store.Begin(action,before,server);
                                        var after=action.kind=="boost"?await client.Boost(action.delta,before).ConfigureAwait(false):await client.Claim(action.side,before).ConfigureAwait(false);
                                        if(after.skipped) {store.Log("skipped",new {reason=after.reason});store.ClearPending();}
                                        else if(after.rejected&&action.kind=="claim") {
                                            var serverAfter=await ReadServer().ConfigureAwait(false);
                                            var reason=Policy.ConfirmRejected(after.code,before,after,server,serverAfter);
                                            store.Log("claim-rejected",new {side=action.side,code=after.code,before=before,after=after,serverBefore=server,serverAfter=serverAfter});
                                            store.ClearPending();Status.state=after;throw new InvalidOperationException(reason);
                                        } else if(action.kind=="boost") {
                                            after=await ObserveBoost(action.delta,before,after).ConfigureAwait(false);
                                            store.Log("boost-confirmed",new {mode=Status.mode,delta=action.delta,before=before,after=after});store.ClearPending();Status.state=after;
                                        } else {
                                            var serverAfter=await ReadServer().ConfigureAwait(false);Policy.Confirm(action.side,before,after,server,serverAfter);
                                            store.Log("claim-confirmed",new {side=action.side,before=before,after=after,serverBefore=server,serverAfter=serverAfter});
                                            Status.claimsThisRun++;store.ClearPending();Status.state=after;
                                        }
                                        Publish(action.kind=="claim"?"领取已确认":"次数补充已确认");
                                        if(action.kind=="boost") loopDelay=(int)Math.Max(100,Math.Min(1000,nextBoost-now()));
                                    }
                                }
                            } else Publish(Status.fault??"已暂停");
                        } catch(Exception error) {
                            if(error is ReadQueryException&&!store.Exists("pending.json")&&Status.fault==null) {
                                Status.readRetryCount++;
                                var seconds=Math.Min(60,5*(1<<Math.Min(4,Status.readRetryCount-1)));
                                Status.readRetryAt=now()+seconds*1000;Status.readRetryError=error.Message;
                                store.Log("read-query-retry",new {operation=((ReadQueryException)error).Operation,errorType=error.InnerException.GetType().Name,attempt=Status.readRetryCount,retryAfterSeconds=seconds});
                                Publish("状态查询失败，等待自动重试");
                            } else {
                                if(Status.fault!=error.Message) store.Log("paused-on-error",new {message=error.Message});
                                Status.fault=error.Message;Publish(Status.fault);
                            }
                        }
                        await delay(loopDelay).ConfigureAwait(false);
                    }
                } catch(Exception error) { Status.fault=error.Message;store.Log("stopped-on-error",new {message=error.Message}); }
                try {
                    if(restoreRequested) {
                        try { await restore(client).ConfigureAwait(false);Status.normalStartupRestored=true; }
                        catch(Exception error) { Status.fault=error.Message;store.Log("cleanup-needs-attention",new {message=error.Message}); }
                    }
                } finally {
                    if(client!=null) client.Dispose();
                    Status.running=false;Status.automationDisconnected=true;Publish("已停止");File.Delete(store.FilePath("lock.json"));
                }
            }
            return Status.fault==null?0:1;
        }
    }
}
