using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tappo {
    internal sealed class ActivityRecord {
        public DateTimeOffset at;
        public string message;
        public bool error;
        public string Line { get {return at.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm:ss")+"    "+message.Replace('\r',' ').Replace('\n',' ');} }
    }
    // Keep the UI to user-facing messages; technical diagnostics remain in the log file.
    internal sealed class ActivityLog {
        readonly string path;
        long length=-1;DateTime written;
        public List<ActivityRecord> Recent=new List<ActivityRecord>();
        public string ReadError;
        public ActivityLog(string path) {this.path=path;}
        public void Refresh() {
            try {
                var info=new FileInfo(path);
                if(!info.Exists) {
                    if(Directory.Exists(path)) {ReadError="日志暂时无法读取：日志路径是文件夹。";return;}
                    Recent.Clear();length=-1;ReadError=null;return;
                }
                if(info.Length==length&&info.LastWriteTimeUtc==written&&ReadError==null)return;
                var recent=new List<ActivityRecord>();
                foreach(var row in Rows(path)) {
                    ActivityRecord record;
                    try {record=Parse(row);}catch {continue;}
                    if(record==null)continue;
                    recent.Add(record);
                    if(recent.Count>=3)break;
                }
                Recent=recent;ReadError=null;
                length=info.Length;written=info.LastWriteTimeUtc;
            } catch(Exception error) {
                if(!(error is IOException)&&!(error is UnauthorizedAccessException))throw;
                ReadError="日志暂时无法读取："+error.Message;
            }
        }
        static string Field(Dictionary<string,object> ev,string key) {object value;return ev.TryGetValue(key,out value)&&value!=null?Convert.ToString(value):null;}
        internal static ActivityRecord Parse(string row) {
            var ev=Json.Decode<Dictionary<string,object>>(row);var name=Field(ev,"event");string text=null;bool error=false;
            var reason=Field(ev,"message");var stage=Field(ev,"stage");var kind=Field(ev,"errorType");
            if(name=="started")text="后台已连接";
            else if(name=="program-selected")text="已更改桌宠程序位置";
            else if(name=="mode-selected")text="模式已切换为"+(Field(ev,"mode")=="brute"?"暴力模式":Field(ev,"mode")=="clicker"?"连点器模式":"人类模式");
            else if(name=="reserve-target-selected")text="储备目标已改为 "+Convert.ToInt32(ev["target"]).ToString("N0")+" 次";
            else if(name=="clicker-rate-selected")text="补充速度已改为 "+Convert.ToInt32(ev["rate"]).ToString("N0")+" 次/秒";
            else if(name=="claim-confirmed")text="已领取"+(Field(ev,"side")=="left"?"宝箱一":"宝箱二")+" · 消耗 1,000 次";
            else if(name=="boost-confirmed")text="已补充 "+ev["delta"]+" 次";
            else if(name=="boost-recovered") {
                var after=ev.ContainsKey("after")?Json.Convert<Snapshot>(ev["after"]):null;
                text=after!=null&&after.boostOverwritten?"本次补数被旧上报覆盖，按最新计数继续":"已补充 "+ev["delta"]+" 次";
            }
            else if(name=="manual-paused")text="手动停止运行";
            else if(name=="manual-resumed")text="手动恢复运行";
            else if(name=="paused-on-error") {error=true;text="异常暂停："+(reason??"旧记录未保存具体原因");}
            else if(name=="stopped-on-error") {error=true;text="后台异常退出："+(reason??"旧记录未保存具体原因");}
            else if(name=="cleanup-needs-attention") {error=true;text="停止后需要检查桌宠："+(reason??"旧记录未保存具体原因");}
            else if(name=="pending-resolved")text="已确认继续，上次操作不重发";
            else if(name=="read-query-retry") {
                error=true;text=Connection.RetryMessage(stage,Field(ev,"connectionFailure"));
                var operation=Field(ev,"operation");
                if(!string.IsNullOrWhiteSpace(reason))text+="："+reason;
                else if(!string.IsNullOrWhiteSpace(operation))text+="（"+operation+"）";
            }
            else if(name=="operation-state"&&!string.IsNullOrEmpty(kind)) {
                error=true;text=stage=="local-save"?"计数保存失败，正在重试":stage=="request"?"领取回执未确认，正在核对":stage=="verify"?"领取结果查询失败，正在重试":"领取结果仍在核对中";
            }
            else if(name=="read-query-recovered")text="状态查询已恢复";
            else if(name=="connection-restored")text="桌宠连接已恢复";
            else if(name=="app-version-validated")text="已校验桌宠 "+ev["version"]+" 接口";
            else if(name=="boost-overwritten")text="本次补数被旧上报覆盖，按最新计数继续";
            if(text==null)return null;
            return new ActivityRecord {at=DateTimeOffset.Parse(Field(ev,"at")),message=text,error=error};
        }
        static IEnumerable<string> Rows(string path) {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) {
                long position=stream.Length;var block=new byte[4096];var row=new List<byte>();
                while(position>0) {
                    var count=(int)Math.Min(position,block.Length);position-=count;stream.Position=position;
                    int read=0,n;while(read<count&&(n=stream.Read(block,read,count-read))>0)read+=n;
                    for(int i=read-1;i>=0;i--) {
                        if(block[i]==10) {
                            if(row.Count>0) {row.Reverse();yield return Encoding.UTF8.GetString(row.ToArray()).TrimEnd('\r');row.Clear();}
                        } else row.Add(block[i]);
                    }
                }
                if(row.Count>0) {row.Reverse();yield return Encoding.UTF8.GetString(row.ToArray()).TrimStart('\uFEFF').TrimEnd('\r');}
            }
        }
    }
}
