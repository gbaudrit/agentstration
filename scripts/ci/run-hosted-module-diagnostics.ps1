[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [hashtable[]] $Modules,

    [string] $AssemblyRoot = 'tests',

    [string] $ReportDirectory = 'artifacts/test-diagnostics'
)

$ErrorActionPreference = 'Stop'
$failedModules = @()
foreach ($module in $Modules) {
    try {
        & (Join-Path $PSScriptRoot 'run-test-module-with-diagnostics.ps1') `
            -AssemblyPath (Join-Path $AssemblyRoot "$($module.Name)/bin/Release/net10.0/$($module.Name).dll") `
            -ReportPath (Join-Path $ReportDirectory "$($module.Name).json") `
            -MinimumExpectedTests $module.Tests `
            -WarningWorkingSetMiB $module.Warning `
            -FailureWorkingSetMiB $module.Failure
    }
    catch {
        # Each module still has a separate process and the same memory budget.
        # Finish collecting evidence, then fail the group as a whole.
        $failedModules += $module.Name
        Write-Warning "Hosted diagnostics failed for '$($module.Name)'. Check its report, or the assembly path if no report was created."
    }
}
if ($failedModules.Count -gt 0) {
    throw "Hosted diagnostics failed for: $($failedModules -join ', '). All selected modules were attempted."
}
