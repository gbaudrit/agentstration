[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $AssemblyPath,

    [Parameter(Mandatory)]
    [string] $ReportPath,

    [Parameter(Mandatory)]
    [ValidateRange(1, [int]::MaxValue)]
    [int] $MinimumExpectedTests,

    [ValidateRange(0, [int]::MaxValue)]
    [int] $WarningWorkingSetMiB = 0,

    [ValidateRange(0, [int]::MaxValue)]
    [int] $FailureWorkingSetMiB = 0
)

$ErrorActionPreference = 'Stop'
$resolvedAssembly = (Resolve-Path -LiteralPath $AssemblyPath).Path
$resolvedReport = [System.IO.Path]::GetFullPath($ReportPath)
$reportDirectory = Split-Path -Parent $resolvedReport
if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
    New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
}
$trxFileName = ".test-results-$([Guid]::NewGuid().ToString('N')).trx"
$trxPath = Join-Path $reportDirectory $trxFileName

$startInfo = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
$startInfo.ArgumentList.Add($resolvedAssembly)
$startInfo.ArgumentList.Add('--minimum-expected-tests')
$startInfo.ArgumentList.Add($MinimumExpectedTests.ToString([System.Globalization.CultureInfo]::InvariantCulture))
$startInfo.ArgumentList.Add('--progress')
$startInfo.ArgumentList.Add('off')
$startInfo.ArgumentList.Add('--report-trx')
$startInfo.ArgumentList.Add('--report-trx-filename')
$startInfo.ArgumentList.Add($trxFileName)
$startInfo.ArgumentList.Add('--results-directory')
$startInfo.ArgumentList.Add($reportDirectory)
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $startInfo
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
if (-not $process.Start()) {
    throw "Could not start test module '$resolvedAssembly'."
}

$standardOutput = $process.StandardOutput.ReadToEndAsync()
$standardError = $process.StandardError.ReadToEndAsync()
$peakWorkingSetBytes = 0L
$peakPrivateBytes = 0L
$peakAggregateDotnetWorkingSetBytes = 0L
while (-not $process.HasExited) {
    $process.Refresh()
    $peakWorkingSetBytes = [Math]::Max($peakWorkingSetBytes, $process.WorkingSet64)
    $peakPrivateBytes = [Math]::Max($peakPrivateBytes, $process.PrivateMemorySize64)
    $aggregateDotnetWorkingSetBytes = 0L
    foreach ($dotnetProcess in [System.Diagnostics.Process]::GetProcessesByName('dotnet')) {
        try {
            $dotnetProcess.Refresh()
            $aggregateDotnetWorkingSetBytes += $dotnetProcess.WorkingSet64
        }
        catch [System.InvalidOperationException] {
            # A process can exit between enumeration and sampling.
        }
        finally {
            $dotnetProcess.Dispose()
        }
    }
    $peakAggregateDotnetWorkingSetBytes = [Math]::Max($peakAggregateDotnetWorkingSetBytes, $aggregateDotnetWorkingSetBytes)
    Start-Sleep -Milliseconds 100
}
$process.WaitForExit()
$stopwatch.Stop()
$null = $standardOutput.GetAwaiter().GetResult()
$null = $standardError.GetAwaiter().GetResult()

$testCount = 0
if (Test-Path -LiteralPath $trxPath) {
    try {
        [xml] $trx = Get-Content -LiteralPath $trxPath -Raw
        $counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
        if ($null -ne $counters) {
            $testCount = [int]$counters.GetAttribute('total')
        }
    }
    finally {
        [System.IO.File]::Delete($trxPath)
    }
}
$workingSetMiB = [Math]::Round($peakWorkingSetBytes / 1MB, 1)
$privateMiB = [Math]::Round($peakPrivateBytes / 1MB, 1)
$aggregateDotnetWorkingSetMiB = [Math]::Round($peakAggregateDotnetWorkingSetBytes / 1MB, 1)
$moduleName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedAssembly)
$status = if ($process.ExitCode -ne 0 -or
    $testCount -lt $MinimumExpectedTests -or
    ($FailureWorkingSetMiB -gt 0 -and $workingSetMiB -ge $FailureWorkingSetMiB)) {
    'failed'
}
elseif ($WarningWorkingSetMiB -gt 0 -and $workingSetMiB -ge $WarningWorkingSetMiB) {
    'warning'
}
else {
    'passed'
}

[ordered]@{
    module = $moduleName
    status = $status
    testCount = $testCount
    minimumExpectedTests = $MinimumExpectedTests
    durationSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
    peakWorkingSetMiB = $workingSetMiB
    peakPrivateMemoryMiB = $privateMiB
    peakAggregateDotnetWorkingSetMiB = $aggregateDotnetWorkingSetMiB
    warningWorkingSetMiB = $WarningWorkingSetMiB
    failureWorkingSetMiB = $FailureWorkingSetMiB
    exitCode = $process.ExitCode
    runtime = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
    os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
} | ConvertTo-Json | Set-Content -LiteralPath $resolvedReport -Encoding utf8

$summary = "${moduleName}: $testCount tests, $([Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) s, $workingSetMiB MiB working set, $privateMiB MiB private, $aggregateDotnetWorkingSetMiB MiB aggregate dotnet, status $status"
Write-Host $summary
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    "| $moduleName | $testCount | $([Math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) | $workingSetMiB | $privateMiB | $aggregateDotnetWorkingSetMiB | $status |" |
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
}

if ($process.ExitCode -ne 0) {
    throw "Test module '$moduleName' exited with code $($process.ExitCode). Rerun it directly for failure details."
}
if ($testCount -lt $MinimumExpectedTests) {
    throw "Test module '$moduleName' discovered $testCount tests; expected at least $MinimumExpectedTests."
}
if ($FailureWorkingSetMiB -gt 0 -and $workingSetMiB -ge $FailureWorkingSetMiB) {
    throw "Test module '$moduleName' peaked at $workingSetMiB MiB working set; failure budget is $FailureWorkingSetMiB MiB."
}
if ($status -eq 'warning') {
    Write-Warning "Test module '$moduleName' peaked at $workingSetMiB MiB working set; warning budget is $WarningWorkingSetMiB MiB."
}
