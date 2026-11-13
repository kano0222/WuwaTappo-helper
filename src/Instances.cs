using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Tappo {
    internal static class Instances {
        internal static string Key(string path) {
            using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-","");
        }
        internal static string SavedPath(string root) {
            var file=Path.Combine(root,"config.local.json");
            return File.Exists(file)?Json.Decode<Settings>(RuntimeStore.ReadText(file)).appPath:null;
        }
        internal static RuntimeStore Open(string baseRoot,string id) {
            if(id==null)return new RuntimeStore(baseRoot);
            if(id.Length!=64||id.Any(c=>!Uri.IsHexDigit(c)))throw new InvalidOperationException("实例标识无效。");
            return new RuntimeStore(Path.Combine(baseRoot,"runtime","instances",id.ToUpperInvariant()),id.ToUpperInvariant(),baseRoot);
        }
        internal static string ForPath(string baseRoot,string path) {
            var saved=SavedPath(baseRoot);
            if(saved!=null&&Key(saved)==Key(path))return null;
            return Key(path);
        }
        internal static string Arguments(RuntimeStore store) {
            return store.InstanceId==null?"":" --instance "+store.InstanceId;
        }
        internal static IDisposable LockBindings(string root) {
            var gate=new System.Threading.Mutex(false,"Local\\TappoBindings-"+Panel.InstanceKey(Key(root)));
            bool owned=false;
            try {
                try {owned=gate.WaitOne(5000);}catch(System.Threading.AbandonedMutexException){owned=true;}
                if(!owned)throw new InvalidOperationException("另一个窗口正在保存桌宠绑定，请稍后再试。");
                return new Lease(gate);
            } catch {gate.Dispose();throw;}
        }
        internal static IDisposable Acquire(string app) {
            // Acquired and released on Program.Main while it synchronously waits for the worker.
            var gate=new System.Threading.Mutex(false,"Local\\TappoApp-"+Panel.InstanceKey(Key(app)));
            bool owned=false;
            try {try {owned=gate.WaitOne(0);}catch(System.Threading.AbandonedMutexException){owned=true;}
                if(!owned)throw new InvalidOperationException("这个桌宠已由另一个助手后台接管，请使用对应窗口。");
                return new Lease(gate);
            } catch {gate.Dispose();throw;}
        }
        sealed class Lease : IDisposable {
            readonly System.Threading.Mutex gate;
            public Lease(System.Threading.Mutex gate) {this.gate=gate;}
            public void Dispose() {gate.ReleaseMutex();gate.Dispose();}
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        struct Entry { public uint size,usage,pid; public UIntPtr heap; public uint module,threads,parent; public int priority; public uint flags; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string name; }
        [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool Process32FirstW(IntPtr handle,ref Entry entry);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool Process32NextW(IntPtr handle,ref Entry entry);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,int family,int tableClass,uint reserved);
        internal static HashSet<int> Descendants(int pid,Dictionary<int,int> parents) {
            var result=new HashSet<int>{pid};bool changed;
            do {changed=false;foreach(var pair in parents)if(result.Contains(pair.Value)&&result.Add(pair.Key))changed=true;}while(changed);
            return result;
        }
        static HashSet<int> ProcessTree(int pid,int[] hosted=null) {
            var handle=CreateToolhelp32Snapshot(2,0);
            if(handle==new IntPtr(-1))throw new InvalidOperationException("无法核实桌宠进程树。");
            try {
                var parents=new Dictionary<int,int>();var entry=new Entry {size=(uint)Marshal.SizeOf(typeof(Entry))};
                if(!Process32FirstW(handle,ref entry))throw new InvalidOperationException("无法读取桌宠进程树。");
                do {parents[(int)entry.pid]=(int)entry.parent;}while(Process32NextW(handle,ref entry));
                var tree=Descendants(pid,parents);
                if(hosted!=null&&parents.ContainsKey(pid))foreach(var browser in hosted)
                    if(parents.ContainsKey(browser))tree.UnionWith(Descendants(browser,parents));
                return tree;
            } finally {CloseHandle(handle);}
        }
        internal static int PortFor(string app) {
            var processes=Process.GetProcessesByName("WuwaTappo");
            int pid;
            try {
                var matching=processes.Where(p=>string.Equals(Platform.ProcessPath(p.Id),app,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(matching.Length!=1)throw new ConnectionFailureException("debug-unavailable","所选桌宠尚未运行，或无法确认唯一实例。");
                pid=matching[0].Id;
            } finally {foreach(var process in processes)process.Dispose();}
            return OwnedPort(pid);
        }
        internal static int OwnedPort(int pid) {
            int[] hosted=null;
            Func<HashSet<int>> tree=()=>ProcessTree(pid,hosted);
            return ResolvePort(tree(),ReadTcpRows,()=> {
                // Windows can launch the browser under explorer rather than the
                // host. Its Mojo channel names the actual hosting pet process.
                hosted=HostedWebViews.Roots(pid);
                return DebugPortFor(pid,tree(),CommandLine,
                    child=>string.Equals(Path.GetFileName(Platform.ProcessPath(child)),"msedgewebview2.exe",StringComparison.OrdinalIgnoreCase));
            },(port,ignored)=>ProbePort(port,tree,true));
        }
        internal sealed class TcpRow {
            public int state,localPort,remotePort,pid;
            public IPAddress localAddress,remoteAddress;
        }
        static TcpRow[] ReadTcpRows() {
            // Include established connections: some systems omit the CDP listener.
            int size=0;
            GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,5,0); // AF_INET, OWNER_PID_ALL
            for(int attempt=0;attempt<3;attempt++) {
                var buffer=Marshal.AllocHGlobal(size);
                try {
                    var result=GetExtendedTcpTable(buffer,ref size,false,2,5,0);
                    if(result==122)continue; // Table grew between the size query and read.
                    if(result!=0)throw new InvalidOperationException("无法核实调试端口归属。");
                    var rows=new List<TcpRow>();var count=Marshal.ReadInt32(buffer);
                    for(int i=0;i<count;i++) {
                        var row=IntPtr.Add(buffer,4+i*24);
                        rows.Add(new TcpRow {state=Marshal.ReadInt32(row),localAddress=new IPAddress((long)(uint)Marshal.ReadInt32(row,4)),
                            localPort=(int)(ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row,8)),
                            remoteAddress=new IPAddress((long)(uint)Marshal.ReadInt32(row,12)),
                            remotePort=(int)(ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row,16)),pid=Marshal.ReadInt32(row,20)});
                    }
                    return rows.ToArray();
                } finally {Marshal.FreeHGlobal(buffer);}
            }
            throw new InvalidOperationException("调试端口列表持续变化，请重试。");
        }
        internal static string CommandLine(int pid) {
            using(var search=new ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId="+pid)) {
                search.Options.Timeout=TimeSpan.FromSeconds(2);
                using(var rows=search.Get()) foreach(ManagementObject row in rows) using(row) return row["CommandLine"] as string;
            }
            return null;
        }
        internal static int DebugPort(string commandLine) {
            var matches=Regex.Matches(commandLine??"","(?:^|[\\s\"])--remote-debugging-port=(\\d+)(?=[\\s\"]|$)");
            int port;
            if(matches.Count!=1||!int.TryParse(matches[0].Groups[1].Value,out port)||port<1||port>65535)
                throw new ConnectionFailureException("debug-unavailable","无法从所选桌宠启动参数确认调试端口。");
            return port;
        }
        internal static int DebugPortFor(int pid,HashSet<int> tree,Func<int,string> commandLine,Func<int,bool> isBrowser) {
            var host=CandidatePort(pid,commandLine);
            if(host.HasValue)return host.Value;
            // Elevated hosts can have no readable command line while their WebView2
            // children remain readable. A candidate still needs TCP ownership proof.
            var ports=new HashSet<int>();
            foreach(var child in tree.Where(child=>child!=pid&&isBrowser(child))) {
                var port=CandidatePort(child,commandLine);
                if(port.HasValue)ports.Add(port.Value);
            }
            if(ports.Count!=1)throw new ConnectionFailureException("debug-unavailable",
                ports.Count==0?"无法从所选桌宠及其 WebView2 启动参数确认调试端口。":"所选桌宠的 WebView2 调试端口不唯一。");
            return ports.Single();
        }
        static int? CandidatePort(int pid,Func<int,string> commandLine) {
            try {return DebugPort(commandLine(pid));}
            catch(ConnectionFailureException) {return null;}
            catch(ManagementException) {return null;}
            catch(UnauthorizedAccessException) {return null;}
        }
        internal static bool OwnsConnection(IEnumerable<TcpRow> rows,HashSet<int> tree,int port,int clientPort) {
            return rows.Any(row=>row.state==5&&row.localAddress.Equals(IPAddress.Loopback)&&row.remoteAddress.Equals(IPAddress.Loopback)&&
                row.localPort==port&&row.remotePort==clientPort&&tree.Contains(row.pid));
        }
        internal static bool ProbePort(int port,HashSet<int> tree) {
            return ProbePort(port,()=>tree);
        }
        static bool ProbePort(int port,Func<HashSet<int>> tree,bool diagnose=false) {
            using(var client=new TcpClient(AddressFamily.InterNetwork)) {
                var connect=client.ConnectAsync(IPAddress.Loopback,port);
                if(!connect.Wait(1000)) {
                    if(diagnose)throw new InvalidOperationException("本机调试端口连接超时（端口 "+port+"）。");
                    return false;
                }
                var clientPort=((IPEndPoint)client.Client.LocalEndPoint).Port;
                if(WaitForOwner(port,clientPort,ReadTcpRows,tree,System.Threading.Thread.Sleep))return true;
                if(!diagnose)return false;
                var conflict=ForeignServer(ReadTcpRows(),tree(),port);
                if(conflict!=null)throw new InvalidOperationException(ServerConflict(port,conflict));
                throw new InvalidOperationException("未取得可核实的服务端连接记录（端口 "+port+"）。");
            }
        }
        static TcpRow ForeignServer(IEnumerable<TcpRow> rows,HashSet<int> tree,int port) {
            return rows.FirstOrDefault(row=>(row.state==2||row.state==5)&&row.localPort==port&&
                (!row.localAddress.Equals(IPAddress.Loopback)||!tree.Contains(row.pid)));
        }
        static string ServerConflict(int port,TcpRow row) {
            return "系统 TCP 记录与所选桌宠不匹配（端口 "+port+"，进程 "+row.pid+"，状态 "+row.state+"，"+
                (row.localAddress.Equals(IPAddress.Loopback)?"回环地址":"非回环地址")+"）。";
        }
        internal static bool WaitForOwner(int port,int clientPort,Func<TcpRow[]> read,Func<HashSet<int>> tree,Action<int> delay) {
            for(int attempt=0;attempt<5;attempt++) {
                var currentTree=tree();
                if(OwnsConnection(read(),currentTree,port,clientPort))return true;
                if(attempt<4)delay(20);
            }
            return false;
        }
        internal static int ResolvePort(HashSet<int> tree,Func<TcpRow[]> read,Func<int> candidate,Func<int,HashSet<int>,bool> probe) {
            var rows=read();
            var ports=new HashSet<int>(rows.Where(row=>row.state==2&&tree.Contains(row.pid)&&IPAddress.IsLoopback(row.localAddress)).Select(row=>row.localPort));
            if(ports.Count>1)throw new InvalidOperationException("所选桌宠存在多个监听端口，无法确认调试入口。");
            if(ports.Count==1) {
                var port=ports.Single();Platform.CheckListeners(port,true);return port;
            }
            int fallback;
            try {
                fallback=candidate();
                Platform.CheckListeners(fallback,true);
                if(!probe(fallback,tree))throw new InvalidOperationException("未能核实调试端口的服务端进程归属。");
            } catch(Exception error) {
                throw new ConnectionFailureException("debug-unavailable","所选桌宠调试入口验证失败："+error.Message);
            }
            return fallback;
        }
        internal static int AvailablePort() {
            var listener=new TcpListener(IPAddress.Loopback,0);
            try {listener.Start();return ((IPEndPoint)listener.LocalEndpoint).Port;}finally {listener.Stop();}
        }
    }
}
