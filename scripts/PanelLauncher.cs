using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal static class PanelLauncher
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool check = args.Length == 1 && args[0] == "--check";
        string root = AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            string panel = Required(root, "Panel.ps1");
            string shell = Required(root, @"dependencies\powershell\pwsh.exe");
            Required(root, @"dependencies\node\node.exe");
            Required(root, "Start.ps1");
            Required(root, "Control.ps1");
            foreach (string required in new [] { @"scripts\App-Discovery.ps1", @"scripts\Panel-Tray.ps1", @"scripts\Start-DebugSession.ps1", @"assets\helper.ico", "app-profile.json", "automation.mjs", @"lib\tappo.mjs", @"lib\compatibility.mjs", @"lib\modes.mjs", @"lib\settings.mjs", @"lib\storage.mjs" })
                Required(root, required);
            if (check) return 0;
            // CreateNoWindow suppresses the console; WindowStyle Hidden can also hide WPF windows.
            string arguments = "-NoLogo -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File " + Quote(panel);
            if (args.Length == 2 && args[0] == "--render")
                arguments += " -RenderPath " + Quote(Path.GetFullPath(args[1]));
            else if (args.Length != 0)
                throw new ArgumentException("不支持的启动参数。");
            var errors = new StringBuilder();
            var info = new ProcessStartInfo(shell, arguments) {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true,
                StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += delegate { };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) {
                    if (e.Data != null) lock (errors) {
                        if (errors.Length < 16000) errors.AppendLine(e.Data);
                    }
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("控制面板启动失败（退出码 " + process.ExitCode + "）。\n" + errors);
            }
            return 0;
        }
        catch (Exception error)
        {
            if (!check)
            {
                string message = error.Message;
                try {
                    string directory = Path.Combine(root, "runtime");
                    Directory.CreateDirectory(directory);
                    string log = Path.Combine(directory, "panel-launcher-error.log");
                    File.WriteAllText(log, DateTimeOffset.Now + "\n" + error, Encoding.UTF8);
                    message += "\n\n详细记录：" + log;
                } catch { }
                if (!(args.Length == 2 && args[0] == "--render"))
                    MessageBox.Show(message, "Wuwa Tappo · 无法打开控制面板", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }
    }

    private static string Required(string root, string relative)
    {
        string file = Path.Combine(root, relative);
        if (!File.Exists(file))
            throw new FileNotFoundException("缺少必要文件：" + relative + "\n请保留完整文件夹；从源码使用时，请先运行 scripts/Build-Launcher.ps1。");
        return file;
    }

    private static string Quote(string value)
    {
        // These arguments are file paths, which cannot contain a double quote on Windows.
        if (value.IndexOf('"') >= 0) throw new ArgumentException("路径包含无效字符。");
        return "\"" + value + "\"";
    }
}
