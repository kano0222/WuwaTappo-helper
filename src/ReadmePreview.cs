using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;

namespace Tappo {
    // Offline fixtures only: no Worker, AppClient, window, timer, tray or update check.
    internal static class ReadmePreview {
        public static void Render(string output) {
            Directory.CreateDirectory(output);
            var root=Path.Combine(Path.GetTempPath(),"tappo-readme-"+Guid.NewGuid().ToString("N"));
            var store=new RuntimeStore(root);
            var epoch=new DateTimeOffset(2026,10,27,12,0,0,TimeSpan.FromHours(8));
            var counts=new[]{1600,1600,1700,1800,1900,1905,1905,1905,1910,910,910};
            try {
                for(int frame=0;frame<counts.Length;frame++) {
                    var time=epoch.AddSeconds(frame);var now=time.ToUnixTimeMilliseconds();
                    var mode=frame<2||frame>=9?"brute":frame<5?"clicker":"human";
                    var claimed=frame>=9;
                    var state=new Snapshot {time=now,loggedIn=true,available=counts[frame],
                        cumulative=counts[frame]+(claimed?12000:11000),consumed=claimed?12000:11000,
                        left=epoch.AddSeconds(claimed?1809:9).ToUnixTimeMilliseconds(),right=epoch.AddSeconds(754).ToUnixTimeMilliseconds(),
                        buttons=new Dictionary<string,bool>{{"left",false},{"right",false}}};
                    var status=new Status {at=time.ToString("o"),pid=Process.GetCurrentProcess().Id,running=true,
                        paused=frame==6||frame==7,mode=mode,state=state,claimsThisRun=claimed?3:2,
                        chestTimes=new ChestTimes {updatedAt=epoch.ToUnixTimeMilliseconds(),left=state.left,right=state.right}};
                    store.Write("preferences.json",new Preference {mode=mode});
                    var events=new List<string> {
                        Json.Encode(new {at=epoch.AddMinutes(-1).ToString("o"),@event="started"}),
                        Json.Encode(new {at=epoch.AddSeconds(-30).ToString("o"),@event="claim-confirmed",side="right"})
                    };
                    for(int i=2;i<=frame;i++) {
                        if(counts[i]>counts[i-1]) events.Add(Json.Encode(new {at=epoch.AddSeconds(i).ToString("o"),@event="boost-confirmed",delta=counts[i]-counts[i-1]}));
                        if(i==9) events.Add(Json.Encode(new {at=epoch.AddSeconds(i).ToString("o"),@event="claim-confirmed",side="left"}));
                    }
                    File.WriteAllLines(store.FilePath("events.jsonl"),events);
                    using(var panel=new Panel(store,preview:true)) {
                        panel.ReadClock=()=>now;panel.ReadStatus=()=>status;
                        panel.DispatchOverride=action=>{throw new InvalidOperationException("预览禁止执行自动化。");};
                        panel.SetPath(@"C:\WuwaTappo\WuwaTappo.exe");
                        panel.Render(Path.Combine(output,frame.ToString("000")+".png"));
                        if(panel.E<ScrollViewer>("MainScroll").ScrollableHeight>=1)
                            throw new InvalidOperationException("预览面板内容未完整显示。");
                        if(panel.E<TextBlock>("StatusLabel").Text!=(status.paused?"已暂停":"运行中"))
                            throw new InvalidOperationException("模拟状态渲染失败。");
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
