[CmdletBinding()]
param(
    [switch] $NoBrowser
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'slot-tools.ps1')

$repositoryRoot = Get-AgentstrationRepositoryRoot -StartPath $PSScriptRoot
$branchOutput = @(& git -C $repositoryRoot branch --show-current)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to determine the current Git branch.'
}
$branch = ($branchOutput -join '').Trim()

if ([string]::IsNullOrWhiteSpace($branch)) {
    $commitOutput = @(& git -C $repositoryRoot rev-parse --short=12 HEAD)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to determine the current Git commit.'
    }
    $commit = ($commitOutput -join '').Trim()
    $branchDisplay = "(detached at $commit)"
    $slot = Get-AgentstrationSlotId -Name "detached-$commit"
}
else {
    $branchDisplay = $branch
    $slot = Get-AgentstrationSlotId -Name $branch
}

$slotDataPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot ".agentstration\slots\$slot"))
[System.IO.Directory]::CreateDirectory($slotDataPath) | Out-Null

Write-Host 'Agentstration development slot'
Write-Host ''
Write-Host ("Slot:       {0}" -f $slot)
Write-Host ("Branch:     {0}" -f $branchDisplay)
Write-Host ("Worktree:   {0}" -f $repositoryRoot)
Write-Host ("Data:       {0}" -f $slotDataPath)
Write-Host ''
Write-Host 'Starting Agentstration.AppHost...'
Write-Host ''

$infrastructurePorts = @(Get-AgentstrationFreeTcpPorts -Count 3)
$environment = @{
    'Agentstration__Slot' = $slot
    'Agentstration__SlotDataPath' = $slotDataPath
    'ASPNETCORE_ENVIRONMENT' = 'Development'
    'DOTNET_ENVIRONMENT' = 'Development'
    'ASPIRE_ALLOW_UNSECURED_TRANSPORT' = 'true'
    'ASPNETCORE_URLS' = "http://127.0.0.1:$($infrastructurePorts[0])"
    'ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL' = "http://127.0.0.1:$($infrastructurePorts[1])"
    'ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL' = "http://127.0.0.1:$($infrastructurePorts[2])"
}
$previousEnvironment = @{}
$exitCode = 1

try {
    foreach ($entry in $environment.GetEnumerator()) {
        $previousEnvironment[$entry.Key] = [System.Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [System.Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }

    $browserOpened = $false
    & dotnet run --project (Join-Path $repositoryRoot 'src\Agentstration.AppHost') --no-launch-profile |
        ForEach-Object {
            $line = $_.ToString()
            Write-Host $line

            if (-not $NoBrowser -and -not $browserOpened) {
                $dashboardUrl = Get-AgentstrationDashboardLoginUrl -Line $line
                if ($null -ne $dashboardUrl) {
                    try {
                        Start-Process -FilePath $dashboardUrl
                        $browserOpened = $true
                        Write-Host 'Opened Aspire dashboard in the default browser.'
                    }
                    catch {
                        Write-Warning "Unable to open the Aspire dashboard automatically: $($_.Exception.Message)"
                    }
                }
            }
        }
    $exitCode = $LASTEXITCODE
}
finally {
    foreach ($entry in $previousEnvironment.GetEnumerator()) {
        [System.Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}

exit $exitCode
