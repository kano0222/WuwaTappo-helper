using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Tappo {
    internal static class AutoRunChecks {
        static int checks;
        static void Check(bool pass,string message) {checks++;if(!pass)throw new Exception(message);}
        public static int Run() {
            checks=0;
            var root=Path.Combine(Path.GetTempPath(),"tappo-auto-run-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                // A tiny executable with the expected metadata; never run it or start a real worker.
                var source=Path.Combine(root,"pet.cs");var pet=Path.Combine(root,"WuwaTappo.exe");
                File.WriteAllText(source,"[assembly:System.Reflection.AssemblyProduct(\"Wuwa Tappo\")][assembly:System.Reflection.AssemblyTitle(\"Wuwa Tappo\")]class Pet {static void Main(){}}");
                var compiler=Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),"csc.exe");
                using(var process=Process.Start(new ProcessStartInfo(compiler,"/nologo /target:winexe /out:\""+pet+"\" \""+source+"\"") {UseShellExecute=false,CreateNoWindow=true})) {
                    if(!process.WaitForExit(10000)||process.ExitCode!=0)throw new Exception("auto-run pet metadata fixture did not compile");
                }
                Check(Platform.ValidPath(pet),"fixture path is invalid");
                LauncherConflict(root,pet);
                var freshRoot=Path.Combine(root,"first-open");Directory.CreateDirectory(freshRoot);
                File.Copy(pet,Path.Combine(freshRoot,"WuwaTappo.exe"));
                var fresh=new RuntimeStore(freshRoot);fresh.Write("preferences.json",new Preference {mode="clicker",autoRun=true});
                using(var panel=new Panel(fresh,true)) {
                    panel.InitializeTray();panel.Window.Show();
                    int calls=0;string command=null;panel.DispatchOverride=action=>{calls++;command=action;panel.ReadStatus=()=>new Status {running=true,pid=Process.GetCurrentProcess().Id,connected=true,at=DateTimeOffset.UtcNow.ToString("o")};};
                    Check(Instances.SavedPath(fresh.Root)==null&&!fresh.Exists("status.json"),"first-open fixture already has a saved path or run history");
                    panel.AutoRunOnOpen().GetAwaiter().GetResult();panel.AutoRunOnOpen().GetAwaiter().GetResult();
                    Check(calls==1&&command=="start","first automatic run requires a previous manual run despite discovering the pet");
                    Check(!panel.Window.IsVisible&&!panel.Window.ShowInTaskbar,"successful first automatic start did not hide to tray");
                }
                var store=new RuntimeStore(root);Platform.SavePath(store,pet);
                using(var panel=new Panel(store,true)) {
                    int calls=0;panel.DispatchOverride=action=>calls++;
                    panel.InitializeTray();
                    Check(!panel.TrayAutoRun.Checked&&panel.TrayAutoRun.Text=="自动运行","tray auto-run defaults on or has wrong text");
                    Check(panel.E<CheckBox>("AutoRunCheckBox").IsChecked==false,"auto-run defaults on");
                    panel.AutoRunOnOpen().GetAwaiter().GetResult();Check(calls==0,"disabled setting ran a worker");
                    panel.E<CheckBox>("AutoRunCheckBox").IsChecked=true;
                    panel.E<CheckBox>("AutoRunCheckBox").RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
                    Check(calls==0&&store.Read<Preference>("preferences.json").autoRun,"switch did not save or started immediately");
                    Check(panel.TrayAutoRun.Checked,"panel toggle did not update tray");
                    panel.TrayAutoRun.PerformClick();
                    Check(panel.E<CheckBox>("AutoRunCheckBox").IsChecked==false&&!store.Read<Preference>("preferences.json").autoRun&&calls==0,"tray toggle did not save, update panel or started immediately");
                    panel.TrayAutoRun.PerformClick();
                    Check(panel.E<CheckBox>("AutoRunCheckBox").IsChecked==true&&panel.TrayAutoRun.Checked&&calls==0,"tray toggle did not enable panel without running");
                    panel.E<ListBox>("ModeSelector").SelectedIndex=2;
                    Check(store.Read<Preference>("preferences.json").autoRun&&store.Read<Preference>("preferences.json").mode=="human","mode change lost auto-run");
                    Check(!store.Exists("control.json"),"saving settings wrote a worker command");
                }
                using(var panel=new Panel(store,true)) {
                    int calls=0;string command=null;panel.DispatchOverride=action=>{calls++;command=action;};
                    panel.InitializeTray();Check(panel.TrayAutoRun.Checked,"reopened tray lost saved setting");
                    Check(panel.E<CheckBox>("AutoRunCheckBox").IsChecked==true,"setting not restored");
                    panel.AutoRunOnOpen().GetAwaiter().GetResult();panel.AutoRunOnOpen().GetAwaiter().GetResult();
                    Check(calls==1&&command=="start","opening did not start once or reopening from tray ran again");
                    panel.SetAutoRun(false);var settings=store.Read<Preference>("preferences.json");
                    Check(!settings.autoRun&&settings.mode=="human"&&settings.reserveTarget==2000&&settings.clickerRate==100,"toggle overwrote mode parameters");
                }
                foreach(var scenario in new[]{"running","paused","pending","stale","failure","disconnected","fault","retry"}) {
                    store.Write("preferences.json",new Preference {mode="clicker",autoRun=true});
                    using(var panel=new Panel(store,true)) {
                        var now=RuntimeStore.Now;
                        var status=new Status {running=true,pid=Process.GetCurrentProcess().Id,at=DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("o"),connected=true};
                        if(new[]{"paused","pending","stale","failure"}.Contains(scenario)) {status.paused=true;status.manuallyPaused=true;}
                        if(scenario=="disconnected")status.connected=false;
                        if(scenario=="fault")status.fault="需要处理";
                        if(scenario=="retry")status.readRetryAt=now+10000;
                        if(scenario=="stale")status.at=DateTimeOffset.FromUnixTimeMilliseconds(now-60000).ToString("o");
                        if(scenario=="pending")store.Write("pending.json",new {historical=true});
                        panel.ReadClock=()=>now;panel.ReadStatus=()=>status;
                        int calls=0;string command=null;
                        panel.InitializeTray();panel.Window.Show();
                        panel.DispatchOverride=action=>{calls++;command=action;if(scenario=="failure")throw new InvalidOperationException("启动未获允许");status.paused=false;status.manuallyPaused=false;};
                        panel.AutoRunOnOpen().GetAwaiter().GetResult();panel.AutoRunOnOpen().GetAwaiter().GetResult();
                        var shouldRun=scenario=="paused"||scenario=="failure";
                        Check(calls==(shouldRun?1:0)&&(!shouldRun||command=="resume"),"auto-run bypassed existing state: "+scenario);
                        var hidden=scenario=="running"||scenario=="paused";
                        Check(panel.Window.IsVisible!=hidden&&panel.Window.ShowInTaskbar!=hidden,"auto tray visibility incorrect: "+scenario);
                        if(hidden) {panel.Show();panel.AutoRunOnOpen().GetAwaiter().GetResult();panel.TryAutoTray();Check(panel.Window.IsVisible,"restored panel automatically hid again");}
                        if(scenario=="pending")Check(store.Exists("pending.json"),"auto-run cleared pending confirmation");
                        if(scenario=="failure")Check(panel.E<TextBlock>("DetailMessage").Text=="启动未获允许","auto-run failure hidden or retried");
                    }
                    store.ClearPending();
                }
                StartupUpdate(root);
                var missing=new RuntimeStore(Path.Combine(root,"missing"),"missing-pet");missing.Write("preferences.json",new Preference {mode="clicker",autoRun=true});
                using(var panel=new Panel(missing,true)) {
                    int calls=0;panel.DispatchOverride=action=>calls++;panel.AutoRunOnOpen().GetAwaiter().GetResult();
                    panel.InitializeTray();
                    Check(calls==0&&panel.E<TextBlock>("DetailMessage").Text.Contains("请先选择桌宠程序"),"missing path opened selection or started a worker");
                    File.Delete(missing.FilePath("preferences.json"));Directory.CreateDirectory(missing.FilePath("preferences.json"));
                    panel.TrayAutoRun.PerformClick();
                    Check(panel.E<CheckBox>("AutoRunCheckBox").IsChecked==true&&panel.TrayAutoRun.Checked&&panel.E<TextBlock>("DetailMessage").Text.Contains("未能保存"),"failed settings write changed a switch");
                }
                return checks;
            } finally {Directory.Delete(root,true);}
        }
        static void StartupUpdate(string root) {
            foreach(var scenario in new[]{"current","offline","available","available-mini"}) {
                var store=new RuntimeStore(Path.Combine(root,scenario));
                store.Write("preferences.json",new Preference {autoRun=true});
                bool mini=scenario=="available-mini",available=scenario.StartsWith("available");
                if(mini)store.Write("panel-mini.json",true);
                using(var panel=new Panel(store)) {
                    var waiting=new TaskCompletionSource<UpdateInfo>();
                    var identity=RuntimeIdentity.Create(store,"worker");
                    var status=new Status {running=true,pid=identity.pid,identity=identity,connected=true,at=DateTimeOffset.UtcNow.ToString("o"),
                        paused=mini,manuallyPaused=mini};
                    int checksStarted=0,resumes=0;
                    panel.AutoCheckUpdates=true;
                    panel.UpdateCheck=token=>{checksStarted++;return waiting.Task;};
                    panel.ReadStatus=()=>status;
                    panel.DispatchOverride=action=>{
                        Check(action=="resume"&&checksStarted==1,"startup update delayed resume or started too late");
                        resumes++;status.paused=false;status.manuallyPaused=false;
                    };
                    panel.InitializeTray();panel.Window.Show();
                    panel.Window.Dispatcher.Invoke(new Action(()=>{}),DispatcherPriority.ApplicationIdle);
                    panel.TryAutoTray();
                    Check(checksStarted==1&&resumes==(mini?1:0)&&panel.Window.IsVisible&&panel.Window.ShowInTaskbar,
                        "automatic tray did not wait for the startup update check: "+scenario);
                    if(scenario=="offline")waiting.SetException(new System.Net.Http.HttpRequestException("offline"));
                    else waiting.SetResult(new UpdateInfo {version="9.8.7",available=available});
                    panel.Window.Dispatcher.Invoke(new Action(()=>{}),DispatcherPriority.ApplicationIdle);
                    Check(panel.Window.IsVisible==available&&panel.Window.ShowInTaskbar==available,
                        "startup update result did not determine automatic tray visibility: "+scenario);
                    if(available) {
                        var button=panel.E<Button>(mini?"MiniUpdateButton":"UpdateButton");
                        Check(button.IsVisible&&button.IsEnabled&&button.Content.ToString().Contains("下载新版"),"new version action is not visible: "+scenario);
                        if(mini) {
                            panel.UpdateDownload=(info,progress,token)=>Task.FromResult("isolated-stage");
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            Check(button.Content.ToString().Contains("重启更新"),"mini update action did not use the shared download flow");
                        }
                    }
                }
            }
        }
        static void LauncherConflict(string root,string pet) {
            var store=new RuntimeStore(Path.Combine(root,"launcher-conflict"));Platform.SavePath(store,pet);
            var exe=Path.Combine(store.Root,"WuwaTappoHelper.exe");
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"WuwaTappoHelper.exe"),exe);
            File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"WuwaTappoHelper.exe.config"),exe+".config");
            using(Instances.Acquire(pet))
            using(var child=Process.Start(new ProcessStartInfo(exe,"--worker") {UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=store.Root})) {
                Check(child.WaitForExit(10000)&&child.ExitCode==1,"launcher did not reject an occupied fixture pet");
            }
            var failure=store.Read<LauncherStatus>("launcher-status.json");
            Check(failure!=null&&failure.error.Contains("已由另一个助手后台接管")&&!store.Exists("status.json"),"launcher lost the conflict reason or ran a worker");
            var now=RuntimeStore.Now;
            store.Write("status.json",new Status {pid=int.MaxValue,at=DateTimeOffset.FromUnixTimeMilliseconds(now-60000).ToString("o"),claimsThisRun=7});
            using(var panel=new Panel(store,preview:true)) {
                panel.ReadClock=()=>now;panel.Refresh();
                Check(panel.E<TextBlock>("MainMessage").Text=="后台已退出","conflict fixture does not start from an exited worker");
                int starts=0;
                panel.DispatchOverride=action=>{starts++;throw new InvalidOperationException(failure.error);};
                panel.E<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                now+=30000;panel.Refresh();
                Check(starts==1&&panel.E<TextBlock>("StatusLabel").Text=="启动失败"&&panel.E<TextBlock>("DetailMessage").Text==failure.error&&
                    panel.E<TextBlock>("MiniDetailMessage").Text==failure.error&&panel.E<TextBlock>("ClaimsValue").Text=="7",
                    "old worker history or the notice timeout hid the current launch failure");
            }
        }
    }
}
