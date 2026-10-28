using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Tappo {
    internal sealed class StartupConfigurationException : Exception {
        public StartupConfigurationException(Exception inner) : base("桌宠启动配置恢复未完成，已停止后台。请核对 runtime/debug-policy-session.json 和启动配置后再启动。",inner) { }
    }
    internal static class Platform {
        internal static void CompleteStartup(Action restore,Action persist) {
            Exception failure=null;
            try {restore();} catch(Exception error) {failure=error;}
            try {persist();} catch(Exception error) {failure=failure==null?error:new AggregateException(failure,error);}
            if(failure!=null)throw new StartupConfigurationException(failure);
        }
        public static string Executable { get { return System.Reflection.Assembly.GetExecutingAssembly().Location; } }
        public static bool Administrator { get { using(var identity=WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } }
        public static bool ValidPath(string path) {
            if(string.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path)||Path.GetPathRoot(path).Length<=1||
                !string.Equals(Path.GetFileName(path),"WuwaTappo.exe",StringComparison.OrdinalIgnoreCase)||!File.Exists(path)) return false;
            try { var info=FileVersionInfo.GetVersionInfo(path); return info.ProductName=="Wuwa Tappo"&&info.FileDescription=="Wuwa Tappo"; } catch { return false; }
        }
        public static string InstallExecutable(string location) {
            if(string.IsNullOrWhiteSpace(location)) return null;
            var path=Environment.ExpandEnvironmentVariables(location.Trim().Trim('"','\''));
            return Path.IsPathRooted(path)&&Path.GetPathRoot(path).Length>1?Path.Combine(path,"WuwaTappo.exe"):null;
        }
        static IEnumerable<string> Candidates(RuntimeStore store) {
            Settings saved=null; try { var path=Path.Combine(store.Root,"config.local.json"); if(File.Exists(path)) saved=Json.Decode<Settings>(RuntimeStore.ReadText(path)); } catch { }
            if(saved!=null) yield return saved.appPath;
            foreach(var process in Process.GetProcessesByName("WuwaTappo")) {
                string path=null; using(process) { try { path=process.MainModule.FileName; } catch { } }
                if(path!=null) yield return path;
            }
            foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine}) foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32}) {
                using(var root=RegistryKey.OpenBaseKey(hive,view)) {
                    using(var key=root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\WuwaTappo.exe"))
                        if(key!=null) yield return key.GetValue("") as string;
                    using(var uninstall=root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall")) {
                        if(uninstall==null) continue;
                        foreach(var child in uninstall.GetSubKeyNames()) using(var key=uninstall.OpenSubKey(child)) {
                            if(key==null||!Regex.IsMatch(key.GetValue("DisplayName","") as string??"",@"wuwa\s*tappo",RegexOptions.IgnoreCase)) continue;
                            yield return InstallExecutable(key.GetValue("InstallLocation") as string);
                            var icon=key.GetValue("DisplayIcon") as string;
                            if(!string.IsNullOrEmpty(icon)) yield return icon.Split(',')[0].Trim('"');
                        }
                    }
                }
            }
            yield return Path.Combine(store.Root,"WuwaTappo.exe"); yield return Path.Combine(store.Root,@"WuwaTappo\WuwaTappo.exe");
            foreach(var root in new[]{Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs")}) {
                if(string.IsNullOrEmpty(root)) continue;
                yield return Path.Combine(root,@"WuwaTappo\WuwaTappo.exe"); yield return Path.Combine(root,@"Wuwa Tappo\WuwaTappo.exe");
            }
        }
        public static string Discover(RuntimeStore store) {
            foreach(var candidate in Candidates(store).Where(p=>!string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
                try { if(ValidPath(candidate)) return Path.GetFullPath(candidate); } catch { }
            return null;
        }
        public static void SavePath(RuntimeStore store,string path) {
            if(!ValidPath(path)) throw new InvalidOperationException("请选择 Wuwa Tappo 安装目录中的 WuwaTappo.exe；文件不存在或应用标识不匹配。");
            RuntimeStore.AtomicWrite(Path.Combine(store.Root,"config.local.json"),new Settings {appPath=path});
        }
        public static string LoadPath(RuntimeStore store) {
            var path=Path.Combine(store.Root,"config.local.json");
            if(!File.Exists(path)) throw new InvalidOperationException("请先在控制面板选择桌宠程序。");
            var settings=Json.Decode<Settings>(RuntimeStore.ReadText(path));
            if(settings==null||!ValidPath(settings.appPath)) throw new InvalidOperationException("桌宠路径无效，请重新选择程序。");
            return settings.appPath;
        }
        public static void CheckListeners() {
            var endpoints=IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(p=>p.Port==19222).ToArray();
            if(endpoints.Length==0) throw new ConnectionFailureException("debug-unavailable","调试入口不存在");
            if(endpoints.Any(p=>!IPAddress.IsLoopback(p.Address))) throw new InvalidOperationException("调试端口并非仅限本机，已停止。");
        }
        public static async Task EnsureDebugSession(RuntimeStore store) {
            var app=LoadPath(store);
            var processes=Process.GetProcessesByName("WuwaTappo"); var running=processes.Length>0;
            foreach(var p in processes) p.Dispose();
            if(running) { CheckListeners(); return; }
            if(!Administrator) throw new InvalidOperationException("启动桌宠连接需要管理员权限。");
            var test=new TcpListener(IPAddress.Loopback,19222); try { test.Start(); } finally { test.Stop(); }
            const string policy=@"Software\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments";
            const string keyName="WuwaTappo.exe", value="--remote-debugging-address=127.0.0.1 --remote-debugging-port=19222";
            using(var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64)) {
                bool existed=false, modified=false; object original=null; RegistryValueKind kind=RegistryValueKind.String;
                var created=new List<string>(); var record=new Dictionary<string,object>{{"startedAt",DateTimeOffset.UtcNow.ToString("o")}};
                try {
                    using(var key=root.OpenSubKey(policy)) if(key!=null&&key.GetValueNames().Contains(keyName)) {
                        existed=true; original=key.GetValue(keyName,null,RegistryValueOptions.DoNotExpandEnvironmentNames); kind=key.GetValueKind(keyName);
                    }
                    string current="";
                    foreach(var part in policy.Split('\\')) { current=current.Length==0?part:current+"\\"+part; using(var key=root.OpenSubKey(current)) if(key==null) created.Add(current); }
                    record["originalPolicy"]=new {exists=existed,value=original,kind=kind.ToString()}; record["createdKeys"]=created; store.Write("debug-policy-session.json",record);
                    using(var key=root.CreateSubKey(policy)) { modified=true; key.SetValue(keyName,value,RegistryValueKind.String); }
                    using(var child=Process.Start(new ProcessStartInfo(app) {WorkingDirectory=Path.GetDirectoryName(app),UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden})) {
                        record["launched"]=true; record["processId"]=child.Id;
                    }
                    var until=RuntimeStore.Now+25000;
                    while(true) {
                        try { CheckListeners(); await AppClient.LocalGet("/json/version",1000).ConfigureAwait(false); record["debugReady"]=true; break; }
                        catch { if(RuntimeStore.Now>=until) throw new InvalidOperationException("桌宠连接未就绪，请检查桌宠状态。"); }
                        await Task.Delay(400).ConfigureAwait(false);
                    }
                } catch(Exception error) { record["error"]=error.Message; throw; }
                finally {
                    CompleteStartup(()=> {try {
                        if(modified) {
                            using(var key=root.OpenSubKey(policy,true)) {
                                if(key==null||!object.Equals(key.GetValue(keyName),value)) throw new InvalidOperationException("启动配置被同时修改，已停止自动覆盖，请核对 debug-policy-session.json。");
                                if(existed) key.SetValue(keyName,original,kind); else key.DeleteValue(keyName,false);
                            }
                            for(int i=created.Count-1;i>=0;i--) {
                                bool empty; using(var key=root.OpenSubKey(created[i])) empty=key!=null&&key.SubKeyCount==0&&key.ValueCount==0;
                                if(empty) root.DeleteSubKey(created[i],false);
                            }
                        }
                        record["policyRestored"]=true;
                    } catch(Exception error) { record["restoreError"]=error.Message; throw; }
                    },()=> { record["finishedAt"]=DateTimeOffset.UtcNow.ToString("o"); store.Write("debug-policy-session.json",record); });
                }
            }
        }
        public static async Task Restore(IAppClient client,RuntimeStore store) {
            if(client==null) throw new InvalidOperationException("没有可用连接；未尝试强制结束桌宠，请手动退出后普通启动。");
            try { await client.Quit().ConfigureAwait(false); } catch { }
            for(int i=0;i<30;i++) {
                bool open=IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p=>p.Port==19222);
                if(!open) {
                    await Task.Delay(1000).ConfigureAwait(false);
                    var app=LoadPath(store); var info=new ProcessStartInfo(app) {WorkingDirectory=Path.GetDirectoryName(app),UseShellExecute=false,WindowStyle=ProcessWindowStyle.Hidden};
                    info.EnvironmentVariables.Remove("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS");
                    using(var child=Process.Start(info)) { if(child==null) throw new InvalidOperationException("普通桌宠启动失败。"); }
                    return;
                }
                await Task.Delay(400).ConfigureAwait(false);
            }
            throw new InvalidOperationException("桌宠未完成正常退出；没有强制结束，请手动退出以关闭调试端口。");
        }
    }
}
