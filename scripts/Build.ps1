param([switch]$Test,[string]$OutputDirectory,[string]$Version)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows with .NET Framework 4.8 build tools is required.' }
$sources=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$releaseBuild=$false
if ($Version) { $releaseBuild=$true }
elseif ($env:GITHUB_REF_TYPE -eq 'tag') { $Version=$env:GITHUB_REF_NAME; $releaseBuild=$true }
else {
    if (Get-Command git -ErrorAction SilentlyContinue) { $Version=& git -C $root describe --tags --match 'v[0-9]*' --abbrev=0 2>$null }
    if (-not $Version) { $Version='0.0.0' }
}
if ($Version -notmatch '^v?(\d+)\.(\d+)\.(\d+)$') { throw 'Version must use vX.Y.Z or X.Y.Z format.' }
$buildVersion=$Version.TrimStart('v')
if (-not $releaseBuild) {
    $parts=$buildVersion.Split('.')
    $buildVersion=$parts[0]+'.'+$parts[1]+'.'+([int]$parts[2]+1)
}
$assemblyVersion=$buildVersion+'.0'
$localBuild=-not $releaseBuild -and $env:GITHUB_ACTIONS -ne 'true'
if ($localBuild) {
    $buildRoot=Join-Path $root 'build'
    $pattern='^'+[Regex]::Escape($buildVersion)+'\.(\d+)$'
    $revision=0
    if (Test-Path -LiteralPath $buildRoot) {
        foreach ($directory in Get-ChildItem -LiteralPath $buildRoot -Directory) {
            if ($directory.Name -match $pattern) { $revision=[Math]::Max($revision,[int]$Matches[1]) }
        }
    }
    $buildVersion=$buildVersion+'.'+($revision+1)
    $assemblyVersion=$buildVersion
}
if (-not $OutputDirectory) {
    $OutputDirectory=Join-Path $root 'build'
    # Keep the CI packaging contract; local development builds have their own revision.
    if ($env:GITHUB_ACTIONS -ne 'true') { $OutputDirectory=Join-Path $OutputDirectory $buildVersion }
}
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$displayVersion=if ($releaseBuild) { $buildVersion } else { $buildVersion+'-dev' }
$versionSource=Join-Path $OutputDirectory 'Version.cs'
[IO.File]::WriteAllText($versionSource,('[assembly:System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")][assembly:System.Reflection.AssemblyVersion("'+$assemblyVersion+'")][assembly:System.Reflection.AssemblyInformationalVersion("'+$displayVersion+'")]'))
$sources+=$versionSource
# JSON models are populated by JavaScriptSerializer rather than C# assignments.
$arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/nowarn:0649','/main:Tappo.Program',('/out:'+(Join-Path $OutputDirectory 'WuwaTappoHelper.exe')),('/win32icon:'+(Join-Path $root 'assets\helper.ico')),('/win32manifest:'+(Join-Path $root 'src\app.manifest')))
foreach ($assembly in @('System.dll','System.Core.dll','System.Net.Http.dll','System.Web.Extensions.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Xaml.dll','WPF\WindowsBase.dll','WPF\PresentationCore.dll','WPF\PresentationFramework.dll')) {
    $arguments+=('/reference:'+(Join-Path $framework $assembly))
}
$arguments+=('/resource:'+(Join-Path $root 'src\Panel.xaml')+',Panel.xaml')
$arguments+=('/resource:'+(Join-Path $root 'assets\helper.ico')+',helper.ico')
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'src\Bridge') -Filter '*.js') { $arguments+=('/resource:'+$file.FullName+',bridge.'+$file.Name) }
& $compiler @arguments @sources
if ($LASTEXITCODE -ne 0) { throw 'C# compilation failed.' }
$configuration='<?xml version="1.0"?><configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8"/></startup></configuration>'
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'WuwaTappoHelper.exe.config'),$configuration)
Write-Output ('Built: '+(Join-Path $OutputDirectory 'WuwaTappoHelper.exe'))
if ($Test) {
    $testArguments=@($arguments | Where-Object { $_ -notlike '/main:*' -and $_ -notlike '/out:*' -and $_ -notlike '/target:*' })
    $testArguments+=@('/target:exe','/main:Tappo.Tests',('/out:'+(Join-Path $OutputDirectory 'Tests.exe')))
    $testSources=@(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' | ForEach-Object { $_.FullName })
    & $compiler @testArguments @sources @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & (Join-Path $OutputDirectory 'Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'C# checks failed.' }
}
