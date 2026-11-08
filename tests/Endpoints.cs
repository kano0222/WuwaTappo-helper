using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Tappo {
    internal static class EndpointChecks {
        static int checks;
        static void Check(bool valid,string message) {if(!valid)throw new Exception(message);checks++;}
        static void Reject(Action action,string message) {
            bool rejected=false;try {action();}catch(InvalidOperationException){rejected=true;}
            Check(rejected,message);
        }
        static Instances.TcpRow Row(int pid,int port,int clientPort=44000,int state=5) {
            return new Instances.TcpRow {pid=pid,localPort=port,remotePort=clientPort,state=state,localAddress=IPAddress.Loopback,remoteAddress=IPAddress.Loopback};
        }
        internal static int Run() {
            checks=0;
            Check(Instances.DebugPort(Platform.AppStartInfo(@"C:\Pet\WuwaTappo.exe",true,58269).Arguments)==58269,"host debug switch not parsed");
            foreach(var args in new[]{null,"--remote-debugging-port=0","--remote-debugging-port=65536","--remote-debugging-port=abc",
                "--remote-debugging-port=42suffix","--remote-debugging-port=12 --remote-debugging-port=13","x--remote-debugging-port=12"})
                Reject(()=>Instances.DebugPort(args),"invalid or ambiguous port accepted");
            var tree=new HashSet<int>{10,11};var row=Row(11,58269);
            Check(Instances.OwnsConnection(new[]{row},tree,58269,44000),"owned established server not accepted");
            Check(!Instances.OwnsConnection(new[]{row},new HashSet<int>{20},58269,44000),"other pet accepted");
            Check(!Instances.OwnsConnection(new[]{row},tree,58269,44001),"another client connection used as proof");
            Check(!Instances.OwnsConnection(new[]{Row(11,44000,58269)},tree,58269,44000),"outbound client row used as server proof");
            Check(!Instances.OwnsConnection(new[]{Row(11,58269,state:8)},tree,58269,44000),"closed connection accepted");
            row.remoteAddress=IPAddress.Parse("192.0.2.1");
            Check(!Instances.OwnsConnection(new[]{row},tree,58269,44000),"remote peer accepted");
            row=Row(11,58269);row.localAddress=IPAddress.Any;
            Check(!Instances.OwnsConnection(new[]{row},tree,58269,44000),"wildcard server proof accepted");
            var port=Instances.AvailablePort();bool probed=false;
            Check(Instances.ResolvePort(tree,()=>new Instances.TcpRow[0],()=>port,(p,t)=>{probed=p==port&&t==tree;return true;})==port&&probed,"missing listener did not use verified fallback");
            Reject(()=>Instances.ResolvePort(tree,()=>new Instances.TcpRow[0],()=>port,(p,t)=>false),"unowned candidate accepted");
            Reject(()=>Instances.ResolvePort(tree,()=>new[]{Row(10,123,state:2),Row(11,124,state:2)},()=>port,(p,t)=>true),"ambiguous listeners bypassed by fallback");
            var pid=Process.GetCurrentProcess().Id;
            Check(!string.IsNullOrEmpty(Instances.CommandLine(pid)),"WMI could not read selected process command line");
            var listener=new TcpListener(IPAddress.Loopback,0);
            try {
                listener.Start();port=((IPEndPoint)listener.LocalEndpoint).Port;
                Check(Instances.ProbePort(port,new HashSet<int>{pid}),"native established owner verification failed");
                Check(!Instances.ProbePort(port,new HashSet<int>{-1}),"native probe accepted wrong process tree");
                // Simulate this incident: only the established server row is visible.
                Check(Instances.ResolvePort(new HashSet<int>{pid},()=>new Instances.TcpRow[0],()=>port,
                    Instances.ProbePort)==port,"listener omission prevented actual owned connection");
            } finally {listener.Stop();}
            Reject(()=>Instances.ResolvePort(new HashSet<int>{pid},()=>new Instances.TcpRow[0],()=>port,
                Instances.ProbePort),"closed endpoint accepted");
            listener=new TcpListener(IPAddress.Any,0);
            try {
                listener.Start();port=((IPEndPoint)listener.LocalEndpoint).Port;
                Reject(()=>Instances.ResolvePort(new HashSet<int>{pid},()=>new Instances.TcpRow[0],()=>port,
                    Instances.ProbePort),"fallback accepted publicly bound endpoint");
            } finally {listener.Stop();}
            return checks;
        }
    }
}
