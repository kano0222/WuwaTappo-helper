using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Tappo {
    internal static class PanelMessagingChecks {
        static int checks;
        static void Check(bool value,string message) {checks++;if(!value)throw new Exception(message);}
        static string Text(Panel panel,string name) {return panel.E<TextBlock>(name).Text;}
        internal static int Run() {
            checks=0;var root=Path.Combine(Path.GetTempPath(),"tappo-messages-"+Guid.NewGuid().ToString("N"));
            try {
                var store=new RuntimeStore(root);long now=RuntimeStore.Now;
                var identity=RuntimeIdentity.Create(store,"worker");
                var status=new Status {identity=identity,pid=identity.pid,running=true,connected=true,at=DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("o"),state=Tests.State(),claimsThisRun=7};
                status.state.time=now;
                using(var panel=new Panel(store,preview:true)) {
                    panel.ReadStatus=()=>status;panel.ReadClock=()=>now;panel.InspectIdentity=_=>"matched";panel.Refresh();
                    Check(Text(panel,"AvailableCaption")=="可用次数"&&Text(panel,"ClaimsCaption")=="本次运行领取","fresh figures marked historical");
                    now+=65000;panel.Refresh();
                    Check(Text(panel,"MainMessage")=="后台响应异常"&&Text(panel,"DetailMessage").Contains("1 分 5 秒")&&!Text(panel,"DetailMessage").Contains("卡死"),"stale matching process lacks age or asserts a root cause");
                    Check(Text(panel,"AvailableCaption").Contains("上次记录")&&Text(panel,"ClaimsCaption").Contains("上次记录")&&Text(panel,"ClaimsHint").StartsWith("记录于 "),"old numbers appear current");
                    Check(Text(panel,"ReserveCaption").Contains("记录于")&&Text(panel,"SnapshotLabel").Contains("上次记录"),"old figure timestamps missing");
                    Check(Text(panel,"CountCaption")=="助手领取的宝箱数"&&Text(panel,"TotalValue")=="0","independent cumulative count incorrectly marked stale");
                    panel.InspectIdentity=_=>"identity-unverifiable";panel.Refresh();
                    Check(Text(panel,"MainMessage")=="无法确认后台状态"&&!Text(panel,"ActivityText").Contains("后台已退出"),"unknown identity presented as exit");
                    panel.InspectIdentity=_=>"pid-reused";panel.Refresh();
                    Check(Text(panel,"MainMessage")=="后台已退出"&&Text(panel,"DetailMessage").Contains("上次运行记录")&&Text(panel,"AvailableCaption").Contains("上次记录"),"original process disappearance not clearly distinguished");
                    panel.InspectIdentity=_=>"matched";status.at=DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("o");status.state.time=now;panel.Refresh();
                    Check(Text(panel,"MainMessage")=="后台运行中"&&!Text(panel,"AvailableCaption").Contains("上次"),"fresh state retains old anomaly labels");
                    status.state.time=now-60000;panel.Refresh();
                    Check(Text(panel,"AvailableCaption").Contains("上次记录")&&Text(panel,"ClaimsCaption")=="本次运行领取","fresh status masks an old balance snapshot or incorrectly ages the run counter");
                    status.state.time=now;
                    panel.ReadStatus=()=>{throw new IOException("private path/stack");};panel.Refresh();
                    Check(Text(panel,"MainMessage")=="无法确认后台状态"&&Text(panel,"AvailableCaption").Contains("上次记录")&&!Text(panel,"DetailMessage").Contains("private"),"read failure leaves figures current or leaks technical detail");
                    panel.ReadStatus=()=>status;
                    status.at="invalid";panel.Refresh();Check(Text(panel,"DetailMessage").Contains("更新时间未知"),"missing timestamp invented an age");
                    status.at=DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("o");
                    foreach(var action in new[]{"pause","resume","stop","stop-background","disconnect","reset"}) {
                        panel.ControlTimeoutMs=1;
                        Pump(async()=>{
                            var operation=panel.Act(action);
                            Check(!Text(panel,"DetailMessage").Contains("管理员"),"non-start busy prompt mentions administrator");
                            if(action=="resume")Check(Text(panel,"MainMessage")=="正在恢复运行…"&&
                                Text(panel,"DetailMessage").Contains("接收恢复指令"),"resume is missing immediate acknowledgement");
                            try {await operation;throw new Exception("missing acknowledgement succeeded");}
                            catch(TimeoutException error) {
                                Check(error.Message==Panel.TimeoutMessage(action,false)&&!error.Message.Contains("管理员")&&error.Message.Contains("导出日志"),"action-specific timeout lost uncertainty or suggests elevation");
                            }
                        });
                        Check(Text(panel,"StatusLabel")=="结果未确认"&&!Text(panel,"DetailMessage").Contains("已成功"),"timeout looks successful");
                    }
                    panel.ControlTimeoutMs=3000;
                    var output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"console-previews");Directory.CreateDirectory(output);
                    status.commandId=store.Read<Command>("control.json").id;status.resetCommandId=status.commandId;
                    status.paused=true;status.manuallyPaused=true;panel.Refresh();
                    panel.E<Button>("ViewModeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var window=panel.Window;
                    window.ShowActivated=false;window.ShowInTaskbar=false;window.Opacity=0;
                    window.Show();window.UpdateLayout();
                    var alert=panel.E<Border>("MiniAlert");var miniStatus=panel.E<TextBlock>("MiniStatusLabel");
                    double compactHeight=window.ActualHeight;
                    var stats=panel.E<Border>("MiniStatsCard");var surface=panel.E<Border>("RootSurface");
                    double statsTop=stats.TranslatePoint(new Point(),surface).Y;
                    Check(alert.Visibility==Visibility.Collapsed,"stopped mini shows an alert");
                    Pump(async()=>{
                        var operation=panel.Act("resume");
                        window.UpdateLayout();
                        Check(alert.Visibility==Visibility.Collapsed&&window.ActualHeight==compactHeight&&miniStatus.Text=="正在恢复运行…"&&
                            miniStatus.ToolTip.ToString().Contains("接收恢复指令"),"initial mini resume changes its height or loses progress");
                        status.commandId=store.Read<Command>("control.json").id;
                        status.message="正在准备桌宠连接…";status.retrying=true;
                        status.paused=false;status.manuallyPaused=false;status.at=DateTimeOffset.UtcNow.ToString("o");
                        await Task.Delay(400);
                        Check(!operation.IsCompleted&&Text(panel,"DetailMessage")==status.message,
                            "resume acknowledged completion before preparation finished or hid its progress");
                        window.UpdateLayout();
                        Check(alert.Visibility==Visibility.Collapsed&&window.ActualHeight==compactHeight&&miniStatus.ToolTip.ToString()==status.message&&
                            stats.TranslatePoint(new Point(),surface).Y==statsTop,"detailed mini resume moves cards or loses tooltip");
                        panel.Render(Path.Combine(output,"mini-resuming.png"),(int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight));
                        status.retrying=false;
                        await operation;
                    });
                    window.Hide();
                    panel.E<Button>("MiniViewModeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    panel.ControlTimeoutMs=1;
                    Pump(async()=>{try {await panel.Act("pause");}catch(TimeoutException) { }});
                    panel.Render(Path.Combine(output,"messages-pause-unconfirmed-minimum.png"),820,660);
                    var detail=panel.E<TextBlock>("DetailMessage");var picker=panel.E<Button>("BrowseAppButton");
                    Check(detail.TranslatePoint(new Point(),panel.Window.Content as UIElement).Y+detail.ActualHeight<=picker.TranslatePoint(new Point(),panel.Window.Content as UIElement).Y,"unconfirmed-action text overlaps path selection at minimum size");
                    var command=store.Read<Command>("control.json");
                    now=RuntimeStore.Now+1;status.at=DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("o");status.paused=true;status.manuallyPaused=true;status.commandId="unrelated";panel.Refresh();
                    Check(Text(panel,"StatusLabel")=="结果未确认","unrelated status falsely acknowledged the command");
                    status.commandId=command.id;panel.Refresh();
                    Check(Text(panel,"MainMessage")=="已停止运行"&&!Text(panel,"DetailMessage").Contains("未能确认"),"late acknowledgement retains timeout warning");
                    status.paused=false;status.manuallyPaused=false;panel.Refresh();
                    Check(Text(panel,"MainMessage")=="后台运行中"&&!Text(panel,"DetailMessage").Contains("未能确认"),"recovery retains failed-action notice");
                    now+=65000;
                    panel.Render(Path.Combine(output,"messages-stale-minimum.png"),820,660);
                    panel.InspectIdentity=_=>"identity-unverifiable";panel.Render(Path.Combine(output,"messages-unknown.png"));
                    panel.InspectIdentity=_=>"process-exited";panel.Render(Path.Combine(output,"messages-exited.png"));
                }
                Check(Panel.TimeoutMessage("start",true).Contains("管理员")&&!Panel.TimeoutMessage("start",false).Contains("管理员"),"start prompt ignores actual elevation path");
                Check(Panel.TimeoutMessage("pause",false).Contains("可能仍在执行")&&!Panel.TimeoutMessage("pause",false).Contains("命令未执行"),"pause timeout claims an unobserved outcome");
            } finally {Directory.Delete(root,true);}
            return checks;
        }
        static void Pump(Func<Task> operation) {
            var previous=SynchronizationContext.Current;var dispatcher=Dispatcher.CurrentDispatcher;
            try {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                var task=operation();var frame=new DispatcherFrame();
                task.ContinueWith(_=>dispatcher.BeginInvoke(new Action(()=>frame.Continue=false)));
                Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();
            } finally {SynchronizationContext.SetSynchronizationContext(previous);}
        }
    }
}
