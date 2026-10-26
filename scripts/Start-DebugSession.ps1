param([Parameter(Mandatory)][string]$AppPath)
$ErrorActionPreference = 'Stop'
$runtime = Join-Path (Split-Path -Parent $PSScriptRoot) 'runtime'
[void][IO.Directory]::CreateDirectory($runtime)
$policyPath = 'Software\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments'
$policyName = 'WuwaTappo.exe'
$policyValue = '--remote-debugging-address=127.0.0.1 --remote-debugging-port=19222'
$resultFile = Join-Path $runtime 'debug-policy-session.json'
$registryBase = $null
$original = $null
$modified = $false
$createdKeys = @()
$record = [ordered]@{ startedAt = [DateTimeOffset]::Now.ToString('o'); launched = $false; policyRestored = $false }
try {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator launcher required.' }
    if (Get-Process -Name WuwaTappo -ErrorAction SilentlyContinue) { throw 'WuwaTappo is still running; no configuration changed.' }
    $portCheck = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 19222)
    try { $portCheck.Start() } finally { $portCheck.Stop() }
    $registryBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $existing = $registryBase.OpenSubKey($policyPath)
    $original = @{ exists = $false; value = $null; kind = $null }
    if ($existing) {
        try {
            if ($policyName -in $existing.GetValueNames()) {
                $original.exists = $true
                $original.value = $existing.GetValue($policyName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $original.kind = $existing.GetValueKind($policyName).ToString()
            }
        } finally { $existing.Dispose() }
    }
    $currentPath = ''
    foreach ($part in $policyPath.Split('\')) {
        $currentPath = if ($currentPath) { "$currentPath\$part" } else { $part }
        $key = $registryBase.OpenSubKey($currentPath)
        if ($key) { $key.Dispose() } else { $createdKeys += $currentPath }
    }
    $record.originalPolicy = $original
    $record.createdKeys = $createdKeys
    $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultFile -Encoding utf8
    $policyKey = $registryBase.CreateSubKey($policyPath)
    try {
        $modified = $true
        $policyKey.SetValue($policyName, $policyValue, [Microsoft.Win32.RegistryValueKind]::String)
    } finally { $policyKey.Dispose() }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $AppPath
    $startInfo.WorkingDirectory = Split-Path -Parent $AppPath
    $startInfo.UseShellExecute = $false
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $started = [Diagnostics.Process]::Start($startInfo)
    $record.launched = $true
    $record.processId = $started.Id
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $listeners = @(Get-NetTCPConnection -State Listen -LocalPort 19222 -ErrorAction SilentlyContinue)
        if ($listeners.Count) { break }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $listeners.Count) { throw 'Debug port did not appear within 25 seconds.' }
    if (@($listeners | Where-Object { $_.LocalAddress -notin @('127.0.0.1','::1') }).Count) { throw 'Debug port is not restricted to loopback; do not continue probing.' }
    $record.listeners = @($listeners | ForEach-Object { @{ address = $_.LocalAddress; port = $_.LocalPort; processId = $_.OwningProcess } })
    $version = Invoke-RestMethod -Uri 'http://127.0.0.1:19222/json/version' -TimeoutSec 4
    $record.browser = $version.Browser
    $record.debugReady = $true
} catch {
    $record.error = $_.Exception.Message
} finally {
    try {
        if ($modified) {
            $key = $registryBase.OpenSubKey($policyPath, $true)
            try {
                # Do not overwrite a concurrent third-party edit.
                if ($key.GetValue($policyName) -cne $policyValue) { throw 'Policy changed concurrently; automatic restore stopped.' }
                if ($original.exists) {
                    $key.SetValue($policyName, $original.value, [Microsoft.Win32.RegistryValueKind]([Enum]::Parse([Microsoft.Win32.RegistryValueKind],$original.kind)))
                } else { $key.DeleteValue($policyName, $false) }
            } finally { if ($key) { $key.Dispose() } }
            for ($index = $createdKeys.Count - 1; $index -ge 0; $index--) {
                $key = $registryBase.OpenSubKey($createdKeys[$index])
                if ($key) {
                    try { $empty = $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0 } finally { $key.Dispose() }
                    if ($empty) { $registryBase.DeleteSubKey($createdKeys[$index], $false) }
                }
            }
            $record.policyRestored = $true
        }
    } catch { $record.restoreError = $_.Exception.Message }
    if ($registryBase) { $registryBase.Dispose() }
    $record.finishedAt = [DateTimeOffset]::Now.ToString('o')
    $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $resultFile -Encoding utf8
}
