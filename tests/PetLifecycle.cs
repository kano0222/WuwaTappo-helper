using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Tappo {
    internal static class PetLifecycleChecks {
        static int checks;
        static void Check(bool pass,string message) {checks++;if(!pass)throw new Exception(message);}
        // A real isolated process and TCP listener, with no game, account or CDP mutations.
        public static int Fixture(string root) {
            var listener=new TcpListener(IPAddress.Loopback,Instances.DebugPort(Environment.CommandLine));listener.Start();
            try {
                File.WriteAllText(Path.Combine(root,"ready"),"ready");
                var until=DateTime.UtcNow.AddSeconds(30);
                while(!File.Exists(Path.Combine(root,"quit"))&&DateTime.UtcNow<until)Thread.Sleep(20);
                return File.Exists(Path.Combine(root,"quit"))?0:3;
            } finally {listener.Stop();}
        }
        static Process Start(string root) {
            Directory.CreateDirectory(root);
            var app=Path.Combine(root,"WuwaTappo.exe");
            if(!File.Exists(app))File.Copy(Platform.Executable,app);
            File.Delete(Path.Combine(root,"quit"));File.Delete(Path.Combine(root,"ready"));
            var child=Process.Start(new ProcessStartInfo(app,"--pet-fixture \""+root+"\" --remote-debugging-port="+Instances.AvailablePort()) {UseShellExecute=false,CreateNoWindow=true});
            try {
                var until=DateTime.UtcNow.AddSeconds(8);
                while(!child.HasExited&&!File.Exists(Path.Combine(root,"ready"))&&DateTime.UtcNow<until)Thread.Sleep(20);
                Check(!child.HasExited&&File.Exists(Path.Combine(root,"ready")),"pet fixture did not start");return child;
            } catch {if(!child.HasExited)child.Kill();child.Dispose();throw;}
        }
        static int Probe(string app) {
            using(var child=Process.Start(new ProcessStartInfo(Platform.Executable,"--instance-lease-check \""+app+"\"") {UseShellExecute=false,CreateNoWindow=true})) {
                if(!child.WaitForExit(8000)) {child.Kill();throw new Exception("lease probe timed out");}
                return child.ExitCode;
            }
        }
        public static int Run() {
            checks=0;
            var root=Path.Combine(Path.GetTempPath(),"tappo-pet-lifecycle-"+Guid.NewGuid().ToString("N"));
            var one=Path.Combine(root,"one");var two=Path.Combine(root,"two");
            var app=Path.Combine(one,"WuwaTappo.exe");var other=Path.Combine(two,"WuwaTappo.exe");
            Process first=null,second=null,restarted=null;
            try {
                first=Start(one);second=Start(two);
                var client=new FakeClient {QuitResult=()=>{File.WriteAllText(Path.Combine(one,"quit"),"quit");return Task.FromResult(0);}};
                using(Instances.Acquire(other)) {
                    using(Instances.Acquire(app)) {
                        Check(Probe(app)==2,"running lease not held");
                        Platform.ClosePet(client,app).GetAwaiter().GetResult();
                        Check(first.WaitForExit(3000)&&first.ExitCode==0&&!Platform.CheckRunningApp(app),"selected pet not closed");
                        Check(!second.HasExited&&Platform.CheckRunningApp(other),"closing selected pet affected another pet");
                    }
                    Check(Probe(app)==0&&Probe(other)==2,"stop did not release only selected lease");
                    restarted=Start(one);
                    Check(Platform.CheckRunningApp(app),"pet could not restart after close");
                    Platform.ClosePet(client,app).GetAwaiter().GetResult();
                    Check(restarted.WaitForExit(3000)&&restarted.ExitCode==0&&client.Quits==2,"restarted pet did not close cleanly");
                }
                return checks;
            } finally {
                foreach(var child in new[]{first,second,restarted})if(child!=null) {if(!child.HasExited){child.Kill();child.WaitForExit();}child.Dispose();}
                if(Directory.Exists(root))Directory.Delete(root,true);
            }
        }
    }
}
