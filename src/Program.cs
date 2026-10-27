using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace Tappo {
    internal static class Program {
        [STAThread] public static int Main(string[] args) {
            if(args.Length==1&&args[0]=="--check") {
                try {
                    Resources.Text("Panel.xaml");Resources.Text("bridge.discover.js");Resources.Text("bridge.state.js");Resources.Text("bridge.boost.js");Resources.Text("bridge.claim.js");
                    using(var icon=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("helper.ico")) if(icon==null) return 1;
                    return 0;
                } catch {return 1;}
            }
            var store=new RuntimeStore(AppDomain.CurrentDomain.BaseDirectory);
            bool headless=args.Length>0;
            try {
                if(args.Length==1&&args[0]=="--worker") {
                    var worker=new Worker(store,async()=>{await Platform.EnsureDebugSession(store).ConfigureAwait(false);return await AppClient.Connect().ConfigureAwait(false);},
                        client=>Platform.Restore(client,store),Task.Delay,()=>RuntimeStore.Now);
                    return worker.Run().GetAwaiter().GetResult();
                }
                if(args.Length==2&&args[0]=="--probe") {Probe(args[1]).GetAwaiter().GetResult();return 0;}
                if(args.Length==2&&args[0]=="--control") {
                    if(Array.IndexOf(new[]{"pause","resume","stop","disconnect"},args[1])<0) throw new InvalidOperationException("未知控制操作。");
                    if(!Panel.Alive(store.Read<Status>("status.json"))) throw new InvalidOperationException("后台未运行。");
                    if(args[1]=="resume"&&store.Exists("pending.json")) throw new InvalidOperationException("存在未确认记录，禁止恢复。");
                    store.Write("control.json",new Command {id=Guid.NewGuid().ToString(),action=args[1]});return 0;
                }
                var render=args.Length==2&&args[0]=="--render";
                if(args.Length>0&&!render) throw new InvalidOperationException("不支持的启动参数。");
                using(var panel=new Panel(store)) {
                    if(render) {panel.Render(Path.GetFullPath(args[1]));return 0;}
                    if(!panel.ClaimInstance()) return 0;
                    var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
                    app.DispatcherUnhandledException+=(s,e)=>{RuntimeStore.Append(store.FilePath("panel-launcher-error.log"),e.Exception.Message+"\n");e.Handled=true;};
                    panel.Window.Height=Math.Max(660,Math.Min(900,SystemParameters.WorkArea.Height-40));
                    panel.InitializeTray();panel.StartTimer();panel.Refresh();app.Run(panel.Window);
                }
                return 0;
            } catch(Exception error) {
                store.Write("launcher-status.json",new LauncherStatus {failedAt=DateTimeOffset.UtcNow.ToString("o"),error=error.Message});
                RuntimeStore.Append(store.FilePath("panel-launcher-error.log"),DateTimeOffset.Now.ToString("o")+" "+error+"\n");
                if(!headless) MessageBox.Show(error.Message,"Wuwa Tappo · 后台助手",MessageBoxButton.OK,MessageBoxImage.Error);
                return 1;
            }
        }
        static async Task Probe(string output) {
            using(var client=await AppClient.Connect().ConfigureAwait(false)) {
                var state=await client.Snapshot().ConfigureAwait(false);
                Policy.Choose(state,"brute",true,()=>0.5); // Validate local state without executing the returned plan.
                var server=await client.Server().ConfigureAwait(false);server.Validate();
                RuntimeStore.AtomicWrite(Path.GetFullPath(output),new {version=client.Profile.version,capabilitiesValidated=true,localStateValidated=true,serverChestTimesValid=server.left.HasValue&&server.right.HasValue,mutationsExecuted=false});
            }
        }
    }
}
