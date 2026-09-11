[CmdletBinding()]
param(
    [string] $CoverageRoot = "artifacts/coverage"
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$resolvedCoverageRoot = if ([System.IO.Path]::IsPathRooted($CoverageRoot)) {
    [System.IO.Path]::GetFullPath($CoverageRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $CoverageRoot))
}
$artifactsPrefix = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $resolvedCoverageRoot.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Coverage output '$resolvedCoverageRoot' must be inside '$artifactsRoot'."
}

$rawRoot = Join-Path $resolvedCoverageRoot 'raw'
$reportRoot = Join-Path $resolvedCoverageRoot 'report'
$coverageFiles = @(Get-ChildItem -LiteralPath $rawRoot -Filter '*.cobertura.xml' -File -Recurse -ErrorAction SilentlyContinue)
if ($coverageFiles.Count -eq 0) {
    throw "No Cobertura inputs were found below '$rawRoot'."
}

New-Item -ItemType Directory -Force -Path $reportRoot | Out-Null
$reports = ($coverageFiles.FullName -join ';')
& dotnet reportgenerator "-reports:$reports" "-targetdir:$reportRoot" '-reporttypes:Html;Cobertura'
if ($LASTEXITCODE -ne 0) {
    throw "ReportGenerator exited with code $LASTEXITCODE."
}

$coberturaPath = Join-Path $reportRoot 'Cobertura.xml'
$htmlPath = Join-Path $reportRoot 'index.html'
if (-not (Test-Path -LiteralPath $coberturaPath) -or -not (Test-Path -LiteralPath $htmlPath)) {
    throw "The consolidated Cobertura and HTML reports were not both produced."
}

[xml] $coverage = Get-Content -LiteralPath $coberturaPath -Raw
$coverageNode = $coverage.SelectSingleNode("/*[local-name()='coverage']")
if ($null -eq $coverageNode) {
    throw "The consolidated report does not contain a Cobertura coverage root."
}

$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$linesCovered = [int]::Parse($coverageNode.GetAttribute('lines-covered'), $invariant)
$linesValid = [int]::Parse($coverageNode.GetAttribute('lines-valid'), $invariant)
$branchesCovered = [int]::Parse($coverageNode.GetAttribute('branches-covered'), $invariant)
$branchesValid = [int]::Parse($coverageNode.GetAttribute('branches-valid'), $invariant)
if ($linesValid -eq 0 -or $branchesValid -eq 0) {
    throw "The consolidated report must contain line and branch coverage."
}

$packages = @($coverage.SelectNodes("//*[local-name()='package']"))
$invalidPackages = @($packages | Where-Object {
    $_.GetAttribute('name') -match '(?i)(^|\.)Tests($|\.)' -or
    $_.GetAttribute('name') -match '(?i)Performance\.Tests'
})
$invalidSources = @($coverage.SelectNodes("//*[local-name()='class']") | Where-Object {
    $_.GetAttribute('filename') -match '(?i)[\\/]tests[\\/]'
})
if ($invalidPackages.Count -gt 0 -or $invalidSources.Count -gt 0) {
    throw "The consolidated report unexpectedly contains test or performance sources."
}

function Format-Rate([int] $covered, [int] $valid) {
    return (($covered / $valid).ToString('P2', $invariant))
}

$summary = @(
    '## Functional code coverage',
    '',
    '| Metric | Covered | Total | Rate |',
    '| --- | ---: | ---: | ---: |',
    "| Lines | $linesCovered | $linesValid | $(Format-Rate $linesCovered $linesValid) |",
    "| Branches | $branchesCovered | $branchesValid | $(Format-Rate $branchesCovered $branchesValid) |",
    '',
    "Merged from $($coverageFiles.Count) Fast and Integration module reports. Performance and test assemblies are excluded.",
    '',
    'Coverage is report-only; no percentage threshold is enforced.'
)
$summaryPath = Join-Path $reportRoot 'summary.md'
$summary | Set-Content -LiteralPath $summaryPath -Encoding utf8
$summary | ForEach-Object { Write-Host $_ }
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
}
