[CmdletBinding()]
param(
    [Parameter()]
    [string] $BaseRevision
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $repositoryRoot

try {
    if (-not $BaseRevision -or $BaseRevision -match '^0+$') {
        $BaseRevision = 'HEAD^'
    }

    & git rev-parse --verify "$BaseRevision^{commit}" *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "Base revision '$BaseRevision' is not available. Check out enough Git history to compute the format diff."
    }

    $changedFiles = @(
        & git diff --name-only --diff-filter=ACMRT $BaseRevision HEAD -- '*.cs' '*.razor'
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list changed .NET files from '$BaseRevision' to HEAD."
    }

    if ($changedFiles.Count -eq 0) {
        Write-Host 'No changed C# or Razor files require format verification.'
        return
    }

    $aepFiles = @($changedFiles | Where-Object { $_ -like 'aep/*' })
    $platformFiles = @($changedFiles | Where-Object { $_ -notlike 'aep/*' })

    if ($platformFiles.Count -gt 0) {
        & dotnet format Agentstration.slnx --verify-no-changes --no-restore --include @platformFiles
        if ($LASTEXITCODE -ne 0) {
            throw 'Formatting verification failed for changed Agentstration files.'
        }
    }

    if ($aepFiles.Count -gt 0) {
        & dotnet format aep/Aep.slnx --verify-no-changes --no-restore --include @aepFiles
        if ($LASTEXITCODE -ne 0) {
            throw 'Formatting verification failed for changed AEP files.'
        }
    }
}
finally {
    Pop-Location
}
