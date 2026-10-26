function Get-TappoInstallExecutable([string]$InstallLocation) {
    if (-not $InstallLocation) { return $null }
    # Installers may quote InstallLocation. Combine paths without consulting PowerShell drives.
    $directory=[Environment]::ExpandEnvironmentVariables($InstallLocation.Trim().Trim('"',"'"))
    if (-not [IO.Path]::IsPathFullyQualified($directory)) { return $null }
    return [IO.Path]::Combine($directory,'WuwaTappo.exe')
}

function Get-TappoCandidates([string]$Root) {
    $config = Join-Path $Root 'config.local.json'
    if (Test-Path -LiteralPath $config) {
        try { (Get-Content -LiteralPath $config -Raw -Encoding utf8 | ConvertFrom-Json).appPath } catch { }
    }
    Get-Process -Name WuwaTappo -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Path } catch { } }
    foreach ($hive in @('HKCU:\Software','HKLM:\Software','HKLM:\Software\WOW6432Node')) {
        $appKey = Join-Path $hive 'Microsoft\Windows\CurrentVersion\App Paths\WuwaTappo.exe'
        if (Test-Path -LiteralPath $appKey) { (Get-Item -LiteralPath $appKey).GetValue('') }
        $uninstall = Join-Path $hive 'Microsoft\Windows\CurrentVersion\Uninstall'
        if (Test-Path -LiteralPath $uninstall) {
            Get-ItemProperty -Path (Join-Path $uninstall '*') -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -match '(?i)wuwa\s*tappo' } | ForEach-Object {
                    if ($_.InstallLocation) { Get-TappoInstallExecutable ([string]$_.InstallLocation) }
                    if ($_.DisplayIcon -match '^"([^"]+\.exe)"|^([^,]+\.exe)') {
                        if ($Matches[1]) { $Matches[1] } else { $Matches[2] }
                    }
                }
        }
    }
    Join-Path $Root 'WuwaTappo.exe'
    Join-Path $Root 'WuwaTappo\WuwaTappo.exe'
    foreach ($base in @($env:ProgramFiles,${env:ProgramFiles(x86)},(Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if ($base) { Join-Path $base 'WuwaTappo\WuwaTappo.exe'; Join-Path $base 'Wuwa Tappo\WuwaTappo.exe' }
    }
}

function Test-TappoApp([string]$Path,[string]$Root) {
    if (-not $Path -or -not [IO.Path]::IsPathFullyQualified($Path) -or [IO.Path]::GetFileName($Path) -ine 'WuwaTappo.exe' -or -not [IO.File]::Exists($Path)) { return $false }
    try {
        # Installation identity only; runtime capability checks decide compatibility.
        $info=[Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        return $info.ProductName -ceq 'Wuwa Tappo' -and $info.FileDescription -ceq 'Wuwa Tappo'
    } catch { return $false }
}

function Find-TappoApp([string]$Root,[string[]]$Candidates) {
    if (-not $PSBoundParameters.ContainsKey('Candidates')) { $Candidates = @(Get-TappoCandidates $Root) }
    foreach ($candidate in ($Candidates | Where-Object { $_ } | Select-Object -Unique)) {
        if (Test-TappoApp $candidate $Root) { return [IO.Path]::GetFullPath($candidate) }
    }
    return $null
}

function Save-TappoApp([string]$Root,[string]$Path) {
    if (-not (Test-TappoApp $Path $Root)) { throw '请选择 Wuwa Tappo 安装目录中的 WuwaTappo.exe；文件不存在或应用标识不匹配。' }
    $config = Join-Path $Root 'config.local.json'
    if (Test-Path -LiteralPath $config) {
        try { if ((Get-Content -LiteralPath $config -Raw -Encoding utf8 | ConvertFrom-Json).appPath -eq $Path) { return } } catch { }
    }
    $temporary = $config + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [IO.File]::WriteAllText($temporary, (@{appPath=$Path} | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $config -Force
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}

function Resolve-TappoApp([string]$Root,[switch]$Interactive,[switch]$Choose,$Owner) {
    $path = if (-not $Choose) { Find-TappoApp $Root } else { $null }
    if (-not $path -and $Interactive) {
        $dialog = [Microsoft.Win32.OpenFileDialog]::new()
        $dialog.Title = '选择已安装的 WuwaTappo.exe'
        $dialog.Filter = 'Wuwa Tappo (WuwaTappo.exe)|WuwaTappo.exe'
        $dialog.CheckFileExists = $true
        $accepted = if ($Owner) { $dialog.ShowDialog($Owner) } else { $dialog.ShowDialog() }
        if (-not $accepted) { throw '未选择桌宠程序。可稍后点击“选择程序”重新设置。' }
        $path = $dialog.FileName
    }
    if (-not $path) { throw '未找到桌宠。请在控制面板点击“选择程序”，指定 WuwaTappo.exe。' }
    Save-TappoApp $Root $path
    return $path
}
