using System;
using System.IO;
using System.Windows;

namespace Tappo {
    internal static class PanelLifetimeChecks {
        internal static int Run() {
            var root=Path.Combine(Path.GetTempPath(),"tappo-panel-lifetime-"+Guid.NewGuid().ToString("N"));
            int checks=0;
            try {
                for(int i=0;i<3;i++) {
                    var store=new RuntimeStore(Path.Combine(root,i.ToString()));bool closed=false;
                    var panel=new Panel(store,preview:true);
                    panel.DispatchOverride=_=>{throw new Exception("Disposal dispatched a business command");};
                    panel.Window.Closed+=(s,e)=>closed=true;
                    panel.Window.ShowActivated=false;panel.Window.ShowInTaskbar=false;
                    panel.Window.WindowStartupLocation=WindowStartupLocation.Manual;panel.Window.Left=-10000;panel.Window.Top=-10000;
                    try {
                        panel.Window.Show();
                        if(!panel.Window.IsVisible)throw new Exception("Window fixture was not shown");checks++;
                    } finally {panel.Dispose();}
                    if(!closed||panel.Window.IsVisible)throw new Exception("Disposed panel retained a live WPF window");checks++;
                    panel.Dispose();
                    if(panel.Window.IsVisible)throw new Exception("Repeated disposal reopened the panel");checks++;
                }
            } finally {Directory.Delete(root,true);}
            return checks;
        }
    }
}
