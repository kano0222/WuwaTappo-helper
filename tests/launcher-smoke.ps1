param([string]$PackageRoot=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$root = $PackageRoot
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Tappo 启动器 ' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$files = @('Panel.ps1','Start.ps1','Control.ps1','dependencies\powershell\pwsh.exe','dependencies\node\node.exe','scripts\App-Discovery.ps1','scripts\Panel-Tray.ps1','scripts\Start-DebugSession.ps1','assets\helper.ico','app-profile.json','automation.mjs','lib\tappo.mjs','lib\compatibility.mjs','lib\modes.mjs','lib\settings.mjs','lib\storage.mjs')
function Run-Launcher([string]$Exe, [string]$Arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($Exe, $Arguments)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    # Match a desktop environment without Codex's injected tool paths.
    $info.Environment['PATH'] = "$env:WINDIR\System32;$env:WINDIR"
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (-not $process.WaitForExit(30000)) { $process.Kill($true); throw '启动器检查超时。' }
        return $process.ExitCode
    } finally { $process.Dispose() }
}
try {
    $launcher = Join-Path $fixture 'WuwaTappoHelper.exe'
    Copy-Item -LiteralPath (Join-Path $root 'WuwaTappoHelper.exe') -Destination $launcher
    foreach ($file in $files) {
        $target = Join-Path $fixture $file
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        [IO.File]::WriteAllText($target, '')
    }
    if ((Run-Launcher $launcher '--check') -ne 0) { throw '完整目录检查失败。' }
    foreach ($file in $files) {
        $target = Join-Path $fixture $file
        Remove-Item -LiteralPath $target
        if ((Run-Launcher $launcher '--check') -ne 1) { throw "未发现缺失文件：$file" }
        [IO.File]::WriteAllText($target, '')
    }
    $preview = Join-Path $fixture '界面预览.png'
    if ((Run-Launcher (Join-Path $root 'WuwaTappoHelper.exe') ('--render "' + $preview + '"')) -ne 0) { throw '随附环境无法渲染面板；请查看 runtime/panel-launcher-error.log。' }
    if (-not (Test-Path -LiteralPath $preview)) { throw '未生成面板预览。' }
    Write-Output "启动器检查通过：完整目录、$($files.Count) 种文件缺失、无系统依赖路径渲染；未执行后台操作。"
} finally {
    # Remove only known files from this test's unique temporary directory.
    foreach ($file in ($files + @('WuwaTappoHelper.exe','界面预览.png'))) {
        $target = Join-Path $fixture $file
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
    }
    foreach ($directory in @('dependencies\powershell','dependencies\node','dependencies','scripts','assets','lib','')) {
        Remove-Item -LiteralPath (Join-Path $fixture $directory)
    }
}
