using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tappo {
    internal sealed class PackageHandler : HttpMessageHandler {
        internal byte[] Data; internal HttpStatusCode Code=HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
            token.ThrowIfCancellationRequested();
            if(request.RequestUri.Scheme!="https"||request.Headers.Authorization!=null)throw new Exception("unexpected package request");
            return Task.FromResult(new HttpResponseMessage(Code) {Content=new ByteArrayContent(Data)});
        }
    }
    internal static class InstallerChecks {
        static int checks;
        static void Check(bool value,string message) {if(!value)throw new Exception(message);checks++;}
        static void Reject(Action action,string message) {bool rejected=false;try {action();}catch {rejected=true;}Check(rejected,message);}
        static string Stage(string root) {var stage=Path.Combine(root,"runtime","updates",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);return stage;}
        static void Package(string path,string bad=null) {
            using(var zip=ZipFile.Open(path,ZipArchiveMode.Create)) {
                foreach(var name in UpdateInstaller.Files) {
                    if(bad=="missing"&&name=="LICENSE")continue;
                    using(var output=zip.CreateEntry(name).Open()) {
                        var data=name=="WuwaTappoHelper.exe"?File.ReadAllBytes(typeof(Updates).Assembly.Location):
                            name=="WuwaTappoHelper.exe.config"?File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,name)):Encoding.UTF8.GetBytes("new "+name);
                        output.Write(data,0,data.Length);
                    }
                }
                if(bad!=null&&bad!="missing")using(var writer=new StreamWriter(zip.CreateEntry(bad).Open()))writer.Write("unexpected");
            }
        }
        static async Task WaitFor(Func<bool> condition,string message) {
            var until=DateTime.UtcNow.AddSeconds(25);
            while(!condition()) {if(DateTime.UtcNow>=until)throw new Exception(message);await Task.Delay(50).ConfigureAwait(false);}
        }
        static async Task CrossProcess(string root,string zip) {
            var install=Path.Combine(root,"cross-process");Directory.CreateDirectory(install);
            File.Copy(typeof(Updates).Assembly.Location,Path.Combine(install,"WuwaTappoHelper.exe"));
            // Launch() copies the caller's runtime config along with its updater executable.
            var callerConfig=typeof(Updates).Assembly.Location+".config";
            if(!File.Exists(callerConfig))File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"WuwaTappoHelper.exe.config"),callerConfig);
            File.Copy(callerConfig,Path.Combine(install,"WuwaTappoHelper.exe.config"));
            File.WriteAllText(Path.Combine(install,"README.md"),"old readme");File.WriteAllText(Path.Combine(install,"update-test-mode"),"isolated");
            var primary=new RuntimeStore(install);var id=new string('B',64);var secondary=Instances.Open(install,id);
            primary.Write("pending.json",new {keep=true});secondary.Write("pending.json",new {keep=true});
            var stage=Stage(install);UpdateInstaller.Prepare(zip,stage,install,Updates.CurrentVersion);
            using(var first=Process.Start(new ProcessStartInfo(Path.Combine(install,"WuwaTappoHelper.exe"),"--update-participant primary") {UseShellExecute=false,CreateNoWindow=true}))
            using(var second=Process.Start(new ProcessStartInfo(Path.Combine(install,"WuwaTappoHelper.exe"),"--update-participant "+id) {UseShellExecute=false,CreateNoWindow=true})) {
                try {
                    await WaitFor(()=>primary.Exists("update-ready.json")&&secondary.Exists("update-ready.json"),"participants not ready").ConfigureAwait(false);
                    UpdateInstaller.Launch(stage);
                    await WaitFor(()=>primary.Exists("update-restarted.json")&&secondary.Exists("update-restarted.json"),"updater did not reopen both instances").ConfigureAwait(false);
                    Check(first.HasExited&&second.HasExited&&primary.Exists("update-stopped.json")&&secondary.Exists("update-stopped.json"),"files replaced before participants exited");
                    Check(File.Exists(Path.Combine(stage,"completed"))&&File.ReadAllText(Path.Combine(install,"README.md"))=="new README.md","cross-process installation incomplete");
                    Check(primary.Exists("pending.json")&&secondary.Exists("pending.json"),"cross-process update lost pending journals");
                    await WaitFor(()=>!Directory.Exists(stage)||CanOpenExecutable(stage,"updater.exe"),"updater still running").ConfigureAwait(false);
                    await WaitFor(()=>CanOpenExecutable(install,"WuwaTappoHelper.exe"),"restarted fixtures have not released the installed executable").ConfigureAwait(false);
                } finally {
                    // These are test-owned inert participants only; never terminate a real helper or pet.
                    if(!first.HasExited)first.Kill();if(!second.HasExited)second.Kill();first.WaitForExit();second.WaitForExit();
                }
            }
        }
        static bool CanOpenExecutable(string directory,string name) {
            try {using(var file=new FileStream(Path.Combine(directory,name),FileMode.Open,FileAccess.ReadWrite,FileShare.None))return true;}
            catch(IOException){return false;}
            catch(UnauthorizedAccessException){return false;}
        }
        static void DeleteFixture(string root) {
            var until=DateTime.UtcNow.AddSeconds(5);
            while(true) {
                try {Directory.Delete(root,true);return;}
                catch(IOException) {if(DateTime.UtcNow>=until)throw;}
                catch(UnauthorizedAccessException) {if(DateTime.UtcNow>=until)throw;}
                Thread.Sleep(50);
            }
        }
        public static async Task<int> Run() {
            checks=0;var root=Path.Combine(Path.GetTempPath(),"tappo-installer-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            Exception failure=null;
            try {
                var zip=Path.Combine(root,"package.zip");Package(zip);
                var asset=new ReleaseAsset {name=UpdateInstaller.PackageName,state="uploaded",size=new FileInfo(zip).Length,digest="sha256:"+UpdateInstaller.Hash(zip),browser_download_url="https://github.com/kano0222/WuwaTappo-helper/releases/download/v"+Updates.CurrentVersion+"/"+UpdateInstaller.PackageName};
                var info=new UpdateInfo {version=Updates.CurrentVersion,available=true,asset=asset};
                string json=Json.Encode(new ReleaseInfo {tag_name="v"+Updates.CurrentVersion,html_url="https://github.com/kano0222/WuwaTappo-helper/releases/tag/v"+Updates.CurrentVersion,assets=new[]{asset}});
                Check(Updates.Parse(json,"0.0.0").asset.digest==asset.digest,"asset not parsed");
                Reject(()=>Updates.Parse(json.Replace("sha256:","md5:"),"0.0.0"),"bad asset digest accepted");
                Reject(()=>Updates.Parse(json.Replace("/releases/download/","/other/"),"0.0.0"),"foreign asset URL accepted");
                Reject(()=>Updates.Parse(json.Replace("\"uploaded\"","\"new\""),"0.0.0"),"incomplete asset accepted");
                var bytes=File.ReadAllBytes(zip);int progress=0;
                var stage=await UpdateInstaller.Download(info,root,value=>progress=value,CancellationToken.None,new PackageHandler {Data=bytes}).ConfigureAwait(false);
                Check(progress==100&&UpdateInstaller.ValidatePlan(stage).hashes.Count==5,"download not verified and staged");
                Check(!File.Exists(Path.Combine(root,"WuwaTappoHelper.exe")),"download changed installed program");
                var pending=Path.Combine(root,"runtime","pending.json");File.WriteAllText(pending,"preserve journal");
                var config=Path.Combine(root,"config.local.json");File.WriteAllText(config,"preserve config");
                var id=new string('A',64);var extra=Instances.Open(root,id);extra.Write("preferences.json",new Preference {mode="human"});extra.Write("pending.json",new {keep=true});
                foreach(var name in UpdateInstaller.Files)File.WriteAllText(Path.Combine(root,name),"old "+name);
                int copies=0;
                Reject(()=>UpdateInstaller.Replace(stage,(source,target)=>{File.Copy(source,target,true);if(++copies==2)throw new IOException("injected partial copy failure");}),"copy failure not reported");
                Check(UpdateInstaller.Files.All(name=>File.ReadAllText(Path.Combine(root,name))=="old "+name),"rollback did not restore old files");
                Check(File.ReadAllText(pending)=="preserve journal"&&File.ReadAllText(config)=="preserve config"&&extra.Exists("pending.json"),"rollback modified runtime");
                File.Delete(Path.Combine(root,"LICENSE"));copies=0;
                Reject(()=>UpdateInstaller.Replace(stage,(source,target)=>{File.Copy(source,target,true);if(++copies==5)throw new IOException("new file failure");}),"new-file failure not reported");
                Check(!File.Exists(Path.Combine(root,"LICENSE")),"rollback retained newly created file");
                UpdateInstaller.Replace(stage);
                Check(UpdateInstaller.Files.All(name=>UpdateInstaller.Hash(Path.Combine(root,name))==UpdateInstaller.Hash(Path.Combine(stage,"payload",name))),"installation hashes differ");
                Check(File.ReadAllText(pending)=="preserve journal"&&File.ReadAllText(config)=="preserve config"&&extra.Read<Preference>("preferences.json").mode=="human"&&extra.Exists("pending.json"),"installation changed user data");
                Check(UpdateInstaller.Stores(root).Count==2,"second instance missing from coordination");
                File.AppendAllText(Path.Combine(stage,"payload","README.md"),"tamper");
                bool invalidFiles=false;try {UpdateInstaller.ValidatePlan(stage);}catch(InvalidUpdateFilesException){invalidFiles=true;}
                Check(invalidFiles,"staged tampering not classified for redownload");
                invalidFiles=false;try {UpdateInstaller.ValidatePlan(Path.Combine(root,"deleted-stage"));}catch(InvalidUpdateFilesException){invalidFiles=true;}
                Check(invalidFiles,"missing stage not classified for redownload");
                foreach(var bad in new[]{"missing","../config.local.json","runtime/pending.json","LICENSE"}) {
                    var malformed=Path.Combine(root,Guid.NewGuid().ToString("N")+".zip");Package(malformed,bad);
                    Reject(()=>UpdateInstaller.Prepare(malformed,Stage(root),root,Updates.CurrentVersion),"invalid archive accepted: "+bad);
                }
                Reject(()=>UpdateInstaller.Prepare(zip,Stage(root),root,"99.0.0"),"wrong executable version accepted");
                var originalDigest=asset.digest;asset.digest="sha256:"+new string('0',64);
                bool rejected=false;try {await UpdateInstaller.Download(info,root,null,CancellationToken.None,new PackageHandler {Data=bytes}).ConfigureAwait(false);}catch(InvalidOperationException){rejected=true;}
                Check(rejected,"corrupt download accepted");asset.digest=originalDigest;
                rejected=false;try {await UpdateInstaller.Download(info,root,null,CancellationToken.None,new PackageHandler {Data=bytes.Take(10).ToArray()}).ConfigureAwait(false);}catch(InvalidOperationException){rejected=true;}
                Check(rejected,"truncated download accepted");
                using(var cancelled=new CancellationTokenSource()) {
                    cancelled.Cancel();rejected=false;
                    try {await UpdateInstaller.Download(info,root,null,cancelled.Token,new PackageHandler {Data=bytes}).ConfigureAwait(false);}catch(OperationCanceledException){rejected=true;}
                    Check(rejected,"download cancellation ignored");
                }
                using(var signal=new EventWaitHandle(false,EventResetMode.ManualReset,UpdateInstaller.EventName(AppDomain.CurrentDomain.BaseDirectory))) {
                    Check(!UpdateInstaller.Requested(AppDomain.CurrentDomain.BaseDirectory),"unsignalled update active");
                    signal.Set();
                    var client=new FakeClient();var store=new RuntimeStore(Path.Combine(root,"worker"));store.Write("pending.json",new {keep=true});
                    var worker=new Worker(store,()=>Task.FromResult<IAppClient>(client),c=>{throw new Exception("update restored pet");},ms=>{throw new Exception("update continued loop");},()=>1000);
                    await worker.Run().ConfigureAwait(false);
                    Check(!worker.Status.running&&worker.Status.automationDisconnected&&client.Boosts==0&&client.Claims==0&&client.Quits==0&&store.Exists("pending.json"),"update stop changed account or pending journal");
                    signal.Reset();
                    foreach(var timing in new[]{"preflight","inflight"}) {
                        var active=new FakeClient();active.State.available=0;
                        var activeStore=new RuntimeStore(Path.Combine(root,timing));
                        activeStore.Write("preferences.json",new Preference {mode="clicker"});
                        if(timing=="preflight")active.VersionResult=()=>{signal.Set();return active.State.appVersion;};
                        else active.BoostResult=(delta,before)=> {
                            Check(activeStore.Exists("pending.json"),"in-flight operation lacked journal");signal.Set();
                            var after=Tests.Copy(before);after.cumulative+=delta;after.available+=delta;active.State=after;return after;
                        };
                        int delays=0;
                        var activeWorker=new Worker(activeStore,()=>Task.FromResult<IAppClient>(active),c=>{throw new Exception("update restarted pet");},ms=>{if(++delays>10)throw new Exception("fixture did not reach update boundary");return Task.FromResult(0);},()=>1000);
                        await activeWorker.Run().ConfigureAwait(false);
                        Check(UpdateInstaller.Requested(AppDomain.CurrentDomain.BaseDirectory)&&active.Boosts==(timing=="inflight"?1:0)&&active.Claims==0&&!activeStore.Exists("pending.json")&&!activeWorker.Status.running,"update interrupted confirmation or started another operation: "+timing);
                        signal.Reset();
                    }
                }
                File.WriteAllText(Path.Combine(stage,"completed"),"ok");UpdateInstaller.Cleanup(root);
                Check(!Directory.Exists(stage),"completed update not cleaned");
                await CrossProcess(root,zip).ConfigureAwait(false);
                return checks;
            } catch(Exception error) {failure=error;throw;}
            finally {
                try {DeleteFixture(root);}
                catch(Exception cleanupError) {if(failure==null)throw;Console.Error.WriteLine("Installer fixture cleanup also failed: "+cleanupError);}
            }
        }
    }
}
