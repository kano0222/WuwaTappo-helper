param([string]$FFmpeg='ffmpeg')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$work=Join-Path $root 'build\readme-preview'
$frames=Join-Path $work 'frames'
$output=Join-Path $root 'docs\images\readme-motion.png'
if (-not (Get-Command $FFmpeg -ErrorAction SilentlyContinue)) { throw 'FFmpeg is required to generate the preview. Use -FFmpeg to specify its path.' }
# Separate build output: never replace the installed helper or its runtime data.
& (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory $work
$process=Start-Process -FilePath (Join-Path $work 'WuwaTappoHelper.exe') -ArgumentList @('--render-preview',('"'+$frames+'"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw 'Simulated WPF frame generation failed.' }
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $output))
& $FFmpeg -hide_banner -loglevel error -y -framerate 1 -i (Join-Path $frames '%03d.png') -frames:v 11 -c:v apng -plays 0 -f apng $output
if ($LASTEXITCODE -ne 0) { throw 'APNG encoding failed.' }
Write-Output ('Generated: '+$output+' (11 seconds, infinite loop)')
