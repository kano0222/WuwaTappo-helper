using System;
using System.Collections.Generic;
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
        internal static int[] Roots(int host) {
            if(!string.Equals(Path.GetFileName(Platform.ProcessPath(host)),"WuwaTappo.exe",StringComparison.OrdinalIgnoreCase))return new int[0];
            var roots=new List<int>();
            try {
                using(var search=new ManagementObjectSearcher("SELECT ProcessId,CommandLine FROM Win32_Process WHERE Name='msedgewebview2.exe'")) {
                    search.Options.Timeout=TimeSpan.FromSeconds(2);
                    using(var rows=search.Get())foreach(ManagementObject row in rows)using(row) {
                        var pid=(int)(uint)row["ProcessId"];
                        if(IsBound(row["CommandLine"] as string,host)&&
                            string.Equals(Path.GetFileName(Platform.ProcessPath(pid)),"msedgewebview2.exe",StringComparison.OrdinalIgnoreCase))roots.Add(pid);
                    }
                }
            } catch(ManagementException) { } catch(UnauthorizedAccessException) { }
            return roots.ToArray();
        }
    }
}
