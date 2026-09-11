[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $NoBuild,

    [ValidateSet('All', 'a', 'b')]
    [string] $Shard = 'All',

    [switch] $SkipPublish
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
$cleanupRoot = if ($Shard -eq 'All') {
    $resolvedCoverageRoot
}
else {
    Join-Path $resolvedCoverageRoot "raw/$($Shard.ToLowerInvariant())"
}
if (Test-Path -LiteralPath $cleanupRoot) {
    Remove-Item -LiteralPath $cleanupRoot -Recurse -Force
}

$settingsPath = Join-Path $PSScriptRoot 'coverage.settings.xml'
$lanes = switch ($Shard.ToLowerInvariant()) {
    'a' {
        @(
            @{
                Name = 'Fast'
                Solution = 'Agentstration.Tests.Fast.slnx'
                MinimumTests = 139
                ParallelModules = 4
                ResultsDirectory = Join-Path $resolvedCoverageRoot 'raw/a/fast'
            },
            @{
                Name = 'Integration coverage shard A'
                Solution = 'Agentstration.Tests.CoverageA.slnx'
                MinimumTests = 221
                ParallelModules = 2
                ResultsDirectory = Join-Path $resolvedCoverageRoot 'raw/a/integration'
            }
        )
    }
    'b' {
        @(
            @{
                Name = 'Integration coverage shard B'
                Solution = 'Agentstration.Tests.CoverageB.slnx'
                MinimumTests = 471
                ParallelModules = 2
                ResultsDirectory = Join-Path $resolvedCoverageRoot 'raw/b/integration'
            }
        )
    }
    default {
        @(
            @{
                Name = 'Fast'
                Solution = 'Agentstration.Tests.Fast.slnx'
                MinimumTests = 139
                ParallelModules = 4
                ResultsDirectory = Join-Path $resolvedCoverageRoot 'raw/fast'
            },
            @{
                Name = 'Integration'
                Solution = 'Agentstration.Tests.Integration.slnx'
                MinimumTests = 692
                ParallelModules = 2
                ResultsDirectory = Join-Path $resolvedCoverageRoot 'raw/integration'
            }
        )
    }
}

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

    if (-not $SkipPublish) {
        & (Join-Path $PSScriptRoot 'publish-functional-coverage.ps1') -CoverageRoot $resolvedCoverageRoot
    }
}
finally {
    Pop-Location
}
