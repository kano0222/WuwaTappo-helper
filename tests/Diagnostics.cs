using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Tappo {
    internal static class DiagnosticChecks {
        static int checks;
        static void Check(bool pass,string message) {checks++;if(!pass)throw new Exception(message);}
        public static int Run() {
            checks=0;var root=Path.Combine(Path.GetTempPath(),"tappo-diagnostics-"+Guid.NewGuid().ToString("N"));
            try {
                var store=new RuntimeStore(root);
                store.Log("paused-on-error",new {message="领取结果尚未确认",errorType="InvalidOperationException",code=70006,httpStatus=503,detail="Tappo.Worker.MoveNext test stack detail",before=new {accountKey="PRIVATE-SNAPSHOT-MARKER"}});
                store.Log("manual-resumed");store.Log("read-query-recovered");
                var log=new ActivityLog(store.FilePath("events.jsonl"));log.Refresh();
                Check(log.Recent.Count==3&&log.Recent[2].message=="异常暂停：领取结果尚未确认","specific error reason is missing from the three recent records");
                var lines=string.Join("\n",log.Recent.Select(record=>record.Line));
                Check(!lines.Contains("InvalidOperationException")&&!lines.Contains("MoveNext")&&!lines.Contains("PRIVATE-SNAPSHOT-MARKER"),"user messages expose technical log fields");
                Check(File.ReadAllText(store.FilePath("events.jsonl")).Contains("test stack detail"),"technical evidence was removed from the log file");
                var reopened=new ActivityLog(store.FilePath("events.jsonl"));reopened.Refresh();
                Check(reopened.Recent[2].message.Contains("领取结果尚未确认"),"reopening loses recent error reasons");
                using(var panel=new Panel(store,preview:true)) {
                    Status status=null;panel.ReadStatus=()=>status;
                    var output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"console-previews");Directory.CreateDirectory(output);
                    foreach(var height in new[]{900,940}) {
                        panel.Render(Path.Combine(output,"three-activities-"+height+".png"),880,height);
                        var text=panel.E<TextBlock>("ActivityText");var area=panel.E<Border>("ActivityArea");
                        Check(text.Text.Split('\n').Length==3&&text.ActualHeight==66&&text.TranslatePoint(new Point(),area).Y+text.ActualHeight<=area.ActualHeight-area.Padding.Bottom+0.1,"three activity rows are not fully visible");
                    }
                    Check(panel.Window.FindName("LastErrorText")==null&&panel.Window.FindName("ViewLogsButton")==null&&panel.Window.FindName("ActivityScroll")==null,"duplicate error summary or removed log controls remain");
                    Check(panel.E<TextBlock>("ActivityText").ToolTip==null&&panel.E<TextBlock>("ActivityText").Text.Contains("领取结果尚未确认"),"activity still has a developer tooltip or loses the useful reason");
                    status=new Status {running=true,pid=int.MaxValue,at=DateTimeOffset.UtcNow.ToString("o")};panel.Refresh();
                    Check(panel.E<TextBlock>("ActivityText").Text.Contains("未记录原因"),"unexplained process exit is presented as a known failure");
                    status=null;
                    store.Log("stopped-on-error",new {message="query failed\nsecond line",errorType="IOException",httpStatus=502});panel.Refresh();
                    Check(panel.E<TextBlock>("ActivityText").Text.Split('\n').Length==3&&panel.E<TextBlock>("ActivityText").Text.Contains("second line"),"multiline reasons break the three-row limit or lose information");
                    for(int i=0;i<220;i++)store.Log("boost-confirmed",new {delta=100+i});panel.Refresh();
                    Check(!panel.E<TextBlock>("ActivityText").Text.Contains("领取结果尚未确认"),"an old error is duplicated after it leaves the latest three activities");
                }
                var operationRecord=ActivityLog.Parse(Json.Encode(new {at=DateTimeOffset.UtcNow.ToString("o"),@event="operation-state",stage="verify",errorType="ConnectionFailureException"}));
                Check(operationRecord.error&&operationRecord.message=="领取结果查询失败，正在重试","operation-state exposes exception class names to users");
                File.Delete(store.FilePath("events.jsonl"));log.Refresh();
                Check(log.Recent.Count==0,"cleared log retains stale incidents");
                Directory.CreateDirectory(store.FilePath("events.jsonl"));log.Refresh();
                Check(log.ReadError!=null,"an invalid log path is silently presented as an empty log");
            } finally {Directory.Delete(root,true);}
            return checks;
        }
    }
}
