[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\slot-tools.ps1')

$cases = [ordered]@{
    'main' = 'main'
    'codex/tools' = 'codex-tools'
    'feature/my-new-feature' = 'feature-my-new-feature'
    'Feature/Test_123' = 'feature-test-123'
    '---' = 'slot'
    'CON' = 'con-slot'
}

foreach ($case in $cases.GetEnumerator()) {
    $actual = Get-AgentstrationSlotId -Name $case.Key
    if ($actual -ne $case.Value) {
        throw "Expected '$($case.Key)' to normalize to '$($case.Value)', got '$actual'."
    }
}

$longName = 'feature/' + ('very-long-branch-name-' * 5)
$first = Get-AgentstrationSlotId -Name $longName
$second = Get-AgentstrationSlotId -Name $longName
if ($first -ne $second -or $first.Length -gt 63 -or $first -notmatch '^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$') {
    throw "Long slot normalization is not safe and deterministic: '$first'."
}

$ports = @(Get-AgentstrationFreeTcpPorts -Count 3)
if ($ports.Count -ne 3 -or @($ports | Select-Object -Unique).Count -ne 3) {
    throw "Dynamic infrastructure ports are not distinct: '$($ports -join ', ')'."
}

$runScript = Get-Content (Join-Path $PSScriptRoot '..\run.ps1') -Raw
if ($runScript -notmatch "'ASPIRE_ALLOW_UNSECURED_TRANSPORT'\s*=\s*'true'") {
    throw 'The slot launcher must explicitly allow its loopback HTTP Aspire endpoints.'
}

$dashboardUrl = Get-AgentstrationDashboardLoginUrl -Line "Login to the dashboard at $([char]27)[1mhttp://127.0.0.1:17134/login?t=test-token$([char]27)[0m"
if ($dashboardUrl -ne 'http://127.0.0.1:17134/login?t=test-token') {
    throw "Unable to extract the Aspire dashboard login URL: '$dashboardUrl'."
}
if ($null -ne (Get-AgentstrationDashboardLoginUrl -Line 'Application started without a dashboard URL.')) {
    throw 'A regular log line must not be treated as an Aspire dashboard URL.'
}

Write-Host "Slot tool tests passed ($($cases.Count + 5) cases)."
