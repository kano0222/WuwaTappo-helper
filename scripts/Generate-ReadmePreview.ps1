param([string]$FFmpeg='ffmpeg',[string]$Version)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$images=Join-Path $root 'docs\images'
if (-not (Get-Command $FFmpeg -ErrorAction SilentlyContinue)) { throw 'FFmpeg is required to generate the preview. Use -FFmpeg to specify its path.' }
# Separate build output: never replace the installed helper or its runtime data.
$buildParameters=@{}
if ($Version) {
    $buildParameters.Version=$Version
    $buildParameters.OutputDirectory=Join-Path $root ('build\readme-preview-'+[guid]::NewGuid().ToString('N'))
}
$buildOutput=@(& (Join-Path $PSScriptRoot 'Build.ps1') @buildParameters)
$buildOutput | Write-Output
$built=@($buildOutput | Where-Object { $_ -is [string] -and $_.StartsWith('Built: ') })
if ($built.Count -ne 1) { throw 'Could not locate the preview build output.' }
$work=Split-Path -Parent $built[0].Substring(7)
$frames=Join-Path $work ('preview-frames-'+[guid]::NewGuid().ToString('N'))
$process=Start-Process -FilePath (Join-Path $work 'WuwaTappoHelper.exe') -ArgumentList @('--render-preview',('"'+$frames+'"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'Simulated WPF frame generation failed.' }
$motion=Join-Path $frames 'readme-motion.png'
& $FFmpeg -hide_banner -loglevel error -y -framerate 1 -i (Join-Path $frames '%03d.png') -c:v apng -plays 0 -f apng $motion
if ($LASTEXITCODE -ne 0) { throw 'APNG encoding failed.' }
$mini=Join-Path $frames 'readme-mini.png'
if (-not (Test-Path -LiteralPath $mini -PathType Leaf)) { throw 'Mini panel preview was not generated.' }
[void][IO.Directory]::CreateDirectory($images)
foreach ($name in @('readme-motion.png','readme-mini.png')) {
    Copy-Item -LiteralPath (Join-Path $frames $name) -Destination $images
    Write-Output ('Generated: '+(Join-Path $images $name))
}
