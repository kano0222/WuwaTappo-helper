param(
    [string]$PowerShellHome = $PSHOME,
    [string]$NodePath = (Get-Command node -ErrorAction Stop).Source,
    [string]$NodeLicensePath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory=$root }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows 自带的 .NET Framework 4.x C# 编译器。' }
$shell = Join-Path $PowerShellHome 'pwsh.exe'
if (-not (Test-Path -LiteralPath $shell)) { throw '请通过 PowerShell 7 运行，或指定 -PowerShellHome。' }
$psVersion = (& $shell -NoProfile -Command '$PSVersionTable.PSVersion.ToString()').Trim()
if ($LASTEXITCODE -ne 0 -or [version]$psVersion -lt [version]'7.0') { throw '无法确认 PowerShell 7 运行环境。' }
$nodeVersion = (& $NodePath --version).Trim()
if ($LASTEXITCODE -ne 0 -or [int]$nodeVersion.TrimStart('v').Split('.')[0] -lt 22) { throw '需要 Node.js 22 或更新版本。' }
$dependencies = Join-Path $OutputDirectory 'dependencies'
$psDestination = Join-Path $dependencies 'powershell'
$nodeDestination = Join-Path $dependencies 'node'
if ([IO.Path]::GetFullPath($PowerShellHome).TrimEnd('\') -eq $psDestination) { throw '构建源不能使用项目内已复制的 PowerShell。' }
[void][IO.Directory]::CreateDirectory($psDestination)
[void][IO.Directory]::CreateDirectory($nodeDestination)
# Keep the complete PowerShell distribution, including its original license notices.
Copy-Item -Path (Join-Path $PowerShellHome '*') -Destination $psDestination -Recurse -Force
Copy-Item -LiteralPath $NodePath -Destination (Join-Path $nodeDestination 'node.exe') -Force
$nodeLicense = Join-Path $nodeDestination 'LICENSE'
if ($NodeLicensePath) {
    if ([IO.Path]::GetFullPath($NodeLicensePath) -ne $nodeLicense) { Copy-Item -LiteralPath $NodeLicensePath -Destination $nodeLicense -Force }
    if (-not (Test-Path -LiteralPath $nodeLicense)) { throw '缺少指定的 Node.js 许可证。' }
} else {
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/nodejs/node/$nodeVersion/LICENSE" -OutFile $nodeLicense
}
@{ powerShell=$psVersion; node=$nodeVersion } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dependencies 'versions.json') -Encoding utf8
$output = Join-Path $OutputDirectory 'WuwaTappoHelper.exe'
& $compiler /nologo /target:winexe /platform:x64 /reference:System.Windows.Forms.dll "/win32icon:$(Join-Path $root 'assets\helper.ico')" "/out:$output" (Join-Path $PSScriptRoot 'PanelLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw '启动器编译失败。' }
Write-Output "已生成：$output"
Write-Output '发布时须同时附带 dependencies 目录、面板与后台源码、示例配置及第三方许可文件。'
