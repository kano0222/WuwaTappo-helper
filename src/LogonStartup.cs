using System;
using System.IO;
using Microsoft.Win32;

namespace Tappo {
    internal sealed class LogonStartup {
        const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
        internal readonly string Name,Command;
        internal Func<string> ReadValue;
        internal Action<string> WriteValue;
        internal LogonStartup(string executable,RuntimeStore store) {
            executable=Path.GetFullPath(executable);
            Name="WuwaTappoHelper-"+Panel.InstanceKey(store.Root);
            Command="\""+executable+"\""+Instances.Arguments(store);
            ReadValue=()=>{using(var key=Registry.CurrentUser.OpenSubKey(RunKey))return key==null?null:key.GetValue(Name) as string;};
            WriteValue=value=>{
                using(var key=Registry.CurrentUser.CreateSubKey(RunKey)) {
                    if(value==null)key.DeleteValue(Name,false);
                    else key.SetValue(Name,value,RegistryValueKind.String);
                }
            };
        }
        internal bool Enabled {get{return string.Equals(ReadValue(),Command,StringComparison.OrdinalIgnoreCase);}}
        internal void SetEnabled(bool enabled) {
            if(enabled&&Command.Length>260)throw new InvalidOperationException("助手路径过长，无法设置开机自启，请移到较短的目录后重试。");
            WriteValue(enabled?Command:null);
            if(Enabled!=enabled)throw new InvalidOperationException("开机自启设置未能保存，请重试。");
        }
    }
}
