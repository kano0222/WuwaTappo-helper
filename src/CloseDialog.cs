using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace Tappo {
    internal enum CloseChoice {Cancel,Exit,Tray}
    internal sealed class CloseDialog {
        public readonly Window Window;
        internal readonly Button ExitButton,TrayButton;
        CloseChoice choice=CloseChoice.Cancel;
        public CloseDialog(Window owner) {
            Window=(Window)XamlReader.Parse(@"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='退出' Width='480' SizeToContent='Height' ResizeMode='NoResize'
        WindowStyle='None' AllowsTransparency='True' Background='Transparent' ShowInTaskbar='False'
        FontFamily='Microsoft YaHei UI' FontSize='13' Foreground='#302C28'>
  <Window.Resources>
    <Style TargetType='Button'>
      <Setter Property='Background' Value='White'/><Setter Property='Foreground' Value='#4D443B'/>
      <Setter Property='BorderBrush' Value='#E6E0D8'/><Setter Property='BorderThickness' Value='1'/>
      <Setter Property='Height' Value='44'/><Setter Property='Padding' Value='18,0'/>
      <Setter Property='FontWeight' Value='SemiBold'/><Setter Property='Cursor' Value='Hand'/>
      <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
      <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
        <Border x:Name='Surface' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='11' Padding='{TemplateBinding Padding}'>
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
    <StackPanel>
      <Grid x:Name='TitleGrip' Background='Transparent' Margin='0,0,0,18'>
        <TextBlock Text='关闭助手' Foreground='#918678' FontSize='12' VerticalAlignment='Center'/>
        <Button x:Name='CancelButton' Content='×' HorizontalAlignment='Right' Width='30' Height='30' Padding='0' Background='Transparent' BorderThickness='0' FontSize='20' FontWeight='Normal' ToolTip='取消关闭' AutomationProperties.Name='取消关闭'/>
      </Grid>
      <TextBlock Text='确认要离开吗？' FontSize='21' FontWeight='SemiBold'/>
      <TextBlock Text='你可以选择退出，或让助手在托盘继续运行。' Foreground='#81796F' TextWrapping='Wrap' LineHeight='23' Margin='0,10,0,24'/>
      <Grid>
        <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='12'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
        <Button x:Name='ExitButton' Content='退出' IsDefault='True' Background='#EDB65E' BorderBrush='#EDB65E' Foreground='#382917'/>
        <Button x:Name='TrayButton' Grid.Column='2' Content='最小化到托盘'/>
      </Grid>
    </StackPanel>
  </Border>
</Window>");
            Window.WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner;
            if(owner!=null)Window.Owner=owner;
            ExitButton=(Button)Window.FindName("ExitButton");TrayButton=(Button)Window.FindName("TrayButton");
            ((Button)Window.FindName("CancelButton")).Click+=(s,e)=>Window.Close();
            ((Grid)Window.FindName("TitleGrip")).MouseLeftButtonDown+=(s,e)=>{if(e.LeftButton==MouseButtonState.Pressed)Window.DragMove();};
            ExitButton.Click+=(s,e)=>{choice=CloseChoice.Exit;Window.Close();};
            TrayButton.Click+=(s,e)=>{choice=CloseChoice.Tray;Window.Close();};
            Window.PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;Window.Close();}};
        }
        public CloseChoice Show() {Window.ShowDialog();return choice;}
    }
}
