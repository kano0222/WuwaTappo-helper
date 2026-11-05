using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

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
            return new RuntimeStore(Path.Combine(baseRoot,"runtime","instances",id.ToUpperInvariant()),id.ToUpperInvariant());
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
        static HashSet<int> ProcessTree(int pid) {
            var handle=CreateToolhelp32Snapshot(2,0);
            if(handle==new IntPtr(-1))throw new InvalidOperationException("无法核实桌宠进程树。");
            try {
                var parents=new Dictionary<int,int>();var entry=new Entry {size=(uint)Marshal.SizeOf(typeof(Entry))};
                if(!Process32FirstW(handle,ref entry))throw new InvalidOperationException("无法读取桌宠进程树。");
                do {parents[(int)entry.pid]=(int)entry.parent;}while(Process32NextW(handle,ref entry));
                return Descendants(pid,parents);
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
            var tree=ProcessTree(pid);int size=0;
            GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,3,0);
            var buffer=Marshal.AllocHGlobal(size);
            try {
                if(GetExtendedTcpTable(buffer,ref size,false,2,3,0)!=0)throw new InvalidOperationException("无法核实调试端口归属。");
                var ports=new HashSet<int>();var count=Marshal.ReadInt32(buffer);
                for(int i=0;i<count;i++) {
                    var row=IntPtr.Add(buffer,4+i*24);
                    if(!tree.Contains(Marshal.ReadInt32(row,20)))continue;
                    var address=new IPAddress((long)(uint)Marshal.ReadInt32(row,4));
                    if(!IPAddress.IsLoopback(address))continue;
                    var port=(int)(ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row,8));
                    ports.Add(port);
                }
                if(ports.Count==0)throw new ConnectionFailureException("debug-unavailable","所选桌宠没有本机调试入口；请正常退出该桌宠，再点击启动后台。");
                if(ports.Count!=1)throw new InvalidOperationException("所选桌宠存在多个监听端口，无法确认调试入口。");
                return ports.Single();
            } finally {Marshal.FreeHGlobal(buffer);}
        }
        internal static int AvailablePort() {
            var listener=new TcpListener(IPAddress.Loopback,0);
            try {listener.Start();return ((IPEndPoint)listener.LocalEndpoint).Port;}finally {listener.Stop();}
        }
    }
}
