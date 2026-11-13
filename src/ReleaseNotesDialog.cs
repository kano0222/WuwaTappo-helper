using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace Tappo {
    internal sealed class ReleaseNotesDialog {
        public readonly Window Window;
        public ReleaseNotesDialog(Window owner,string version,ReleaseNote[] notes) {
            Window=(Window)XamlReader.Parse(@"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='更新内容' Width='480' SizeToContent='Height' MinWidth='360'
        WindowStyle='None' AllowsTransparency='True' Background='Transparent' ShowInTaskbar='False'
        ResizeMode='CanResizeWithGrip' FontFamily='Microsoft YaHei UI' FontSize='13' Foreground='#302C28'>
  <Window.Resources>
    <Style TargetType='Button'>
      <Setter Property='Background' Value='#EDB65E'/><Setter Property='Foreground' Value='#382917'/>
      <Setter Property='BorderBrush' Value='#DDD7CE'/><Setter Property='BorderThickness' Value='1'/>
      <Setter Property='Padding' Value='18,0'/><Setter Property='Height' Value='40'/>
      <Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
        <Border x:Name='Surface' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='10' Padding='{TemplateBinding Padding}'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Opacity' Value='0.85'/></Trigger>
          <Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Opacity' Value='0.65'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='#A5702E'/><Setter TargetName='Surface' Property='BorderThickness' Value='2'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
  </Window.Resources>
  <Border Background='#F7F6F2' BorderBrush='#DDD7CE' BorderThickness='1' CornerRadius='18' Padding='24,16,24,24'>
    <Grid>
      <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/><RowDefinition Height='*'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
      <Grid x:Name='TitleGrip' Background='Transparent' Margin='0,0,0,12'>
        <TextBlock Text='WUWA TAPPO  /  更新内容' Foreground='#918678' FontSize='12' VerticalAlignment='Center'/>
        <Button x:Name='CloseButton' Content='×' HorizontalAlignment='Right' Width='30' Height='30' Padding='0' Background='Transparent' BorderThickness='0' FontSize='20' AutomationProperties.Name='关闭更新内容'/>
      </Grid>
      <StackPanel Grid.Row='1' Margin='0,0,0,18'>
        <TextBlock x:Name='Heading' FontSize='21' FontWeight='SemiBold' TextWrapping='Wrap'/>
        <TextBlock x:Name='Summary' Foreground='#81796F' Margin='0,8,0,0' TextWrapping='Wrap'/>
      </StackPanel>
      <ScrollViewer x:Name='NotesScroll' Grid.Row='2' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled' CanContentScroll='False'>
        <StackPanel x:Name='Entries' Margin='0,0,12,0'/>
      </ScrollViewer>
      <Button x:Name='DismissButton' Grid.Row='3' Content='知道了' Width='112' HorizontalAlignment='Right' Margin='0,20,0,0' IsDefault='True'/>
    </Grid>
  </Border>
</Window>");
            Window.MaxHeight=Math.Min(560,SystemParameters.WorkArea.Height-40);
            Window.WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner;
            if(owner!=null)Window.Owner=owner;
            ((TextBlock)Window.FindName("Heading")).Text="已更新至 v"+version;
            ((TextBlock)Window.FindName("Summary")).Text=notes.Length>1?"以下是你尚未查看的 "+notes.Length+" 个版本的更新内容。":"本次更新内容";
            var entries=(StackPanel)Window.FindName("Entries");
            foreach(var note in notes) {
                entries.Children.Add(new TextBlock {Text="v"+note.version,FontSize=15,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,entries.Children.Count==0?0:16,0,10)});
                foreach(var line in note.body.Split('\n').Select(s=>s.TrimEnd()).Where(s=>!string.IsNullOrWhiteSpace(s)))
                    entries.Children.Add(new TextBlock {Text=line.StartsWith("- ")?"• "+line.Substring(2):line,TextWrapping=TextWrapping.Wrap,LineHeight=23,Margin=new Thickness(0,0,0,8)});
            }
            ((Button)Window.FindName("CloseButton")).Click+=(s,e)=>Window.Close();
            ((Button)Window.FindName("DismissButton")).Click+=(s,e)=>Window.Close();
            ((Grid)Window.FindName("TitleGrip")).MouseLeftButtonDown+=(s,e)=>{if(e.LeftButton==MouseButtonState.Pressed)Window.DragMove();};
            Window.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;Window.Close();}};
        }
    }
}
