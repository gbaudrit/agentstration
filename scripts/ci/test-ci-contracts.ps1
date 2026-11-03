$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
. (Join-Path $PSScriptRoot 'coverage-inventory.ps1')
function Require($Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Must-Fail([scriptblock] $Action, [string] $Message) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Require $failed $Message
}
Assert-CoverageInventory $root
foreach ($path in @('src/Agentstration.ResourcePlanning.Storage.PostgreSql/Migrations/Test.cs', 'src/Agentstration.Security.AspNetCoreIdentity.PostgreSql/Test.cs', 'Directory.Packages.props', 'scripts/ci/classify-changes.ps1')) {
    $result = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @($path)
    Require ($result.storage -and $result.dotnet) "Missing storage selection: $path"
}
$result = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @('docs/contributing/testing.md')
Require (-not ($result.storage -or $result.dotnet -or $result.aep -or $result.container)) 'Documentation should skip expensive jobs.'
foreach ($project in Get-ChildItem (Join-Path $root 'src') -Directory -Filter '*PostgreSql*') {
    $selection = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @("src/$($project.Name)/Migration.cs")
    Require $selection.storage "PostgreSQL project omitted: $($project.Name)"
}
$result = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -RunEverything
Require ($result.storage -and $result.dotnet -and $result.aep -and $result.container) 'Manual runs must select everything.'
$browser = Get-Content (Join-Path $root '.github/workflows/browser-ux.yml') -Raw
Require (([regex]::Matches($browser, [regex]::Escape('src/Agentstration.Console.*/**'))).Count -eq 2) 'Console families must trigger browser validation on PRs and pushes.'
foreach ($family in @('Web', 'Client', 'Components')) {
    Require ('src/Agentstration.Console.' + $family + '/Test.cs' -like 'src/Agentstration.Console.*/**') "Browser glob misses Console $family"
}
# A rename is classified as its removed and added paths, just as git diff --no-renames emits it.
$result = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @('src/Agentstration.Security.AspNetCoreIdentity.PostgreSql/Old.cs', 'docs/New.md')
Require $result.storage 'Renamed/deleted storage inputs must retain provider validation.'
$temp = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
New-Item -ItemType Directory $temp | Out-Null
try {
    foreach ($name in @('Fast', 'Integration', 'CoverageA', 'CoverageB')) {
        Copy-Item (Join-Path $root "Agentstration.Tests.$name.slnx") $temp
    }
    $shard = Join-Path $temp 'Agentstration.Tests.CoverageA.slnx'
    $original = Get-Content $shard -Raw
    $entry = [regex]::Match($original, '    <Project[^\n]+\n').Value
    $original.Replace($entry, '') | Set-Content $shard
    Must-Fail { Assert-CoverageInventory $temp } 'Missing coverage project was accepted.'
    $original.Replace('  </Folder>', "$entry  </Folder>") | Set-Content $shard
    Must-Fail { Assert-CoverageInventory $temp } 'Duplicate coverage project was accepted.'
    $original.Replace($entry, '    <Project Path="unexpected.csproj" />') | Set-Content $shard
    Must-Fail { Assert-CoverageInventory $temp } 'Unexpected coverage project was accepted.'
    $reports = Join-Path $temp 'reports'
    New-Item -ItemType Directory $reports | Out-Null
    '<coverage />' | Set-Content (Join-Path $reports 'sample.cobertura.xml')
    '<TestRun><ResultSummary><CollectorDataEntries><Collector><UriAttachments><UriAttachment><A href="host/sample.cobertura.xml" /></UriAttachment></UriAttachments></Collector></CollectorDataEntries></ResultSummary></TestRun>' | Set-Content (Join-Path $reports 'Example.Tests.trx')
    Assert-CoverageReports $reports @('tests/Example.Tests.csproj') | Out-Null
    $copy = Join-Path $reports 'deployment'
    New-Item -ItemType Directory $copy | Out-Null
    Copy-Item (Join-Path $reports 'sample.cobertura.xml') $copy
    $logical = @(Assert-CoverageReports $reports @('tests/Example.Tests.csproj'))
    Require ($logical.Count -eq 1) 'TRX copies must merge once.'
    '<coverage changed="true" />' | Set-Content (Join-Path $copy 'sample.cobertura.xml')
    Must-Fail { Assert-CoverageReports $reports @('tests/Example.Tests.csproj') } 'Conflicting report copies were accepted.'
    Remove-Item $copy -Recurse -Force
    Must-Fail { Assert-CoverageReports $reports @('tests/Missing.Tests.csproj') } 'Missing module report was accepted.'
    Remove-Item (Join-Path $reports 'sample.cobertura.xml')
    Must-Fail { Assert-CoverageReports $reports @('tests/Example.Tests.csproj') } 'Missing module coverage was accepted.'
}
finally { Remove-Item $temp -Recurse -Force }
Write-Host 'CI inventory, report completeness and path-selection contracts passed.'
