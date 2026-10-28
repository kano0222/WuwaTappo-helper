using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tappo {
    internal interface IAppClient : IDisposable {
        bool Connected { get; }
        Task<string> CheckVersion();
        Task<Snapshot> Snapshot(); Task<ServerState> Server(); Task<Snapshot> Boost(int delta,Snapshot before);
        Task<Snapshot> Claim(string side,Snapshot before); Task Quit();
        Task<Snapshot> Recover(Journal journal);
    }
    internal sealed class AppProfile { public string version, commonPath, quitPath, clickPath; public Dictionary<string,string> exports; }
    internal sealed class DiscoveryResult { public AppProfile profile; public string error; }
    internal sealed class Target { public string type,url,webSocketDebuggerUrl; }
    internal sealed class QueryReply { public ServerState state; public string errorType; public int httpStatus,code,retryAfterMs; }
    internal sealed class AppQueryException : Exception {
        public readonly string ErrorType; public readonly int HttpStatus,Code,RetryAfterMs;
        public AppQueryException(QueryReply reply):base("服务器状态查询失败") {ErrorType=reply.errorType;HttpStatus=reply.httpStatus;Code=reply.code;RetryAfterMs=reply.retryAfterMs;}
    }
    internal sealed class AppClient : IAppClient {
        readonly ClientWebSocket socket;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly Dictionary<int,TaskCompletionSource<object>> pending=new Dictionary<int,TaskCompletionSource<object>>();
        readonly SemaphoreSlim sends=new SemaphoreSlim(1,1);
        int sequence; bool disposed; volatile bool disconnected;
        public bool Connected { get {return !disposed&&!disconnected&&socket.State==WebSocketState.Open;} }
        readonly Capabilities capabilities;
        public AppProfile Profile { get {return capabilities.Profile;} }
        static readonly string[] KnownErrors={"未确认领取结果，不自动重试","领取期间出现其他计数或槽位变化，已暂停且不会自动重发","补数期间账号变化，结果未确认，不自动重发"};
        AppClient(ClientWebSocket socket) { this.socket=socket;capabilities=new Capabilities(Evaluate,()=>RuntimeStore.Now); }
        public static async Task<string> LocalGet(string suffix,int timeout) {
            using(var handler=new HttpClientHandler {UseProxy=false})
            using(var http=new HttpClient(handler) {Timeout=TimeSpan.FromMilliseconds(timeout)})
                return await http.GetStringAsync("http://127.0.0.1:19222"+suffix).ConfigureAwait(false);
        }
        internal static async Task<T> WaitForMain<T>(Func<Task<T>> inspect,Func<int,Task> delay,Func<long> now,int timeoutMs) where T:class {
            var deadline=now()+timeoutMs;
            while(true) {
                var result=await inspect().ConfigureAwait(false);
                if(result!=null) return result;
                if(now()>=deadline) throw new TimeoutException("桌宠主窗口尚未就绪，请等待桌宠完成启动后再试。");
                await delay(400).ConfigureAwait(false);
            }
        }
        public static async Task<AppClient> Connect(int mainWaitMs=25000) {
            var selected=await WaitForMain(FindMain,Task.Delay,()=>RuntimeStore.Now,mainWaitMs).ConfigureAwait(false);
            try {
                await selected.capabilities.Ensure().ConfigureAwait(false);
                return selected;
            } catch {selected.Dispose();throw;}
        }
        static async Task<AppClient> FindMain() {
            Platform.CheckListeners();
            var targets=Json.Decode<Target[]>(await LocalGet("/json/list",5000).ConfigureAwait(false));
            AppClient selected=null;
            try {
                foreach(var target in targets.Where(t=>t.type=="page")) {
                    Uri page,address;
                    if(!Uri.TryCreate(target.url,UriKind.Absolute,out page)||page.Host!="tauri.localhost") continue;
                    if(!Uri.TryCreate(target.webSocketDebuggerUrl,UriKind.Absolute,out address)||address.Scheme!="ws"||
                        !new[]{"127.0.0.1","localhost","[::1]","::1"}.Contains(address.Host)||address.Port!=19222)
                        throw new InvalidOperationException("调试地址超出本机范围");
                    var ws=new ClientWebSocket(); ws.Options.Proxy=null;
                    using(var timeout=new CancellationTokenSource(5000)) {
                        try { await ws.ConnectAsync(address,timeout.Token).ConfigureAwait(false); }
                        catch { ws.Dispose(); throw new InvalidOperationException("连接超时或失败"); }
                    }
                    var client=new AppClient(ws);
                    client.StartReceive();
                    try {
                        var label=await client.Evaluate("window.__TAURI_INTERNALS__?.metadata?.currentWindow?.label").ConfigureAwait(false) as string;
                        if(label!="main") { client.Dispose(); continue; }
                        if(selected!=null) { client.Dispose(); throw new InvalidOperationException("主窗口不唯一"); }
                        selected=client;
                    } catch { client.Dispose(); throw; }
                }
                return selected;
            } catch { if(selected!=null) selected.Dispose(); throw; }
        }
        async void StartReceive() {
            var buffer=new byte[32768];
            try {
                while(!lifetime.IsCancellationRequested) {
                    using(var message=new MemoryStream()) {
                        WebSocketReceiveResult frame;
                        do {
                            frame=await socket.ReceiveAsync(new ArraySegment<byte>(buffer),lifetime.Token).ConfigureAwait(false);
                            if(frame.MessageType==WebSocketMessageType.Close) throw new IOException("连接关闭");
                            message.Write(buffer,0,frame.Count);
                            if(message.Length>8000000) throw new IOException("调试响应过大");
                        } while(!frame.EndOfMessage);
                        var response=Json.Decode<Dictionary<string,object>>(Encoding.UTF8.GetString(message.ToArray()));
                        if(!response.ContainsKey("id")) continue;
                        var id=Convert.ToInt32(response["id"]); TaskCompletionSource<object> waiter;
                        lock(pending) { if(!pending.TryGetValue(id,out waiter)) continue; pending.Remove(id); }
                        try {
                            if(response.ContainsKey("error")) throw new InvalidOperationException("应用调用失败，结果可能不确定");
                            var result=(Dictionary<string,object>)response["result"];
                            if(result.ContainsKey("exceptionDetails")) {
                                var raw=Json.Encode(result["exceptionDetails"]);
                                throw new InvalidOperationException(KnownErrors.FirstOrDefault(raw.Contains)??"应用调用失败，结果可能不确定");
                            }
                            var value=(Dictionary<string,object>)result["result"];
                            waiter.TrySetResult(value.ContainsKey("value")?value["value"]:null);
                        } catch(Exception error) { waiter.TrySetException(error); }
                    }
                }
            } catch {
                disconnected=true;
                try {socket.Abort();} catch(ObjectDisposedException) { }
                FailPending();
            }
        }
        void FailPending() {
            lock(pending) {
                foreach(var waiter in pending.Values) waiter.TrySetException(new IOException("后台连接断开，禁止自动重发操作"));
                pending.Clear();
            }
        }
        public async Task<object> Evaluate(string expression,int timeout=8000) {
            if(!Connected) throw new IOException("桌宠连接已断开，等待重新连接；不会自动重发操作。");
            var id=Interlocked.Increment(ref sequence); var waiter=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock(pending) pending.Add(id,waiter);
            try {
                var message=Json.Encode(new Dictionary<string,object>{{"id",id},{"method","Runtime.evaluate"},{"params",new {expression=expression,awaitPromise=true,returnByValue=true,timeout=timeout}}});
                var bytes=Encoding.UTF8.GetBytes(message);
                using(var deadline=new CancellationTokenSource(timeout+2000)) {
                    await sends.WaitAsync(deadline.Token).ConfigureAwait(false);
                    try { await socket.SendAsync(new ArraySegment<byte>(bytes),WebSocketMessageType.Text,true,deadline.Token).ConfigureAwait(false); }
                    finally { sends.Release(); }
                    var finished=await Task.WhenAny(waiter.Task,Task.Delay(timeout+2000)).ConfigureAwait(false);
                    if(finished!=waiter.Task) throw new TimeoutException("调用超时，禁止自动重发操作");
                    return await waiter.Task.ConfigureAwait(false);
                }
            } finally { lock(pending) pending.Remove(id); }
        }
        string StateSource() {
            if(Profile==null) throw new InvalidOperationException("尚未校验应用能力");
            var source=Resources.Text("bridge.state.js").Replace("__COMMON__",Json.Encode(Profile.commonPath));
            foreach(var pair in Profile.exports) source=source.Replace("__"+pair.Key.ToUpperInvariant()+"__",Json.Encode(pair.Value));
            return source;
        }
        string Script(string name,Snapshot before) {
            var text=Resources.Text("bridge."+name+".js").Replace("__STATE__",StateSource());
            text=text.Replace("__CLICKMODULE__",Json.Encode(Profile.clickPath));
            foreach(var pair in Profile.exports) text=text.Replace("__"+pair.Key.ToUpperInvariant()+"__",Json.Encode(pair.Value));
            if(before!=null) foreach(var pair in Json.Decode<Dictionary<string,object>>(Json.Encode(before)))
                text=text.Replace("__BEFORE_"+pair.Key.ToUpperInvariant()+"__",Json.Encode(pair.Value));
            return text;
        }
        public async Task<Snapshot> Snapshot() {
            await capabilities.Ensure().ConfigureAwait(false);
            var state=Json.Convert<Snapshot>(await Evaluate("(async()=>{"+StateSource()+"return snapshot();})()").ConfigureAwait(false));
            state.appVersion=Profile.version;return state;
        }
        public async Task<string> CheckVersion() {
            await capabilities.Ensure(true).ConfigureAwait(false);return Profile.version;
        }
        public async Task<ServerState> Server() {
            var reply=Json.Convert<QueryReply>(await Evaluate("(async()=>{"+StateSource()+"try{const d=await readServer();return {state:{accountKey,total:Number(d.total_click_count??NaN),consumed:Number(d.total_click_consume??NaN),left:Number(d.left_treasure_chest??NaN),right:Number(d.right_treasure_chest??NaN)}}}catch(e){return failure(e,'query')}})()",12000).ConfigureAwait(false));
            if(reply==null||reply.state==null)throw new AppQueryException(reply??new QueryReply {errorType="InvalidReply"});
            return reply.state;
        }
        public async Task<Snapshot> Boost(int delta,Snapshot before) {
            if(delta<1||delta>2000) throw new InvalidOperationException("补充次数超出范围");
            return Json.Convert<Snapshot>(await Evaluate(Script("boost",before).Replace("__DELTA__",delta.ToString()).Replace("__RECOVER__","false")).ConfigureAwait(false));
        }
        public async Task<Snapshot> Claim(string side,Snapshot before) {
            if(side!="left"&&side!="right") throw new InvalidOperationException("槽位不匹配");
            return Json.Convert<Snapshot>(await Evaluate(Script("claim",before).Replace("__SIDE__",Json.Encode(side)).Replace("__RECOVER__","false"),12000).ConfigureAwait(false));
        }
        public async Task<Snapshot> Recover(Journal journal) {
            if(journal==null||journal.action==null||(journal.action.kind!="claim"&&journal.action.kind!="boost")||journal.before==null||string.IsNullOrEmpty(journal.id)||journal.before.operationId!=journal.id||string.IsNullOrEmpty(journal.before.accountKey)||
               (journal.action.kind=="claim"&&(journal.action.side!="left"&&journal.action.side!="right")))
                return new Snapshot {outcome="unknown",failureStage="legacy-journal"};
            if(journal.action.kind=="boost")return Json.Convert<Snapshot>(await Evaluate(Script("boost",journal.before).Replace("__DELTA__",journal.action.delta.ToString()).Replace("__RECOVER__","true")).ConfigureAwait(false));
            return Json.Convert<Snapshot>(await Evaluate(Script("claim",journal.before).Replace("__SIDE__",Json.Encode(journal.action.side)).Replace("__RECOVER__","true"),12000).ConfigureAwait(false));
        }
        public async Task Quit() { await Evaluate("(async()=>{await(await import("+Json.Encode(Profile.quitPath)+")).quitApp();return true;})()",15000).ConfigureAwait(false); }
        public void Dispose() {
            if(disposed) return; disposed=true; lifetime.Cancel(); socket.Abort(); socket.Dispose(); FailPending();
        }
    }
}
