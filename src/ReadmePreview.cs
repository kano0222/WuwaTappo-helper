using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Tappo {
    // Offline fixtures only: no worker, app client, visible window, timer, tray or update check.
    internal static class ReadmePreview {
        public static void Render(string output) {
            Directory.CreateDirectory(output);
            var root=Path.Combine(Path.GetTempPath(),"tappo-readme-"+Guid.NewGuid().ToString("N"));
            var store=new RuntimeStore(root);
            var epoch=new DateTimeOffset(2026,10,27,12,0,0,TimeSpan.FromHours(8));
            var counts=new[]{1600,1600,1700,1800,1900,1905,1905,1905,1910,910,910,910,910};
            const int receiptFrame=9, confirmationFrame=11, confirmationDelay=58;
            const string operationId="preview-left-chest";
            var receivedAt=epoch.AddSeconds(receiptFrame).ToUnixTimeMilliseconds();
            try {
                var history=new ClaimHistory(store,null);
                history.Record("preview-earlier-left");
                history.Record("preview-earlier-right");
                for(int frame=0;frame<counts.Length;frame++) {
                    var confirmed=frame>=confirmationFrame;
                    var time=epoch.AddSeconds(frame+(confirmed?confirmationDelay:0));
                    var now=time.ToUnixTimeMilliseconds();
                    var mode=frame<2||frame>=receiptFrame?"brute":frame<5?"clicker":"human";
                    var claimed=frame>=receiptFrame;
                    var confirming=claimed&&!confirmed;
                    if(confirming)store.Write("pending.json",new Journal {id=operationId});
                    else if(frame==confirmationFrame) {
                        history.Record(operationId);
                        store.ClearPending();
                    }
                    var state=new Snapshot {time=now,loggedIn=true,available=counts[frame],
                        cumulative=counts[frame]+(claimed?12000:11000),consumed=claimed?12000:11000,
                        left=epoch.AddSeconds(receiptFrame+(claimed?1800:0)).ToUnixTimeMilliseconds(),right=epoch.AddSeconds(754).ToUnixTimeMilliseconds(),
                        buttons=new Dictionary<string,bool>{{"left",false},{"right",false}}};
                    var status=new Status {at=time.ToString("o"),pid=Process.GetCurrentProcess().Id,running=true,
                        paused=frame==6||frame==7,recovering=confirming,mode=mode,state=state,claimsThisRun=confirmed?3:2,
                        chestTimes=new ChestTimes {updatedAt=now,left=state.left,right=state.right}};
                    store.Write("preferences.json",new Preference {mode=mode});
                    var events=new List<string> {
                        Json.Encode(new {at=epoch.AddMinutes(-1).ToString("o"),@event="started"}),
                        Json.Encode(new {at=epoch.AddSeconds(-30).ToString("o"),@event="claim-confirmed",side="right"})
                    };
                    for(int i=2;i<=frame;i++) {
                        if(counts[i]>counts[i-1]) events.Add(Json.Encode(new {at=epoch.AddSeconds(i).ToString("o"),@event="boost-confirmed",delta=counts[i]-counts[i-1]}));
                    }
                    if(claimed) events.Add(Json.Encode(new {at=epoch.AddSeconds(receiptFrame).ToString("o"),@event="claim-received",side="left",operationId,receivedAt}));
                    if(confirmed) events.Add(Json.Encode(new {at=epoch.AddSeconds(confirmationFrame+confirmationDelay).ToString("o"),@event="claim-confirmed",side="left",operationId,receivedAt}));
                    File.WriteAllLines(store.FilePath("events.jsonl"),events);
                    using(var panel=new Panel(store,preview:true)) {
                        panel.ReadClock=()=>now;panel.ReadStatus=()=>status;
                        panel.DispatchOverride=action=>{throw new InvalidOperationException("预览禁止执行自动化。");};
                        panel.SetPath(@"C:\WuwaTappo\WuwaTappo.exe");
                        panel.Render(Path.Combine(output,frame.ToString("000")+".png"));
                        if(panel.E<TextBlock>("StatusLabel").Text!=(status.paused?"已暂停":confirming?"检查中":"运行中"))
                            throw new InvalidOperationException("模拟状态渲染失败。");
                        if(frame==4) {
                            panel.E<Button>("ViewModeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            var surface=panel.E<Border>("RootSurface");
                            var width=(int)panel.Window.Width;
                            surface.Measure(new Size(width,double.PositiveInfinity));
                            panel.Render(Path.Combine(output,"readme-mini.png"),width,(int)Math.Ceiling(surface.DesiredSize.Height));
                            panel.E<Button>("MiniViewModeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                    }
                }
            } finally {
                // Only remove the unique fixture directory created by this invocation.
                var target=Path.GetFullPath(root);
                if(target.StartsWith(Path.GetFullPath(Path.GetTempPath()),StringComparison.OrdinalIgnoreCase)&&
                    Path.GetFileName(target).StartsWith("tappo-readme-",StringComparison.Ordinal)) Directory.Delete(target,true);
            }
        }
    }
}
