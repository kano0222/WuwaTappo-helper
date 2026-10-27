using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tappo {
    internal enum CloseChoice {Cancel,Exit,Tray}
    internal sealed class CloseDialog {
        public readonly Window Window;
        internal readonly Button ExitButton,TrayButton;
        CloseChoice choice=CloseChoice.Cancel;
        public CloseDialog(Window owner) {
            Window=new Window {Title="退出",Width=430,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,
                WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner,
                ShowInTaskbar=false,FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=12,Background=Brushes.White};
            if(owner!=null)Window.Owner=owner;
            var grid=new Grid {Background=Brushes.White};grid.RowDefinitions.Add(new RowDefinition());grid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});
            var content=new Grid {Margin=new Thickness(18,20,18,20)};
            content.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});content.ColumnDefinitions.Add(new ColumnDefinition());
            var icon=new Image {Width=32,Height=32,Margin=new Thickness(0,0,12,0),VerticalAlignment=VerticalAlignment.Center,
                Source=Imaging.CreateBitmapSourceFromHIcon(System.Drawing.SystemIcons.Information.Handle,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions())};
            content.Children.Add(icon);
            var text=new TextBlock {Text="确认要离开吗？你可以选择退出，或让助手在托盘继续运行。",TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
            Grid.SetColumn(text,1);content.Children.Add(text);grid.Children.Add(content);
            var footer=new Border {Background=new SolidColorBrush(Color.FromRgb(240,240,240)),Padding=new Thickness(12,10,12,10)};
            var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
            ExitButton=new Button {Content="退出",MinWidth=88,Height=30,Margin=new Thickness(0,0,8,0),IsDefault=true};
            TrayButton=new Button {Content="最小化到托盘",MinWidth=112,Height=30};
            ExitButton.Click+=(s,e)=>{choice=CloseChoice.Exit;Window.Close();};
            TrayButton.Click+=(s,e)=>{choice=CloseChoice.Tray;Window.Close();};
            Window.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;Window.Close();}};
            buttons.Children.Add(ExitButton);buttons.Children.Add(TrayButton);footer.Child=buttons;Grid.SetRow(footer,1);grid.Children.Add(footer);Window.Content=grid;
        }
        public CloseChoice Show() {Window.ShowDialog();return choice;}
    }
}
