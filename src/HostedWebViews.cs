using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Tappo {
    internal static class HostedWebViews {
        [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern IntPtr CommandLineToArgvW(string commandLine,out int count);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);

        internal static bool IsBound(string commandLine,int host) {
            if(string.IsNullOrWhiteSpace(commandLine)||host<=0)return false;
            int count;var memory=CommandLineToArgvW(commandLine,out count);
            if(memory==IntPtr.Zero)throw new InvalidOperationException("无法解析 WebView2 宿主参数。");
            try {
                var args=new string[count];
                for(int i=0;i<count;i++)args[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(memory,i*IntPtr.Size));
                return !args.Any(arg=>arg=="--type"||arg.StartsWith("--type=",StringComparison.Ordinal))&&
                    Switch(args,"embedded-browser-webview")=="1"&&
                    string.Equals(Switch(args,"webview-exe-name"),"WuwaTappo.exe",StringComparison.OrdinalIgnoreCase)&&
                    Regex.IsMatch(Switch(args,"mojo-named-platform-channel-pipe")??"","^"+host+@"\.[1-9]\d*\.\d+$");
            } finally {LocalFree(memory);}
        }
        static string Switch(string[] args,string name) {
            var prefix="--"+name+"=";var values=args.Where(arg=>arg.StartsWith(prefix,StringComparison.Ordinal)).ToArray();
            return values.Length==1?values[0].Substring(prefix.Length):null;
        }
        internal static bool Accept(ProcessIdentity host,ProcessIdentity browser,string commandLine,Func<string,bool> trusted) {
            return host.Alive&&browser.Alive&&browser.Created>=host.Created&&
                string.Equals(Path.GetFileName(host.Path),"WuwaTappo.exe",StringComparison.OrdinalIgnoreCase)&&
                string.Equals(Path.GetFileName(browser.Path),"msedgewebview2.exe",StringComparison.OrdinalIgnoreCase)&&
                IsBound(commandLine,host.Id)&&trusted(browser.Path)&&host.Alive&&browser.Alive;
        }
        internal static ProcessIdentity[] Roots(ProcessIdentity host,Func<string,bool> trusted) {
            var candidates=new Dictionary<int,ProcessIdentity>();var roots=new List<ProcessIdentity>();bool complete=false;
            try {
                // Capture handles BEFORE asking WMI for command lines, then check
                // them again afterwards, so recycled PIDs cannot inherit old args.
                foreach(var process in Process.GetProcessesByName("msedgewebview2"))using(process) {
                    var identity=ProcessIdentity.Open(process.Id);
                    if(identity!=null)candidates.Add(identity.Id,identity);
                }
                using(var search=new ManagementObjectSearcher("SELECT ProcessId,CommandLine FROM Win32_Process WHERE Name='msedgewebview2.exe'")) {
                    search.Options.Timeout=TimeSpan.FromSeconds(2);
                    using(var rows=search.Get())foreach(ManagementObject row in rows)using(row) {
                        var pid=(int)(uint)row["ProcessId"];
                        ProcessIdentity browser;
                        if(candidates.TryGetValue(pid,out browser)&&Accept(host,browser,row["CommandLine"] as string,trusted))roots.Add(browser);
                    }
                }
                complete=true;
            } catch(ManagementException) { } catch(UnauthorizedAccessException) { }
            finally {foreach(var identity in candidates.Values)if(!complete||!roots.Contains(identity))identity.Dispose();}
            if(!complete)return new ProcessIdentity[0];
            return roots.ToArray();
        }
    }
}
