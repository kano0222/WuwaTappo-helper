using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Tappo {
    internal static class InstanceChecks {
        static void Check(bool valid,string message) {if(!valid)throw new Exception(message);}
        internal static int Run() {
            var root=Path.Combine(Path.GetTempPath(),"tappo-instances-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                var first=Path.Combine(root,"cn","WuwaTappo.exe");var second=Path.Combine(root,"global","WuwaTappo.exe");
                RuntimeStore.AtomicWrite(Path.Combine(root,"config.local.json"),new Settings {appPath=first});
                Check(Instances.ForPath(root,first.ToUpperInvariant())==null,"default binding not reused");
                var id=Instances.ForPath(root,second);var store=Instances.Open(root,id);var original=Instances.Open(root,null);
                Check(id==Instances.ForPath(root,second.ToUpperInvariant()),"path casing creates duplicate instance");
                original.Write("pending.json",new {id="first"});store.Write("pending.json",new {id="second"});
                original.Write("control.json",new Command {action="pause"});store.Write("control.json",new Command {action="resume"});
                Check(original.Read<Command>("control.json").action=="pause"&&store.Read<Command>("control.json").action=="resume","controls overlap");
                Check(original.Read<Journal>("pending.json").id=="first"&&store.Read<Journal>("pending.json").id=="second","pending journals overlap");
                foreach(var invalid in new[]{"..","../escape",new string('z',64)}) {
                    bool rejected=false;try {Instances.Open(root,invalid);}catch(InvalidOperationException){rejected=true;}
                    Check(rejected,"unsafe instance id accepted");
                }
                var tree=Instances.Descendants(10,new Dictionary<int,int>{{11,10},{12,11},{21,20},{20,1}});
                Check(tree.SetEquals(new[]{10,11,12}),"process tree includes another instance");
                using(Instances.Acquire(first)) {
                    Check(Child(first)==2,"second worker took same pet");
                    Check(Child(second)==0,"other pet blocked by first worker");
                }
                Check(Child(first)==0,"lease was not released");
                var info=Platform.AppStartInfo(first,true,23456);
                Check(info.Arguments.Contains("--remote-debugging-port=23456"),"assigned port missing");
                var port=Instances.AvailablePort();var listener=new TcpListener(IPAddress.Loopback,port);
                try {listener.Start();Platform.CheckListeners(port);Check(Instances.OwnedPort(Process.GetCurrentProcess().Id)==port,"native TCP owner lookup failed");}finally {listener.Stop();}
                Check(Instances.Arguments(original)==""&&Instances.Arguments(store)==" --instance "+id,"worker instance arguments lost");
                using(var primary=new Panel(original,preview:true))using(var extra=new Panel(store,preview:true)) {
                    Check(primary.ClaimInstance()&&extra.ClaimInstance()&&Child(store.Root,"--panel-lease-check")==2,"panel instance isolation failed");
                    extra.SetPath(second);
                    Check(extra.Window.Title.Contains("global"),"instance title missing");
                    extra.Render(Path.Combine(root,"panel.png"),820,660);
                    var button=extra.E<System.Windows.Controls.Button>("AnotherAppButton");
                    Check(button.ActualWidth>=120&&button.ActualHeight>=28,"second instance button clipped");
                    Check(!extra.E<System.Windows.Controls.Button>("BrowseAppButton").IsEnabled,"secondary profile can be rebound");
                }
                return 19;
            } finally {Directory.Delete(root,true);}
        }
        static int Child(string app,string mode="--instance-lease-check") {
            using(var child=Process.Start(new ProcessStartInfo(Platform.Executable,mode+" \""+app+"\"") {UseShellExecute=false,CreateNoWindow=true})) {
                if(!child.WaitForExit(10000)) {child.Kill();throw new Exception("instance lease child timed out");}
                return child.ExitCode;
            }
        }
    }
}
