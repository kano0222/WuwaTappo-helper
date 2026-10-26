# Exercise the real UI JSON reader against atomic file replacement, in an isolated directory.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'Panel.ps1') -RenderPath (Join-Path $root 'runtime\panel-preview.png')
$originalRuntime=$script:Runtime
$testRuntime=Join-Path $originalRuntime ('panel-file-sharing-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRuntime)
$script:Runtime=$testRuntime
[IO.File]::WriteAllText((Join-Path $testRuntime 'status.json'),'{"v":0}')
$program=@'
const fs=require('fs'),p=require('path'),dir=process.argv[1];
(async()=>{const {replaceSnapshot}=await import(process.argv[2]);let failed=0;for(let i=1;i<=500;i++){
  fs.writeFileSync(p.join(dir,'status.tmp'),JSON.stringify({v:i}));
  try{replaceSnapshot(p.join(dir,'status.tmp'),p.join(dir,'status.json'));}catch{failed++;}
  await new Promise(r=>setTimeout(r,1));
}console.log(JSON.stringify({writes:500,failed}));process.exitCode=failed?1:0;})();
'@
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=(Get-Command node).Source
$start.UseShellExecute=$false; $start.CreateNoWindow=$true
$start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
$start.ArgumentList.Add('-e'); $start.ArgumentList.Add($program); $start.ArgumentList.Add($testRuntime)
$start.ArgumentList.Add(([uri](Join-Path $root 'lib\storage.mjs')).AbsoluteUri)
$worker=[Diagnostics.Process]::Start($start)
$reads=0
try {
    do {
        $value=Read-JsonFile 'status.json'
        if ($null -eq $value -or $value.v -lt 0 -or $value.v -gt 500) { throw 'Invalid snapshot while replacing status file' }
        $reads++
    } while (-not $worker.HasExited)
    if (-not $worker.WaitForExit(5000)) { throw 'File-sharing worker did not exit' }
    $result=$worker.StandardOutput.ReadToEnd().Trim()
    if ($worker.ExitCode -ne 0) { throw ('Atomic replacement failed: '+$result+' '+$worker.StandardError.ReadToEnd()) }
    Write-Output ("File-sharing regression passed: $reads reads; $result")
} finally {
    $script:Runtime=$originalRuntime
    if ($worker.HasExited) {
        [IO.File]::Delete((Join-Path $testRuntime 'status.json'))
        [IO.File]::Delete((Join-Path $testRuntime 'status.tmp'))
        [IO.Directory]::Delete($testRuntime)
    }
}
