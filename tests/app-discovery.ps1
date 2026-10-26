$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\App-Discovery.ps1')
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('Tappo discovery '+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
foreach ($name in @('supported','unsupported')) { [void][IO.Directory]::CreateDirectory((Join-Path $fixture $name)) }
$supported=Join-Path $fixture 'supported\WuwaTappo.exe'
$unsupported=Join-Path $fixture 'unsupported\WuwaTappo.exe'
try {
    $source=Join-Path $fixture 'fixture.cs'
    '[assembly:System.Reflection.AssemblyProduct("Wuwa Tappo")][assembly:System.Reflection.AssemblyTitle("Wuwa Tappo")] class Fixture { static void Main() {} }' | Set-Content -LiteralPath $source
    $compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $compiler /nologo /target:winexe (('/out:'+$supported)) $source
    if ($LASTEXITCODE -ne 0) { throw '无法构建测试程序。' }
    [IO.File]::WriteAllText($unsupported,'unsupported test fixture, never executed')
    @{profiles=@(@{version='old';executableSha256='outdated-hash'})} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixture 'app-profile.json')
    if ((Find-TappoApp -Root $fixture -Candidates @($unsupported,$supported)) -ne $supported) { throw '没有跳过不兼容发行文件。' }
    if (Find-TappoApp -Root $fixture -Candidates @('.\WuwaTappo.exe',$unsupported)) { throw '接受了无效路径或不兼容版本。' }
    foreach ($location in @((Split-Path $supported),('"'+(Split-Path $supported)+'"'),("'"+(Split-Path $supported)+"'"))) {
        if ((Get-TappoInstallExecutable $location) -ne $supported) { throw '带引号的安装记录未正确解析。' }
    }
    if ((Get-TappoInstallExecutable '"Z:\Missing App"') -ne 'Z:\Missing App\WuwaTappo.exe') { throw '未知盘符导致路径拼接失败。' }
    if (Test-TappoApp 'Z:\Missing App\WuwaTappo.exe' $fixture) { throw '接受了不存在的安装位置。' }
    if ((Find-TappoApp -Root $fixture -Candidates @('Z:\Missing App\WuwaTappo.exe',$supported)) -ne $supported) { throw '失效盘符中断了后续查找。' }
    Save-TappoApp $fixture $supported
    $config=Join-Path $fixture 'config.local.json'
    if ((Get-Content -LiteralPath $config -Raw | ConvertFrom-Json).appPath -ne $supported) { throw '选择没有保存。' }
    $rejected=$false
    try { Save-TappoApp $fixture $unsupported } catch { $rejected=$true }
    if (-not $rejected -or (Get-Content -LiteralPath $config -Raw | ConvertFrom-Json).appPath -ne $supported) { throw '无效选择覆盖了原配置。' }
    function Get-TappoCandidates { return @((Join-Path $fixture 'missing\WuwaTappo.exe'),$unsupported,$supported) }
    [IO.File]::WriteAllText($config,'{invalid json')
    if ((Resolve-TappoApp -Root $fixture) -ne $supported) { throw '自动发现未修复失效配置。' }
    Write-Output '程序查找检查通过：带引号安装记录、失效盘符、不兼容跳过、保存、拒绝覆盖、失效配置重新发现。'
} finally {
    foreach ($file in @('fixture.cs','supported\WuwaTappo.exe','unsupported\WuwaTappo.exe','app-profile.json','config.local.json')) {
        $target=Join-Path $fixture $file
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target }
    }
    foreach ($directory in @('supported','unsupported','')) { Remove-Item -LiteralPath (Join-Path $fixture $directory) }
}
