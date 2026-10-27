param([switch]$Test,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory=Join-Path $root 'build' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '需要 Windows 的 .NET Framework 4.8 构建环境。' }
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
# JSON models are populated by JavaScriptSerializer rather than C# assignments.
$arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/nowarn:0649','/main:Tappo.Program',('/out:'+(Join-Path $OutputDirectory 'WuwaTappoHelper.exe')),('/win32icon:'+(Join-Path $root 'assets\helper.ico')),('/win32manifest:'+(Join-Path $root 'src\app.manifest')))
foreach ($assembly in @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) {
    $arguments+=('/reference:'+(Join-Path $framework $assembly))
}
$arguments+=('/resource:'+(Join-Path $root 'src\Panel.xaml')+',Panel.xaml')
$arguments+=('/resource:'+(Join-Path $root 'assets\helper.ico')+',helper.ico')
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'src\Bridge') -Filter '*.js') { $arguments+=('/resource:'+$file.FullName+',bridge.'+$file.Name) }
& $compiler @arguments @sources
if ($LASTEXITCODE -ne 0) { throw 'C# 编译失败。' }
$configuration='<?xml version="1.0"?><configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup></configuration>'
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'WuwaTappoHelper.exe.config'),$configuration)
Write-Output ('已生成：'+(Join-Path $OutputDirectory 'WuwaTappoHelper.exe'))
if ($Test) {
    $testArguments=@($arguments | Where-Object { $_ -notlike '/main:*' -and $_ -notlike '/out:*' -and $_ -notlike '/target:*' })
    $testArguments+=@('/target:exe','/main:Tappo.Tests',('/out:'+(Join-Path $OutputDirectory 'Tests.exe')))
    $testSources=@(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' | ForEach-Object { $_.FullName })
    & $compiler @testArguments @sources @testSources
    if ($LASTEXITCODE -ne 0) { throw '测试编译失败。' }
    & (Join-Path $OutputDirectory 'Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'C# 检查未通过。' }
}
