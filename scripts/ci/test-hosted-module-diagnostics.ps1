$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
function Require($Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
$temp = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
$previousMode = $env:AGENTSTRATION_DIAGNOSTICS_TEST_MODE
$previousSummary = $env:GITHUB_STEP_SUMMARY
New-Item -ItemType Directory $temp | Out-Null
try {
    # A real, package-free process exercises exit codes, TRX parsing and sampling.
    $project = Join-Path $temp 'Fixture.csproj'
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>' |
        Set-Content $project
    '<configuration><packageSources><clear /></packageSources></configuration>' |
        Set-Content (Join-Path $temp 'NuGet.Config')
    @'
var mode = Environment.GetEnvironmentVariable("AGENTSTRATION_DIAGNOSTICS_TEST_MODE");
string Argument(string name) => args[Array.IndexOf(args, name) + 1];
var report = Path.Combine(Argument("--results-directory"), Argument("--report-trx-filename"));
if (mode != "missing")
{
    File.WriteAllText(report, mode == "invalid" ? "<invalid" : mode == "no-counters" ? "<TestRun />" :
        mode == "invalid-counters" ? "<TestRun><Counters total=\"2\" failed=\"0\" /></TestRun>" :
        $"<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><ResultSummary><Counters total=\"2\" passed=\"{(mode is "failed" or "skipped-minimum" ? 1 : 2)}\" failed=\"{(mode == "failed" ? 1 : 0)}\" /></ResultSummary></TestRun>");
}
Console.WriteLine("PRIVATE_TEST_OUTPUT_SENTINEL");
Console.Error.WriteLine("PRIVATE_TEST_ERROR_SENTINEL");
Thread.Sleep(400);
return mode == "exit" ? 7 : mode == "skipped-minimum" ? 9 : 0;
'@ | Set-Content (Join-Path $temp 'Program.cs')
    $assemblyRoot = Join-Path $temp 'tests'
    $output = Join-Path $assemblyRoot 'Fixture/bin/Release/net10.0'
    dotnet build $project --configuration Release --output $output --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Could not build diagnostic test fixture.' }
    $summary = Join-Path $temp 'summary.md'
    $env:GITHUB_STEP_SUMMARY = $summary
    $report = Join-Path $temp 'report.json'
    function Run-Case([string] $Mode, [string[]] $Reasons, [int] $Minimum = 2, [int] $Warning = 0, [int] $Failure = 0) {
        $env:AGENTSTRATION_DIAGNOSTICS_TEST_MODE = $Mode
        $caught = $false
        try {
            & (Join-Path $PSScriptRoot 'run-test-module-with-diagnostics.ps1') `
                -AssemblyPath (Join-Path $output 'Fixture.dll') -ReportPath $report `
                -MinimumExpectedTests $Minimum -WarningWorkingSetMiB $Warning -FailureWorkingSetMiB $Failure
        }
        catch { $caught = $true }
        $result = Get-Content $report -Raw | ConvertFrom-Json
        Require ($caught -eq ($Reasons.Count -gt 0)) "Unexpected failure for $Mode"
        Require (($result.failureReasons -join ',') -eq ($Reasons -join ',')) "Wrong classification for $Mode"
        Require ($result.minimumExpectedTests -eq $Minimum -and $result.warningWorkingSetMiB -eq $Warning -and $result.failureWorkingSetMiB -eq $Failure) 'Budgets or inventory were changed.'
        Require ($result.sourceRevision -eq $env:GITHUB_SHA -and $result.runId -eq $env:GITHUB_RUN_ID) 'Run provenance was lost.'
        Require (-not (Get-ChildItem $temp -Filter '*.trx')) 'Raw TRX was retained.'
        Require ((Get-Content $report -Raw) -notmatch 'PRIVATE_TEST_') 'Test payload leaked into diagnostics.'
        return $result
    }
    $result = Run-Case 'passed' @()
    Require ($result.status -eq 'passed' -and $result.passedTestCount -eq 2 -and $result.failedTestCount -eq 0) 'Successful tests misclassified.'
    $result = Run-Case 'passed' @() -Warning 1
    Require ($result.status -eq 'warning') 'Warning became a failure or was suppressed.'
    $result = Run-Case 'passed' @('working-set-budget-exceeded') -Failure 1
    Require ($result.exitCode -eq 0 -and $result.passedTestCount -eq 2) 'Budget failure lost successful test outcome.'
    $result = Run-Case 'failed' @('test-failures')
    Require ($result.failedTestCount -eq 1) 'Failed test counter was lost.'
    $result = Run-Case 'exit' @('test-process-exit')
    Require ($result.exitCode -eq 7) 'Process exit code was lost.'
    $result = Run-Case 'skipped-minimum' @('test-process-exit', 'test-count-below-minimum')
    Require ($result.testCount -eq 2 -and $result.passedTestCount -eq 1) 'Skipped cases hid the runner minimum shortfall.'
    $null = Run-Case 'passed' @('test-count-below-minimum') -Minimum 3
    $null = Run-Case 'missing' @('test-report-missing', 'test-count-below-minimum')
    $null = Run-Case 'invalid' @('test-report-invalid', 'test-count-below-minimum')
    $null = Run-Case 'no-counters' @('test-report-invalid', 'test-count-below-minimum')
    $null = Run-Case 'invalid-counters' @('test-report-invalid', 'test-count-below-minimum')
    $null = Run-Case 'failed' @('test-failures', 'working-set-budget-exceeded') -Failure 1

    # Use two copies of the fixture to verify that the first failing report is
    # retained and the following module runs before the batch throws.
    $after = Join-Path $assemblyRoot 'After/bin/Release/net10.0'
    New-Item -ItemType Directory -Force $after | Out-Null
    foreach ($file in Get-ChildItem $output -File) {
        Copy-Item $file.FullName (Join-Path $after ($file.Name -replace '^Fixture', 'After'))
    }
    $env:AGENTSTRATION_DIAGNOSTICS_TEST_MODE = 'passed'
    $reports = Join-Path $temp 'batch'
    $caught = $false
    try {
        & (Join-Path $PSScriptRoot 'run-hosted-module-diagnostics.ps1') -AssemblyRoot $assemblyRoot -ReportDirectory $reports -Modules @(
            @{ Name = 'Fixture'; Tests = 2; Warning = 0; Failure = 1 },
            @{ Name = 'Absent'; Tests = 2; Warning = 0; Failure = 0 },
            @{ Name = 'After'; Tests = 2; Warning = 0; Failure = 0 }
        )
    }
    catch { $caught = $true }
    Require $caught 'Batch swallowed a failed module.'
    $first = Get-Content (Join-Path $reports 'Fixture.json') -Raw | ConvertFrom-Json
    $last = Get-Content (Join-Path $reports 'After.json') -Raw | ConvertFrom-Json
    Require ($first.status -eq 'failed' -and $last.status -eq 'passed') 'Batch stopped before collecting all reports.'
    & (Join-Path $PSScriptRoot 'run-hosted-module-diagnostics.ps1') -AssemblyRoot $assemblyRoot -ReportDirectory $reports -Modules @(
        @{ Name = 'After'; Tests = 2; Warning = 0; Failure = 0 }
    )
    Require ((Get-Content $summary -Raw) -match 'working-set-budget-exceeded') 'Summary omitted failure reasons.'
    Require ((Get-Content $summary -Raw) -notmatch 'PRIVATE_TEST_') 'Test payload leaked into summary.'
    Write-Host 'Hosted diagnostic classification, budget enforcement and complete batch collection passed.'
}
finally {
    $env:AGENTSTRATION_DIAGNOSTICS_TEST_MODE = $previousMode
    $env:GITHUB_STEP_SUMMARY = $previousSummary
    Remove-Item $temp -Recurse -Force
}
