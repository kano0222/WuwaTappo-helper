using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tappo {
    internal static class ReleaseNotesChecks {
        static int checks;
        static void Check(bool pass,string message) {checks++;if(!pass)throw new Exception(message);}
        static readonly ReleaseNote[] Notes={
            new ReleaseNote {version="0.2.0",body="- 新增自动运行。"},
            new ReleaseNote {version="0.2.1",body="- 修复部分电脑无法打开助手的问题。"},
            new ReleaseNote {version="0.2.2",body="- 自动领取增加安全余量。"},
            new ReleaseNote {version="0.2.3",body="- 自动运行成功后收进托盘。\n- 更新后显示更新内容。"}
        };
        public static int Run() {
            checks=0;
            Check(ReleaseNotes.Select(Notes,"0.2.3",null).Single().version=="0.2.3","first install displayed historical notes");
            Check(ReleaseNotes.Select(Notes,"0.2.3","invalid").Length==1,"invalid read state did not use current version");
            Check(ReleaseNotes.Select(Notes,"0.2.3","9999999999999999999999.2.3").Length==1&&ReleaseNotes.Select(Notes,"0.2.3","0.2.3-beta.9999999999999999999999").Length==1,"malformed read version prevented the notice");
            Check(string.Join(",",ReleaseNotes.Select(Notes,"0.2.3","0.2.0").Select(n=>n.version))=="0.2.3,0.2.2,0.2.1","skipped versions missing or incorrectly ordered");
            Check(ReleaseNotes.Select(Notes,"0.2.3","0.2.3").Length==0&&ReleaseNotes.Select(Notes,"0.2.2","0.2.3").Length==0,"same version or downgrade prompted again");
            Check(ReleaseNotes.Select(Notes,"0.2.4",null).Length==0&&ReleaseNotes.Select(Notes,"0.2.3.1-dev",null).Length==0,"missing or development release has misleading notes");
            var beta=Notes.Concat(new[]{new ReleaseNote {version="0.2.3-beta.1",body="- 测试内容"}}).ToArray();
            Check(ReleaseNotes.Select(beta,"0.2.3","0.2.3-beta.1").Single().version=="0.2.3","beta to stable update ignored");
            var embedded=Json.Decode<ReleaseNote[]>(Resources.Text("ReleaseNotes.json"));
            Check(embedded.Length>0&&embedded.Any(n=>n.version=="0.2.2"&&n.body.Contains("安全余量")),"embedded changelog missing release content");
            var root=Path.Combine(Path.GetTempPath(),"tappo-notes-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try {
                using(var first=ReleaseNotes.Open(root,"0.2.1",Notes)) {
                    Check(first!=null,"first notice unavailable");
                    Check(ReleaseNotes.Open(root,"0.2.1",Notes)==null,"concurrent panel duplicated notice");
                    Check(!File.Exists(Path.Combine(root,"runtime","release-notes-read.json")),"opening marked notes read");
                }
                using(var retry=ReleaseNotes.Open(root,"0.2.1",Notes)) {Check(retry!=null,"undismissed notice lost after restart");retry.MarkRead();}
                Check(ReleaseNotes.Open(root,"0.2.1",Notes)==null,"dismissed notice shown again");
                using(var skipped=ReleaseNotes.Open(root,"0.2.3",Notes))Check(skipped.Entries.Length==2,"persisted read state lost skipped versions");
                var store=new RuntimeStore(root);
                store.Write("preferences.json",new Preference {mode="clicker",autoRun=true});
                using(var panel=new Panel(store,true)) {
                    panel.InitializeTray();panel.Window.Show();
                    var status=new Status {running=true,pid=Process.GetCurrentProcess().Id,connected=true,at=DateTimeOffset.UtcNow.ToString("o")};
                    panel.ReadStatus=()=>status;
                    panel.ShowReleaseNotes("0.2.3",Notes);
                    panel.AutoRunOnOpen().GetAwaiter().GetResult();
                    Check(panel.ReleaseNotesWindow!=null&&panel.ReleaseNotesWindow.IsVisible&&panel.Window.IsVisible,"notice hidden by automatic tray");
                    using(var second=new Panel(new RuntimeStore(Path.Combine(root,"instance"),"fixture",root),true)) {
                        second.Window.Show();second.ShowReleaseNotes("0.2.3",Notes);
                        Check(second.ReleaseNotesWindow==null,"instance showed duplicate notice");
                    }
                    ((Button)panel.ReleaseNotesWindow.FindName("DismissButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(panel.ReleaseNotesWindow==null&&!panel.Window.IsVisible&&!panel.Window.ShowInTaskbar,"dismissal did not complete deferred tray");
                    Check(ReleaseNotes.Open(root,"0.2.3",Notes)==null,"dismissal did not persist read version");
                    panel.Show();panel.TryAutoTray();Check(panel.Window.IsVisible,"restoring after notes dismissal hid again");
                    panel.ShowReleaseNotes("0.2.3",Notes);Check(panel.ReleaseNotesWindow==null,"read notes reopened");
                }
                File.WriteAllText(Path.Combine(root,"runtime","release-notes-read.json"),"{corrupt");
                using(var corrupt=ReleaseNotes.Open(root,"0.2.3",Notes))Check(corrupt.Entries.Length==1,"damaged read state prevented the notice");
                File.Delete(Path.Combine(root,"runtime","release-notes-read.json"));
                using(var panel=new Panel(store,true)) {
                    panel.Window.Show();panel.ShowReleaseNotes("0.2.3",Notes);panel.AllowExit=true;panel.Window.Close();
                    Check(!File.Exists(Path.Combine(root,"runtime","release-notes-read.json")),"application exit marked unread notes read");
                }
                if(embedded.Any(n=>n.version==Updates.BuildVersion)) {
                    using(var panel=new Panel(new RuntimeStore(Path.Combine(root,"startup")))) {
                        panel.AutoShowReleaseNotes=true;panel.Window.Show();
                        panel.Window.Dispatcher.Invoke(new Action(()=>{}),System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        Check(panel.ReleaseNotesWindow!=null&&panel.ReleaseNotesWindow.IsVisible,"release startup did not show embedded notes");
                        panel.AllowExit=true;panel.Window.Close();
                    }
                }
                Render(ReleaseNotes.Select(Notes,"0.2.3","0.2.0"));
                return checks;
            } finally {Directory.Delete(root,true);}
        }
        static void Render(ReleaseNote[] notes) {
            var output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"release-notes-preview");Directory.CreateDirectory(output);
            double singleHeight=0;
            foreach(var scenario in new[]{"single","multiple","long","narrow"}) {
                var content=scenario=="single"?notes.Take(1).ToArray():scenario=="long"?new[]{new ReleaseNote {version="0.2.3",body=string.Join("\n",Enumerable.Repeat("- 更新内容较多时保持底部按钮可见，并在内容区域滚动查看。",24))}}:notes;
                var dialog=new ReleaseNotesDialog(null,"0.2.3",content);
                dialog.Window.WindowStartupLocation=WindowStartupLocation.Manual;dialog.Window.Left=-10000;dialog.Window.Top=-10000;
                if(scenario=="narrow") {dialog.Window.SizeToContent=SizeToContent.Manual;dialog.Window.Width=360;dialog.Window.Height=360;}
                dialog.Window.Show();dialog.Window.UpdateLayout();
                var surface=(FrameworkElement)dialog.Window.Content;
                var size=new Size(surface.ActualWidth,surface.ActualHeight);
                var scroll=(ScrollViewer)dialog.Window.FindName("NotesScroll");
                var button=(Button)dialog.Window.FindName("DismissButton");var position=button.TranslatePoint(new Point(),surface);
                Check(position.Y>=0&&position.Y+button.ActualHeight<=size.Height&&button.ActualWidth>=100,"dismiss button clipped at "+size);
                Check(scroll.ViewportHeight>0&&(scroll.ScrollableHeight>0)==(scenario=="long"||scenario=="narrow"),"notes scroll inaccessible at "+size);
                Check(dialog.Window.ActualHeight<=dialog.Window.MaxHeight,"notes window exceeded work area limit");
                if(scenario=="single") {singleHeight=size.Height;Check(size.Width==480&&singleHeight<360,"short notes did not shrink to content: "+size);}
                if(scenario=="multiple")Check(size.Height>singleHeight&&size.Height<560,"multiple notes did not grow to content");
                if(scroll.ScrollableHeight>0) {scroll.ScrollToEnd();surface.UpdateLayout();Check(scroll.VerticalOffset==scroll.ScrollableHeight,"last release cannot be reached by scrolling");}
                scroll.ScrollToHome();surface.UpdateLayout();
                var image=new RenderTargetBitmap((int)Math.Ceiling(size.Width),(int)Math.Ceiling(size.Height),96,96,PixelFormats.Pbgra32);image.Render(surface);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                using(var file=File.Create(Path.Combine(output,"notes-"+scenario+".png")))encoder.Save(file);
                dialog.Window.Close();
            }
        }
    }
}
