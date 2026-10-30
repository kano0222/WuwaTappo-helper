param([ValidateSet('All','Worker','Panel','Platform','Bridge','Discovery','Boost','Claim','State')][string]$Suite='All')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
if ($Suite -in @('All','Worker','Panel','Platform')) {
    & (Join-Path $PSScriptRoot 'Build.ps1') -Test -TestSuite $Suite
}
$bridges=switch ($Suite) {
    { $_ -in @('All','Bridge') } { 'discovery','boost','claim','state' }
    { $_ -in @('Discovery','Boost','Claim','State') } { $_.ToLowerInvariant() }
}
foreach ($bridge in $bridges) {
    & node (Join-Path $root "tests/$bridge.cjs")
    if ($LASTEXITCODE -ne 0) { throw "$bridge tests failed." }
}
