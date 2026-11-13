using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Tappo {
    internal static class HostedWebViewChecks {
        static int checks;
        static void Check(bool value,string message) {if(!value)throw new Exception(message);checks++;}
        static string Args(int host,int port) {
            return "--embedded-browser-webview=1 --webview-exe-name=WuwaTappo.exe --mojo-named-platform-channel-pipe="+host+".99.123456 --remote-debugging-port="+port;
        }
        internal static int Fixture(string[] args) {
            TcpListener listener=null;
            try {
                if(args.Contains("--listener")) {
                    listener=new TcpListener(IPAddress.Loopback,Instances.DebugPort(Environment.CommandLine));listener.Start();
                }
                File.WriteAllText(Path.Combine(args[1],"ready"),"ready");
                var until=DateTime.UtcNow.AddSeconds(30);
                while(!File.Exists(Path.Combine(args[1],"quit"))&&DateTime.UtcNow<until)Thread.Sleep(20);
                return 0;
            } finally {if(listener!=null)listener.Stop();}
        }
        static Process Start(string root,string name,string arguments) {
            Directory.CreateDirectory(root);var exe=Path.Combine(root,name);File.Copy(Platform.Executable,exe);
            var child=Process.Start(new ProcessStartInfo(exe,"--hosted-webview-fixture \""+root+"\" "+arguments) {UseShellExecute=false,CreateNoWindow=true});
            try {
                var until=DateTime.UtcNow.AddSeconds(8);
                while(!child.HasExited&&!File.Exists(Path.Combine(root,"ready"))&&DateTime.UtcNow<until)Thread.Sleep(20);
                if(child.HasExited||!File.Exists(Path.Combine(root,"ready")))throw new Exception("hosted WebView fixture failed to start");
                return child;
            } catch {if(!child.HasExited){child.Kill();child.WaitForExit();}child.Dispose();throw;}
        }
        internal static int Run() {
            checks=0;
            const int hostPid=1001,debugPort=58269;
            var args="msedgewebview2.exe "+Args(hostPid,debugPort);
            Check(HostedWebViews.IsBound(args,hostPid),"broker-launched browser rejected");
            Check(!HostedWebViews.IsBound(args,hostPid+1),"another host's browser accepted");
            Check(!HostedWebViews.IsBound(args+" --type=renderer",hostPid),"renderer accepted as browser root");
            Check(!HostedWebViews.IsBound(args.Replace("WuwaTappo.exe","Other.exe"),hostPid),"another WebView app accepted");
            Check(!HostedWebViews.IsBound("msedgewebview2.exe "+Args(hostPid*10,debugPort),hostPid),"partial host PID accepted");
            Check(!HostedWebViews.IsBound(args+" --mojo-named-platform-channel-pipe="+hostPid+".88.9",hostPid),"ambiguous host binding accepted");
            Check(!HostedWebViews.IsBound("msedgewebview2.exe --user-agent=\""+Args(hostPid,debugPort)+"\"",hostPid),"binding inside another argument accepted");
            Check(HostedWebViews.IsBound(args.Replace("="+hostPid+".99.123456","=\""+hostPid+".99.123456\""),hostPid),"quoted host binding rejected");

            var root=Path.Combine(Path.GetTempPath(),"tappo-hosted-webview-"+Guid.NewGuid().ToString("N"));
            var hostRoot=Path.Combine(root,"host");var browserRoot=Path.Combine(root,"browser");var otherRoot=Path.Combine(root,"other-host");
            Process host=null,browser=null,other=null;
            try {
                var port=Instances.AvailablePort();
                host=Start(hostRoot,"WuwaTappo.exe","--remote-debugging-port="+port);
                // Both processes are launched by this test, reproducing a browser
                // outside the pet's parent/child tree, as in the supplied report.
                browser=Start(browserRoot,"msedgewebview2.exe",Args(host.Id,port)+" --listener");
                Check(!Instances.ProbePort(port,new HashSet<int>{host.Id}),"fixture unexpectedly passed parent-tree-only ownership");
                Check(HostedWebViews.Roots(host.Id).Contains(browser.Id),"native WMI host binding not discovered");
                Check(Instances.OwnedPort(host.Id)==port,"broker-launched owned browser could not connect");
                other=Start(otherRoot,"WuwaTappo.exe","");
                Check(!HostedWebViews.Roots(other.Id).Contains(browser.Id),"browser attached to another pet's process");
            } finally {
                foreach(var folder in new[]{hostRoot,browserRoot,otherRoot})if(Directory.Exists(folder))File.WriteAllText(Path.Combine(folder,"quit"),"quit");
                foreach(var child in new[]{host,browser,other})if(child!=null) {if(!child.WaitForExit(3000)){child.Kill();child.WaitForExit();}child.Dispose();}
                if(Directory.Exists(root))Directory.Delete(root,true);
            }
            return checks;
        }
    }
}
