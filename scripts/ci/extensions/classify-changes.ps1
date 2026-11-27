[CmdletBinding()]
param(
    [string[]] $Paths = @(),
    [switch] $RunEverything,
    [string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
)
$ErrorActionPreference = 'Stop'

function Assert-RelativePath([string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -match '(^/|\\|:|(^|/)\.{1,2}(/|$)|//)') {
        throw "Catalogue CI paths must be relative repository paths: $Value"
    }
}

$entries = @()
$catalogueDirectory = Join-Path $RepositoryRoot 'deploy/images/extensions'
foreach ($file in Get-ChildItem -LiteralPath $catalogueDirectory -Filter '*.json' -File | Sort-Object Name) {
    $entry = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
    if (($entry.schemaVersion -isnot [long] -and $entry.schemaVersion -isnot [int]) -or
        $entry.schemaVersion -ne 1 -or $entry.id -cnotmatch '^[a-z][a-z0-9-]*$' -or
        $entry.id -cne $file.BaseName -or $entry.id -in @('server', 'maf-worker') -or
        ($entry.ContainsKey('kind') -and $entry.kind -ne 'aep-extension')) {
        throw "Invalid AEP image catalogue identity: $($file.Name)"
    }
    foreach ($field in @('project', 'testProject', 'versionFile', 'dockerfile')) {
        Assert-RelativePath $entry[$field]
        if ($entry[$field] -match '[*?\[\]]' -or -not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $entry[$field]) -PathType Leaf)) {
            throw "Catalogue $field must identify an existing file: $($file.Name)"
        }
    }
    if ($entry.project -notmatch '^src/extensions/Agentstration\.Extensions\.[A-Za-z0-9.]+/[^/]+\.csproj$' -or
        $entry.testProject -notmatch '^tests/[A-Za-z0-9.]+/[^/]+\.csproj$') {
        throw "Catalogue projects must identify an autonomous extension and its test project: $($file.Name)"
    }
    $sourceDirectory = $entry.project.Substring(0, $entry.project.LastIndexOf('/') + 1)
    if (-not $entry.versionFile.StartsWith($sourceDirectory, [StringComparison]::Ordinal)) {
        throw "An extension version file must stay inside its source directory: $($file.Name)"
    }
    $testDirectory = $entry.testProject.Substring(0, $entry.testProject.LastIndexOf('/') + 1)
    $projectName = [IO.Path]::GetFileNameWithoutExtension($entry.project)
    $dedicatedTests = [IO.Path]::GetFileNameWithoutExtension($entry.testProject) -eq "$projectName.Tests"
    $ci = if ($entry.ContainsKey('ci')) { $entry.ci } else { @{} }
    if ($ci -isnot [Collections.IDictionary]) { throw "Catalogue ci metadata must be an object: $($file.Name)" }
    foreach ($field in @('testPaths', 'additionalPaths', 'sharedPaths')) {
        if ($ci.ContainsKey($field) -and $ci[$field] -isnot [array]) {
            throw "Catalogue ci.$field must be an array: $($file.Name)"
        }
    }
    $testPaths = if ($ci.ContainsKey('testPaths')) { @($ci.testPaths) } elseif ($dedicatedTests) { @("$testDirectory*") } else { @() }
    if (-not $dedicatedTests -and $testPaths.Count -eq 0) {
        throw "A shared test project requires explicit ci.testPaths: $($file.Name)"
    }
    foreach ($path in $testPaths) {
        Assert-RelativePath $path
        if (-not $path.StartsWith($testDirectory, [StringComparison]::Ordinal) -or
            (-not $dedicatedTests -and $path -match '[*?\[\]]')) {
            throw "ci.testPaths must stay inside the test project; shared projects require exact files: $path"
        }
    }
    $additionalPaths = if ($ci.ContainsKey('additionalPaths')) { @($ci.additionalPaths) } else { @() }
    foreach ($path in $additionalPaths) {
        Assert-RelativePath $path
        if (-not ($path.StartsWith("deploy/$($entry.id)/", [StringComparison]::Ordinal) -or
                  $path.StartsWith("scripts/packaging/$($entry.id)", [StringComparison]::Ordinal) -or
                  ($path.StartsWith('.github/workflows/', [StringComparison]::Ordinal) -and
                   [IO.Path]::GetFileName($path).Contains($entry.id)))) {
            throw "ci.additionalPaths must identify extension-owned packaging assets: $path"
        }
    }
    $sharedPaths = if ($ci.ContainsKey('sharedPaths')) { @($ci.sharedPaths) } else { @() }
    foreach ($path in $sharedPaths) { Assert-RelativePath $path }
    $recipeDirectory = $entry.dockerfile.Substring(0, $entry.dockerfile.LastIndexOf('/') + 1)
    if ($entry.dockerfile -ne 'Dockerfile.extension' -and
        -not $entry.dockerfile.StartsWith("deploy/$($entry.id)/", [StringComparison]::Ordinal) -and
        -not $entry.dockerfile.StartsWith($sourceDirectory, [StringComparison]::Ordinal)) {
        throw "An extension recipe must be generic or extension-owned: $($file.Name)"
    }
    $entries += [pscustomobject]@{
        id = $entry.id
        catalogue = "deploy/images/extensions/$($entry.id).json"
        sourceDirectory = $sourceDirectory
        testDirectory = $testDirectory
        testProject = $entry.testProject
        dedicatedTests = $dedicatedTests
        testPaths = $testPaths
        versionFile = $entry.versionFile
        dockerfile = $entry.dockerfile
        recipeDirectory = $recipeDirectory
        additionalPaths = $additionalPaths
        sharedPaths = $sharedPaths
        contract = "scripts/packaging/test_$($entry.id.Replace('-', '_'))_image.py"
    }
}

$common = '^(scripts/ci/extensions/|aep/|Dockerfile\.extension$|\.dockerignore$|\.github/workflows/(oci-images|validate-oci-image)\.yml$|scripts/packaging/(images|test_images|smoke|enrollment_smoke|test_enrollment_smoke)\.py$|scripts/packaging/aep-extension\.smoke\.compose\.yml$)'
$selected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$owned = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($path in $Paths) {
    $unknownCatalogue = $path -match '^deploy/images/extensions/[^/]+\.json$' -and $path -cnotin @($entries.catalogue)
    if ($path -match $common -or $unknownCatalogue) {
        # Removed or unknown catalogues select all remaining images but stay product inputs.
        if ($path -match '^scripts/ci/extensions/') { [void]$owned.Add($path) }
        foreach ($entry in $entries) {
            [void]$selected.Add($entry.id)
        }
    }
    $testOwners = @($entries | Where-Object {
        ($_.dedicatedTests -or $path -cne $_.testProject) -and
            @($_.testPaths | Where-Object { $path -clike $_ }).Count -gt 0
    } | ForEach-Object { $_.id })
    foreach ($entry in $entries) {
        $testOwned = $entry.id -cin $testOwners
        $recipeOwned = $entry.recipeDirectory -ne '' -and $path.StartsWith($entry.recipeDirectory, [StringComparison]::Ordinal)
        $direct = $path.StartsWith($entry.sourceDirectory, [StringComparison]::Ordinal) -or
            $testOwned -or $recipeOwned -or $path -cin @($entry.catalogue, $entry.versionFile, $entry.dockerfile, $entry.contract) -or
            @($entry.additionalPaths | Where-Object { $path -clike $_ }).Count -gt 0
        if ($direct) {
            [void]$owned.Add($path)
            [void]$selected.Add($entry.id)
        }
        elseif (($testOwners.Count -eq 0 -and $path.StartsWith($entry.testDirectory, [StringComparison]::Ordinal)) -or
                @($entry.sharedPaths | Where-Object { $path -clike $_ }).Count -gt 0) {
            # Unclaimed tests/projects retain conservative coverage; explicit owners suppress directory fallback.
            [void]$selected.Add($entry.id)
        }
    }
}
# New common packaging helpers must not fall through to an empty image matrix.
$aepPackaging = '^scripts/packaging/((enrollment_smoke|test_enrollment_smoke)\.py|aep-extension\.smoke\.compose\.yml)$'
$workerPackaging = '^scripts/packaging/((worker_smoke|test_worker_smoke)\.py|maf-worker\.smoke\.compose\.yml)$'
$sharedPackaging = @($Paths | Where-Object {
    $_ -match '^scripts/packaging/' -and -not $owned.Contains($_) -and
    $_ -notmatch $aepPackaging -and $_ -notmatch $workerPackaging
}).Count -gt 0
if ($sharedPackaging) { foreach ($entry in $entries) { [void]$selected.Add($entry.id) } }
if ($RunEverything) { foreach ($entry in $entries) { [void]$selected.Add($entry.id) } }
[pscustomobject]@{
    imageIds = @($entries | Where-Object { $selected.Contains($_.id) } | ForEach-Object { $_.id })
    sharedPackaging = $sharedPackaging
    ownedPaths = @($Paths | Where-Object { $owned.Contains($_) })
}
