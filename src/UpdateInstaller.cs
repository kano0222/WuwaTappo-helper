using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Tappo {
    internal sealed class InvalidUpdateFilesException : InvalidOperationException {
        internal InvalidUpdateFilesException(Exception inner) : base("更新文件已失效，请重新下载。",inner) { }
    }
    internal sealed class UpdatePlan { public string root,version; public Dictionary<string,string> hashes; }
    internal static class UpdateInstaller {
        internal const string PackageName="WuwaTappoHelper-win-x64.zip";
        internal static readonly string[] Files={"WuwaTappoHelper.exe","WuwaTappoHelper.exe.config","README.md","config.example.json","LICENSE"};
        const long MaxPackage=16*1024*1024,MaxExpanded=32*1024*1024;
        static string Key(string root) {return Panel.InstanceKey(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar));}
        internal static string EventName(string root) {return "Local\\TappoUpdate-"+Key(root);}
        static string GateName(string root) {return "Local\\TappoUpdateGate-"+Key(root);}
        internal static bool Requested(string root) {
            try {using(var signal=EventWaitHandle.OpenExisting(EventName(root)))return signal.WaitOne(0);}
            catch(WaitHandleCannotBeOpenedException){return false;}
        }
        internal static bool Installing(string root) {
            using(var gate=new Mutex(false,GateName(root))) {
                bool owned=false;
                try {try {owned=gate.WaitOne(0);}catch(AbandonedMutexException){owned=true;}return !owned;}
                finally {if(owned)gate.ReleaseMutex();}
            }
        }
        internal static void ValidateAsset(ReleaseAsset asset,string tag) {
            if(asset==null||asset.state!="uploaded"||asset.name!=PackageName||asset.size<=0||asset.size>MaxPackage||
                asset.browser_download_url!="https://github.com/kano0222/WuwaTappo-helper/releases/download/"+tag+"/"+PackageName||
                asset.digest==null||!System.Text.RegularExpressions.Regex.IsMatch(asset.digest,@"^sha256:[a-fA-F0-9]{64}$"))
                throw new InvalidOperationException("更新包地址、大小或校验信息无效，请稍后重试。");
        }
        internal static string Hash(string file) {
            using(var sha=SHA256.Create())using(var input=File.OpenRead(file))return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();
        }
        internal static Task<string> Download(UpdateInfo info,string root,Action<int> progress,CancellationToken token) {
            return Download(info,root,progress,token,new HttpClientHandler());
        }
        internal static async Task<string> Download(UpdateInfo info,string root,Action<int> progress,CancellationToken token,HttpMessageHandler handler) {
            if(info==null||!info.available)throw new InvalidOperationException("没有可下载的更新。");
            ValidateAsset(info.asset,"v"+info.version);
            var stage=Path.Combine(Path.GetFullPath(root),"runtime","updates",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try {
                var zip=Path.Combine(stage,"package.zip");
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
                using(var http=new HttpClient(handler) {Timeout=TimeSpan.FromMinutes(5)}) {
                    deadline.CancelAfter(TimeSpan.FromMinutes(5));
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("WuwaTappoHelper/"+Updates.DisplayVersion);
                    using(var response=await http.GetAsync(info.asset.browser_download_url,HttpCompletionOption.ResponseHeadersRead,deadline.Token).ConfigureAwait(false)) {
                        response.EnsureSuccessStatusCode();
                        if(response.Content.Headers.ContentLength.HasValue&&response.Content.Headers.ContentLength.Value!=info.asset.size)
                            throw new InvalidOperationException("更新包大小不匹配。");
                        using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using(var output=new FileStream(zip,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                            var buffer=new byte[8192];int count;long total=0;
                            while((count=await input.ReadAsync(buffer,0,buffer.Length,deadline.Token).ConfigureAwait(false))>0) {
                                total+=count;if(total>info.asset.size)throw new InvalidOperationException("更新包大小超出预期。");
                                await output.WriteAsync(buffer,0,count,deadline.Token).ConfigureAwait(false);
                                if(progress!=null)progress((int)(total*100/info.asset.size));
                            }
                            if(total!=info.asset.size)throw new InvalidOperationException("更新包下载不完整。");
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                if(!string.Equals(Hash(zip),info.asset.digest.Substring(7),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("更新包校验失败，请重新下载。");
                Prepare(zip,stage,root,info.version);
                File.Delete(zip);return stage;
            } catch {try {Directory.Delete(stage,true);}catch { }throw;}
        }
        internal static void Prepare(string zip,string stage,string root,string version) {
            var payload=Path.Combine(stage,"payload");Directory.CreateDirectory(payload);
            var hashes=new Dictionary<string,string>(StringComparer.Ordinal);
            using(var archive=ZipFile.OpenRead(zip)) {
                long total=0;
                foreach(var entry in archive.Entries) {
                    if(!Files.Contains(entry.FullName)||hashes.ContainsKey(entry.FullName))throw new InvalidOperationException("更新包包含意外或重复文件。");
                    total+=entry.Length;if(entry.Length<=0||total>MaxExpanded)throw new InvalidOperationException("更新包解压大小异常。");
                    var target=Path.Combine(payload,entry.FullName);
                    using(var input=entry.Open())using(var output=File.Create(target)) {
                        var buffer=new byte[8192];int count;long copied=0;
                        while((count=input.Read(buffer,0,buffer.Length))>0) {copied+=count;if(copied>entry.Length)throw new InvalidOperationException("更新包解压大小异常。");output.Write(buffer,0,count);}
                        if(copied!=entry.Length)throw new InvalidOperationException("更新包文件不完整。");
                    }
                    hashes.Add(entry.FullName,Hash(target));
                }
            }
            if(hashes.Count!=Files.Length)throw new InvalidOperationException("更新包缺少必需文件。");
            if(AssemblyName.GetAssemblyName(Path.Combine(payload,Files[0])).Version.ToString(3)!=version)throw new InvalidOperationException("更新包版本不匹配。");
            RuntimeStore.AtomicWrite(Path.Combine(stage,"plan.json"),new UpdatePlan {root=Path.GetFullPath(root),version=version,hashes=hashes});
        }
        internal static UpdatePlan ValidatePlan(string stage) {
            try {return ReadPlan(stage);}
            catch(Exception error) {throw new InvalidUpdateFilesException(error);}
        }
        static UpdatePlan ReadPlan(string stage) {
            stage=Path.GetFullPath(stage);
            var plan=Json.Decode<UpdatePlan>(RuntimeStore.ReadText(Path.Combine(stage,"plan.json")));
            if(plan==null||plan.root==null||plan.hashes==null||plan.hashes.Count!=Files.Length)throw new InvalidOperationException("更新计划无效。");
            Updates.ParseVersion(plan.version);
            var parent=Path.GetDirectoryName(stage);
            if(!string.Equals(parent,Path.Combine(Path.GetFullPath(plan.root),"runtime","updates"),StringComparison.OrdinalIgnoreCase)||
                !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(stage),"^[a-f0-9]{32}$"))throw new InvalidOperationException("更新目录无效。");
            foreach(var name in Files)if(!plan.hashes.ContainsKey(name)||Hash(Path.Combine(stage,"payload",name))!=plan.hashes[name])throw new InvalidOperationException("已下载文件发生变化，请重新下载。");
            return plan;
        }
        internal static void Launch(string stage) {
            ValidatePlan(stage);
            var runner=Path.Combine(stage,"updater.exe");
            File.Copy(Platform.Executable,runner,true);
            File.Copy(Platform.Executable+".config",runner+".config",true);
            using(var process=Process.Start(new ProcessStartInfo(runner,"--apply-update \""+stage+"\"") {UseShellExecute=true,WorkingDirectory=stage})) {
                if(process==null)throw new InvalidOperationException("更新程序未能启动，请重试。");
            }
        }
        internal static List<RuntimeStore> Stores(string root) {
            var result=new List<RuntimeStore> {new RuntimeStore(root)};
            var instances=Path.Combine(root,"runtime","instances");
            if(Directory.Exists(instances))foreach(var path in Directory.GetDirectories(instances)) {
                var id=Path.GetFileName(path);
                if(System.Text.RegularExpressions.Regex.IsMatch(id,"^[a-fA-F0-9]{64}$"))result.Add(Instances.Open(root,id));
            }
            return result;
        }
        static bool PanelOpen(RuntimeStore store) {
            using(var gate=new Mutex(false,"Local\\TappoPanel-"+Panel.InstanceKey(store.Root))) {
                bool owned=false;
                try {try {owned=gate.WaitOne(0);}catch(AbandonedMutexException){owned=true;}return !owned;}
                finally {if(owned)gate.ReleaseMutex();}
            }
        }
        static bool ProcessesRunning(string root) {
            var exe=Path.Combine(root,Files[0]);
            foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))using(process) {
                var path=Platform.ProcessPath(process.Id);
                if(path==null)throw new InvalidOperationException("无法核实助手进程归属，已停止更新。");
                if(string.Equals(path,exe,StringComparison.OrdinalIgnoreCase))return true;
            }
            return false;
        }
        // Copy only distribution files. Backups remain available if rollback itself fails.
        internal static void Replace(string stage,Action<string,string> copy=null) {
            var plan=ValidatePlan(stage);var backup=Path.Combine(stage,"backup");Directory.CreateDirectory(backup);
            var existed=new HashSet<string>();var touched=new List<string>();
            if(copy==null)copy=(source,target)=>File.Copy(source,target,true);
            foreach(var name in Files)if(File.Exists(Path.Combine(plan.root,name))) {File.Copy(Path.Combine(plan.root,name),Path.Combine(backup,name),true);existed.Add(name);}
            try {
                foreach(var name in Files) {touched.Add(name);copy(Path.Combine(stage,"payload",name),Path.Combine(plan.root,name));}
                foreach(var name in Files)if(Hash(Path.Combine(plan.root,name))!=plan.hashes[name])throw new IOException("更新后文件校验失败。");
            } catch(Exception error) {
                var failures=new List<Exception> {error};
                foreach(var name in touched)try {
                    if(existed.Contains(name))File.Copy(Path.Combine(backup,name),Path.Combine(plan.root,name),true);
                    else File.Delete(Path.Combine(plan.root,name));
                } catch(Exception rollback) {failures.Add(rollback);}
                if(failures.Count>1)throw new AggregateException("恢复旧版未完成，备份保存在："+backup,failures);
                throw new IOException("更新失败，已恢复旧版。",error);
            }
        }
        internal static int Apply(string stage) {
            UpdatePlan plan=null;var restart=new List<RuntimeStore>();bool reopen=false;
            try {
                plan=ValidatePlan(stage);
                using(var gate=new Mutex(false,GateName(plan.root))) {
                    bool owned=false;
                    try {
                        try {owned=gate.WaitOne(0);}catch(AbandonedMutexException){owned=true;}
                        if(!owned)throw new InvalidOperationException("另一个更新正在进行。");
                        using(var signal=new EventWaitHandle(false,EventResetMode.ManualReset,EventName(plan.root))) {
                            restart=Stores(plan.root).Where(PanelOpen).ToList();
                            reopen=true;
                            signal.Set();
                            var until=DateTime.UtcNow.AddSeconds(100);
                            while(ProcessesRunning(plan.root)) {
                                if(DateTime.UtcNow>=until)throw new InvalidOperationException("助手仍有操作或窗口未退出，未替换文件。请等待操作结束后重试。");
                                Thread.Sleep(300);
                            }
                            try {Replace(stage);}catch(AggregateException){reopen=false;throw;}
                            File.WriteAllText(Path.Combine(stage,"completed"),"ok");
                        }
                    } finally {if(owned)gate.ReleaseMutex();}
                }
                return 0;
            } catch(Exception error) {MessageBox.Show(error.Message,"更新未完成",MessageBoxButton.OK,MessageBoxImage.Error);return 1;}
            finally {
                if(reopen)foreach(var store in restart)try {
                    using(var process=Process.Start(new ProcessStartInfo(Path.Combine(plan.root,Files[0]),Instances.Arguments(store)) {UseShellExecute=true,WorkingDirectory=plan.root})) { }
                } catch(Exception error) {MessageBox.Show("请手动打开助手："+error.Message,"重新打开助手失败",MessageBoxButton.OK,MessageBoxImage.Error);}
            }
        }
        internal static void Cleanup(string root) {
            var updates=Path.Combine(root,"runtime","updates");string[] stages;
            try {stages=Directory.Exists(updates)?Directory.GetDirectories(updates):new string[0];}
            catch(IOException){return;}catch(UnauthorizedAccessException){return;}
            foreach(var stage in stages)if(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(stage),"^[a-f0-9]{32}$")&&File.Exists(Path.Combine(stage,"completed"))) {
                // Check before deleting anything: otherwise a locked runner could leave a directory without its completion marker.
                try {
                    if((File.GetAttributes(stage)&FileAttributes.ReparsePoint)!=0)continue;
                    var runner=Path.Combine(stage,"updater.exe");
                    if(File.Exists(runner))using(var unlocked=new FileStream(runner,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { }
                    Directory.Delete(stage,true);
                }catch(IOException){ }catch(UnauthorizedAccessException){ }
            }
        }
    }
}
