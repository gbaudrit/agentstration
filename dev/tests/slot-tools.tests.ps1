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
if ($runScript -notmatch "'Agentstration__DynamicApplicationPorts'\s*=\s*'true'") {
    throw 'The slot launcher must request dynamic application ports from the AppHost.'
}
if ($runScript -notmatch "'Agentstration__Bootstrap__Path'\s*=\s*\`$bootstrapPath") {
    throw 'The slot launcher must pass the development bootstrap catalog to the AppHost.'
}
if ($runScript -notmatch "'Agentstration__Bootstrap__InitialBootstrapEnabled'\s*=\s*'true'") {
    throw 'The slot launcher must enable initial bootstrap.'
}
if ($runScript -notmatch "'Agentstration__Bootstrap__InitialProfiles__0'\s*=\s*'development'") {
    throw 'The slot launcher must apply the development bootstrap profile.'
}
if ($runScript -notmatch '& dotnet build \$appHostProject') {
    throw 'The slot launcher must show the AppHost build before announcing startup.'
}
if ($runScript -notmatch '& dotnet run --project \$appHostProject --no-build --no-launch-profile') {
    throw 'The slot launcher must not rebuild silently during startup.'
}

$appHost = Get-Content (Join-Path $PSScriptRoot '..\..\src\Agentstration.AppHost\Program.cs') -Raw
if ($appHost -notmatch '\.WithEnvironment\("Data__Directory",\s*slotDataPath\)') {
    throw 'The AppHost must configure Data:Directory with the slot data root.'
}
if ($appHost -match '\.WithEnvironment\("Data__Path"') {
    throw 'The AppHost must not use the obsolete Data:Path setting.'
}
if (([regex]::Matches($appHost, '\.WithDynamicHostPorts\(dynamicApplicationPorts\)')).Count -ne 6) {
    throw 'Every project resource must use dynamic host ports when the slot launcher requests them.'
}

$dashboardUrl = Get-AgentstrationDashboardLoginUrl -Line "Login to the dashboard at $([char]27)[1mhttp://127.0.0.1:17134/login?t=test-token$([char]27)[0m"
if ($dashboardUrl -ne 'http://127.0.0.1:17134/login?t=test-token') {
    throw "Unable to extract the Aspire dashboard login URL: '$dashboardUrl'."
}
if ($null -ne (Get-AgentstrationDashboardLoginUrl -Line 'Application started without a dashboard URL.')) {
    throw 'A regular log line must not be treated as an Aspire dashboard URL.'
}

Write-Host "Slot tool tests passed ($($cases.Count + 14) cases)."
