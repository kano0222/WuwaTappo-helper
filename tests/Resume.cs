using System;
using System.IO;
using System.Threading.Tasks;

namespace Tappo {
    internal static class ResumeChecks {
        public static async Task<int> Run() {
            int checks=0;var root=Path.Combine(Path.GetTempPath(),"tappo-resume-"+Guid.NewGuid().ToString("N"));
            try {
                foreach(var scenario in new[]{"missing","existing","pending","prepare-fails","startup-write-fails","server-wait"}) {
                    var store=new RuntimeStore(Path.Combine(root,scenario));var old=new FakeClient();var fresh=new FakeClient();
                    old.State.left=old.State.right=fresh.State.left=fresh.State.right=900000;
                    bool pending=scenario=="pending",petExists=true,allowReconnect=false;int stage=0,loops=0,prepared=0,launched=0,reconnections=0;long now=100000,deadline=0,preparedAt=0;
                    if(pending) {store.Write("pending.json",new {historical=true});old.State.loggedIn=false;fresh.State.loggedIn=false;}
                    if(scenario=="server-wait")old.ServerResult=()=>{throw new AppQueryException(new QueryReply {httpStatus=503,retryAfterMs=120000,requiredRetryAfterMs=120000});};
                    Worker worker=null;
                    worker=new Worker(store,()=>Task.FromResult<IAppClient>(old),c=>c.Quit(),ms=> {
                        if(stage==0) {
                            deadline=worker.Status.readRetryNotBefore??0;old.Connected=false;petExists=scenario=="existing";stage=1;
                        } else if(stage==1) {
                            if(prepared!=0||launched!=0)throw new Exception("automatic reconnect starts a pet");
                            store.Write("control.json",new Command {id="pause",action="pause"});stage=2;
                        } else if(stage==2&&worker.Status.manuallyPaused) {
                            store.Write("control.json",new Command {id="resume",action="resume"});stage=3;
                        } else if(stage==3) {
                            if(scenario=="server-wait") {
                                if(fresh.Reads>0&&now<deadline)throw new Exception("manual startup bypasses the required server wait");
                                if(fresh.Reads>0) {stage=4;worker.RequestDisconnect();}
                            } else {stage=4;worker.RequestDisconnect();}
                        }
                        now+=ms;if(++loops>200)worker.RequestDisconnect();return Task.FromResult(0);
                    },()=>now,()=> {
                        reconnections++;
                        if(!petExists||!allowReconnect)throw new ConnectionFailureException("debug-unavailable","所选桌宠尚未运行。");
                        return Task.FromResult<IAppClient>(fresh);
                    },()=> {
                        prepared++;preparedAt=now;
                        if(scenario=="prepare-fails")throw new InvalidOperationException("桌宠启动失败。");
                        if(scenario=="startup-write-fails")throw new StartupConfigurationException(new IOException("test write failure"));
                        if(!petExists) {launched++;petExists=true;}
                        allowReconnect=true;return Task.FromResult(0);
                    });
                    var result=await worker.Run();
                    if(scenario=="startup-write-fails") {
                        if(result==0||worker.Status.running||worker.Status.fault==null||prepared!=1)throw new Exception("startup diagnostics failure did not stop the worker");checks++;
                    } else {
                        if(stage!=4||loops>200)throw new Exception(scenario+" did not complete stop/start");checks++;
                        if(pending) {if(prepared!=0||launched!=0||!store.Exists("pending.json"))throw new Exception("pending operation permits a pet restart");}
                        else if(scenario=="prepare-fails") {if(prepared!=1||launched!=0||!File.ReadAllText(store.FilePath("events.jsonl")).Contains("桌宠启动失败"))throw new Exception("manual preparation failure lost its reason");}
                        else if(prepared!=1||!allowReconnect||reconnections<1||worker.Status.fault!=null||launched!=(scenario=="existing"?0:1))throw new Exception(scenario+" manual start did not restore the pet connection");checks++;
                    }
                    if(scenario=="server-wait"&&(preparedAt>=deadline||fresh.Reads==0||now<deadline))throw new Exception("pet was not started while the server cooldown remained protected");checks++;
                    if(old.Claims+fresh.Claims+old.Boosts+fresh.Boosts+old.Quits+fresh.Quits!=0)throw new Exception("resume regression fixture mutated or closed the pet");checks++;
                }
            } finally {Directory.Delete(root,true);}
            return checks;
        }
    }
}
