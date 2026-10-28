using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Tappo {
    internal static class ConnectionChecks {
        static int checks;
        static void Check(bool value,string message) {if(!value)throw new Exception(message);checks++;}
        public static async Task<int> Run() {
            checks=0;
            var root=Path.Combine(Path.GetTempPath(),"tappo-connection-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                foreach(var pending in new[]{false,true}) foreach(var scenario in new[]{"short","long","main","incompatible"}) {
                    var store=new RuntimeStore(Path.Combine(root,scenario+pending));
                    if(pending)store.Write("pending.json",new {historical=true});
                    var client=new FakeClient();client.State.loggedIn=false;
                    long now=100000;int loops=0,restores=0;bool sawWait=false,sawGuidance=false,sawCategory=false,sawRecovered=false,sawSpecific=false;
                    var kind=scenario=="main"?"main-not-ready":scenario=="incompatible"?"incompatible":"debug-unavailable";
                    Func<Task<IAppClient>> connect=()=> {
                        if(now<100000+(scenario=="short"?15000:90000)) throw new ConnectionFailureException(kind,"test failure");
                        return Task.FromResult<IAppClient>(client);
                    };
                    Worker worker=null;
                    worker=new Worker(store,connect,c=>{restores++;return Task.FromResult(0);},ms=> {
                        var status=worker.Status;var hint=Connection.Hint(status,now);
                        if(hint=="正在等待桌宠重新连接")sawWait=true;
                        if(scenario=="main"&&now-100000>60000&&hint=="桌宠主窗口尚未就绪，正在等待桌宠完成启动。")sawSpecific=true;
                        if(scenario=="incompatible"&&hint=="当前桌宠版本暂不兼容，请检查助手更新。")sawSpecific=true;
                        if(hint!=null&&hint.Contains("先点击“停止后台”")) {sawGuidance=true;Check(now-100000>60000,"early restart guidance");}
                        if(status.connectionFailure==kind)sawCategory=true;
                        if(status.connected==true&&status.disconnectedSince==null&&status.connectionFailure==null) {
                            sawRecovered=true;Check(Connection.Hint(status,now)==null,"connection hint survived recovery");
                            Check(!pending||store.Exists("pending.json"),"recovery discarded pending");
                            worker.RequestDisconnect();
                        }
                        now+=ms;if(++loops>220)worker.RequestDisconnect();return Task.FromResult(0);
                    },()=>now,connect);
                    await worker.Run();
                    Check(sawCategory&&sawRecovered&&loops<=220,"failure classification or recovery missing: "+scenario);
                    Check(sawGuidance==(scenario=="long"),"wrong prolonged failure guidance: "+scenario);
                    Check(scenario=="incompatible"||sawWait,"missing initial wait");
                    Check(scenario!="main"&&scenario!="incompatible"||sawSpecific,"missing specific failure hint");
                    Check(restores==0&&client.Quits==0&&client.Claims==0&&client.Boosts==0,"retry mutated or restarted pet");
                }
                foreach(var pending in new[]{false,true}) foreach(var scenario in new[]{"connected","disconnected","recovered-before-stop"}) {
                    var store=new RuntimeStore(Path.Combine(root,"stop-"+scenario+pending));
                    if(pending)store.Write("pending.json",new {historical=true});
                    var client=new FakeClient();client.State.loggedIn=false;int restores=0,reconnects=0;long now=100000;
                    var worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>{restores++;return c.Quit();},ms=> {
                        now+=ms;
                        client.Connected=scenario!="disconnected";
                        store.Write("control.json",new Command {id="stop",action=scenario=="recovered-before-stop"?"stop-background":"stop"});
                        return Task.FromResult(0);
                    },()=>now,()=>{reconnects++;throw new Exception("stop must precede reconnect");});
                    await worker.Run();
                    Check(restores==(scenario=="connected"?1:0)&&client.Quits==restores,"stop restarted disconnected or newly recovered pet");
                    Check(worker.Status.stoppedWithoutRestore==(scenario!="connected"),"stop result did not distinguish background-only stop");
                    Check(reconnects==0&&!worker.Status.running,"stop attempted reconnect or left worker running");
                    Check(store.Exists("pending.json")==pending,"stop confirmed pending operation");
                }
                var boundary=new Status {connected=false,disconnectedSince=1000,connectionFailure="debug-unavailable"};
                Check(!Connection.Hint(boundary,61000).Contains("停止后台"),"guidance at exactly sixty seconds");
                Check(Connection.Hint(boundary,61001).Contains("停止后台"),"guidance missing after sixty seconds");
                Check(Connection.Classify(new ReadQueryException("reconnect",new MainWindowNotReadyException()))=="main-not-ready","main timeout classification lost");
                return checks;
            } finally {Directory.Delete(root,true);}
        }
        public static int Panels() {
            int count=0;
            var root=Path.Combine(Path.GetTempPath(),"tappo-connection-panel-"+Guid.NewGuid().ToString("N"));
            var store=new RuntimeStore(root);
            try {
                using(var panel=new Panel(store,true)) {
                    long now=RuntimeStore.Now;
                    var status=new Status {running=true,pid=Process.GetCurrentProcess().Id,at=DateTimeOffset.UtcNow.ToString("o"),connected=false,disconnectedSince=now-61000,connectionFailure="debug-unavailable"};
                    panel.ReadStatus=()=>status;panel.ReadClock=()=>now;panel.InitializeTray();panel.Refresh();
                    if((string)panel.E<Button>("StopButton").Content!="停止后台"||!panel.E<TextBlock>("DetailMessage").Text.Contains("先点击“停止后台”"))throw new Exception("disconnected stop UI missing");count++;
                    status.connected=true; // Connection recovers after rendering, before the click.
                    string action=null;panel.DispatchOverride=x=>action=x;
                    panel.E<Button>("StopButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if(action!="stop-background")throw new Exception("stale stop UI restored pet");count++;
                    store.Write("pending.json",new {historical=true});status.paused=true;status.manuallyPaused=true;status.recovering=true;
                    panel.Refresh();
                    var detail=panel.E<TextBlock>("DetailMessage").Text;
                    if(!detail.Contains("停止后台")||!detail.Contains("新操作保持暂停"))throw new Exception("connection hint overwrote recovery or manual pause");count++;
                    status.disconnectedSince=null;status.connectionFailure=null;panel.Refresh();
                    detail=panel.E<TextBlock>("DetailMessage").Text;
                    if(detail.Contains("停止后台")||!detail.Contains("新操作保持暂停")||!store.Exists("pending.json"))throw new Exception("recovery cleared pause or pending");count++;
                    if((string)panel.E<Button>("StopButton").Content!="停止并恢复桌宠")throw new Exception("connected stop label missing");count++;
                    File.Delete(store.FilePath("pending.json"));status.paused=false;status.manuallyPaused=false;status.recovering=false;status.state=new Snapshot {locked=true};panel.Refresh();
                    if(panel.E<TextBlock>("DetailMessage").Text!="桌宠正在更新，自动操作已暂停。")throw new Exception("update lock hint missing");count++;
                    status.state.locked=false;panel.Refresh();
                    if(panel.E<TextBlock>("DetailMessage").Text.Contains("正在更新"))throw new Exception("update hint survived recovery");count++;
                }
                foreach(var scenario in new[]{"disconnect","update","incompatible","pending","recovering","manual-pause","retry","fault","stale"}) {
                    var notices=new RuntimeStore(Path.Combine(root,scenario));
                    using(var panel=new Panel(notices,true)) {
                        long now=RuntimeStore.Now;
                        var status=new Status {running=true,pid=Process.GetCurrentProcess().Id,at=DateTimeOffset.UtcNow.ToString("o"),connected=true,state=new Snapshot()};
                        panel.ReadStatus=()=>status;panel.ReadClock=()=>now;panel.Refresh();
                        panel.E<ListBox>("ModeSelector").SelectedIndex=1;
                        if(panel.E<TextBlock>("DetailMessage").Text!="模式已保存。")throw new Exception("mode success fixture missing");count++;
                        string expected;
                        switch(scenario) {
                            case "disconnect":status.connected=false;status.disconnectedSince=now-61000;status.connectionFailure="debug-unavailable";expected="先点击“停止后台”";break;
                            case "update":status.state.locked=true;expected="桌宠正在更新";break;
                            case "incompatible":status.connectionFailure="incompatible";expected="当前桌宠版本暂不兼容";break;
                            case "pending":notices.Write("pending.json",new {historical=true});status.paused=true;expected="上次操作结果待确认";break;
                            case "recovering":notices.Write("pending.json",new {historical=true});status.recovering=true;expected="连接或查询恢复后继续确认";break;
                            case "manual-pause":status.paused=true;status.manuallyPaused=true;expected="计数与领取已暂停";break;
                            case "retry":status.readRetryAt=now+5000;status.readRetryError="test query retry";expected="test query retry";break;
                            case "fault":status.fault="test operation fault";expected=status.fault;break;
                            default:status.at=DateTimeOffset.UtcNow.AddMinutes(-2).ToString("o");expected="不代表当前状态";break;
                        }
                        panel.Refresh();
                        if(!panel.E<TextBlock>("DetailMessage").Text.Contains(expected))throw new Exception("success notice hid status: "+scenario);count++;
                        // Saving another mode while attention is required must not hide it either.
                        panel.E<ListBox>("ModeSelector").SelectedIndex=2;
                        if(!panel.E<TextBlock>("DetailMessage").Text.Contains(expected))throw new Exception("new success notice hid status: "+scenario);count++;
                        if(scenario=="disconnect") {
                            panel.E<ListBox>("ModeSelector").SelectedIndex=-1;
                            if(!panel.E<TextBlock>("DetailMessage").Text.StartsWith("模式保存失败："))throw new Exception("current operation error was discarded");count++;
                            panel.E<ListBox>("ModeSelector").SelectedIndex=1;
                            if(!panel.E<TextBlock>("DetailMessage").Text.Contains(expected))throw new Exception("success did not clear previous operation error");count++;
                        }
                        if(notices.Exists("pending.json"))File.Delete(notices.FilePath("pending.json"));
                        status.connected=true;status.disconnectedSince=null;status.connectionFailure=null;status.state.locked=false;
                        status.paused=false;status.manuallyPaused=false;status.recovering=false;status.readRetryAt=null;status.fault=null;status.at=DateTimeOffset.UtcNow.ToString("o");
                        panel.Refresh();
                        if(panel.E<TextBlock>("DetailMessage").Text.Contains("模式已保存"))throw new Exception("old success notice resurfaced after recovery: "+scenario);count++;
                    }
                }
                return count;
            } finally {Directory.Delete(root,true);}
        }
    }
}
