param([string]$PowerShellHome,[string]$NodePath,[string]$NodeLicensePath)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$dist=Join-Path $root 'dist'
[void][IO.Directory]::CreateDirectory($dist)
$name='WuwaTappoHelper-win-x64-'+[DateTime]::Now.ToString('yyyyMMdd-HHmmss')
$stage=Join-Path $dist $name
if (Test-Path -LiteralPath $stage) { throw '同名分发目录已存在，请稍后再构建。' }
[void][IO.Directory]::CreateDirectory($stage)
# An allowlist prevents local paths, account records and diagnostic captures entering a release.
foreach ($file in @('Panel.ps1','Start.ps1','Control.ps1','automation.mjs','app-profile.json','README.md','config.example.json')) {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $stage
}
foreach ($directory in @('lib','assets')) { [void][IO.Directory]::CreateDirectory((Join-Path $stage $directory)) }
foreach ($file in @('lib\tappo.mjs','lib\compatibility.mjs','lib\modes.mjs','lib\settings.mjs','lib\storage.mjs','assets\helper.ico')) {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination (Join-Path $stage $file)
}
[void][IO.Directory]::CreateDirectory((Join-Path $stage 'scripts'))
foreach ($file in @('App-Discovery.ps1','Panel-Tray.ps1','Start-DebugSession.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $stage 'scripts')
}
if (-not $PowerShellHome) {
    $PowerShellHome=if (Test-Path -LiteralPath (Join-Path $root 'dependencies\powershell\pwsh.exe')) {Join-Path $root 'dependencies\powershell'} else {$PSHOME}
}
if (-not $NodePath) { $NodePath=if (Test-Path -LiteralPath (Join-Path $root 'dependencies\node\node.exe')) {Join-Path $root 'dependencies\node\node.exe'} else {(Get-Command node -ErrorAction Stop).Source} }
if (-not $NodeLicensePath -and (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $NodePath) 'LICENSE'))) { $NodeLicensePath=Join-Path (Split-Path -Parent $NodePath) 'LICENSE' }
& (Join-Path $PSScriptRoot 'Build-Launcher.ps1') -PowerShellHome $PowerShellHome -NodePath $NodePath -NodeLicensePath $NodeLicensePath -OutputDirectory $stage
if (Test-Path -LiteralPath (Join-Path $stage 'runtime')) { throw '分发包意外包含运行记录。' }
if (Test-Path -LiteralPath (Join-Path $stage 'config.local.json')) { throw '分发包意外包含本机配置。' }
$zip=Join-Path $dist ($name+'.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
Write-Output "分发目录：$stage"
Write-Output "分发压缩包：$zip"
