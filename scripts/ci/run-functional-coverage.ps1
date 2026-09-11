[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$coverageRoot = Join-Path $repositoryRoot 'artifacts/coverage'
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$coveragePrefix = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$resolvedCoverageRoot = [System.IO.Path]::GetFullPath($coverageRoot)
if (-not $resolvedCoverageRoot.StartsWith($coveragePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Coverage output '$resolvedCoverageRoot' must be inside '$artifactsRoot'."
}
if (Test-Path -LiteralPath $resolvedCoverageRoot) {
    Remove-Item -LiteralPath $resolvedCoverageRoot -Recurse -Force
}

$settingsPath = Join-Path $PSScriptRoot 'coverage.settings.xml'
$lanes = @(
    @{
        Name = 'Fast'
        Solution = 'Agentstration.Tests.Fast.slnx'
        MinimumTests = 336
        ParallelModules = 4
        ResultsDirectory = Join-Path $resolvedCoverageRoot 'raw/fast'
    },
    @{
        Name = 'Integration'
        Solution = 'Agentstration.Tests.Integration.slnx'
        MinimumTests = 515
        ParallelModules = 2
        ResultsDirectory = Join-Path $resolvedCoverageRoot 'raw/integration'
    }
)

Push-Location $repositoryRoot
try {
    foreach ($lane in $lanes) {
        Write-Host "Collecting $($lane.Name) lane coverage..."
        $arguments = @(
            'test',
            '--solution', $lane.Solution,
            '--configuration', $Configuration
        )
        if ($NoBuild) {
            $arguments += '--no-build'
        }
        $arguments += @(
            '--minimum-expected-tests', $lane.MinimumTests,
            '--max-parallel-test-modules', $lane.ParallelModules,
            '--coverage',
            '--coverage-output-format', 'cobertura',
            '--coverage-settings', $settingsPath,
            '--results-directory', $lane.ResultsDirectory
        )
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$($lane.Name) coverage execution exited with code $LASTEXITCODE."
        }
    }

    & (Join-Path $PSScriptRoot 'publish-functional-coverage.ps1') -CoverageRoot $resolvedCoverageRoot
}
finally {
    Pop-Location
}
