using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Tappo {
    // Keep the process object open for the entire port-resolution attempt. Never
    // reopen a cached PID and mistake its replacement for the validated process.
    internal sealed class ProcessIdentity : IDisposable {
        readonly SafeProcessHandle handle;
        internal readonly int Id;
        internal readonly long Created;
        internal readonly string Path;
        [DllImport("kernel32.dll",SetLastError=true)] static extern SafeProcessHandle OpenProcess(int access,bool inherit,int pid);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(SafeProcessHandle process,int flags,StringBuilder path,ref int size);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
        ProcessIdentity(SafeProcessHandle handle,int id,long created,string path) {this.handle=handle;Id=id;Created=created;Path=path;}
        internal bool Alive {get {return !handle.IsClosed&&!handle.IsInvalid&&WaitForSingleObject(handle,0)==258;}}
        internal static ProcessIdentity Open(int pid) {
            var handle=OpenProcess(0x101000,false,pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
            if(handle.IsInvalid){handle.Dispose();return null;}
            long created,exited,kernel,user;var path=new StringBuilder(32768);int size=path.Capacity;
            if(!GetProcessTimes(handle,out created,out exited,out kernel,out user)||
                !QueryFullProcessImageName(handle,0,path,ref size)) {handle.Dispose();return null;}
            var identity=new ProcessIdentity(handle,pid,created,path.ToString());
            if(identity.Alive)return identity;
            identity.Dispose();return null;
        }
        public void Dispose() {handle.Dispose();}
    }
}
