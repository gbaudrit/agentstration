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
foreach ($path in @('src/Agentstration.ResourcePlanning.Storage.PostgreSql/Migrations/Test.cs', 'src/Agentstration.Security.AspNetCoreIdentity.PostgreSql/Test.cs', 'src/extensions/Agentstration.Extensions.PostgreSql/Program.cs', 'Directory.Packages.props', 'scripts/ci/classify-changes.ps1', '.github/actions/restore-linux-build/action.yml')) {
    $result = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @($path)
    Require ($result.storage -and $result.dotnet) "Missing storage selection: $path"
}
$result = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @('docs/contributing/testing.md')
Require (-not ($result.storage -or $result.dotnet -or $result.aep -or $result.container -or $result.extensionImages)) 'Documentation should skip expensive jobs.'
foreach ($path in @('src/extensions/Agentstration.Extensions.Foundry/Program.cs', 'src/extensions/Agentstration.Extensions.Foundry/version.txt', 'tests/Agentstration.ModelProviders.Tests/FoundryProviderTests.cs', 'tests/Agentstration.ModelProviders.Tests/FoundryLiveTests.cs', 'deploy/images/extensions/foundry.json', 'scripts/packaging/images.py', 'Dockerfile.extension', '.github/workflows/validate-oci-image.yml', '.github/workflows/oci-images.yml')) {
    $selection = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @($path, 'docs/contributing/image-packaging.md')
    Require ($selection.extensionImages -and -not ($selection.dotnet -or $selection.aep -or $selection.container -or $selection.storage)) "Isolated image changes selected transverse validation: $path"
}
foreach ($path in @('aep/src/Agentstration.Aep.Abstractions/AepProtocol.cs', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', '.dockerignore', 'scripts/ci/classify-changes.ps1', '.github/workflows/ci.yml', 'tests/Agentstration.ModelProviders.Tests/Agentstration.ModelProviders.Tests.csproj', 'tests/Agentstration.ModelProviders.Tests/ProviderFixture.cs')) {
    $selection = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @($path)
    Require ($selection.dotnet -and $selection.extensionImages) "Shared image inputs lost product or image validation: $path"
}
foreach ($path in @('src/Agentstration.Web/Program.cs', 'src/Agentstration.Extensions.Contracts/ExtensionContracts.cs', 'src/extensions/Agentstration.Extensions.OpenApi/Program.cs', 'src/extensions/Unknown/Program.cs', 'deploy/images/extensions/openapi.json')) {
    $selection = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @('src/extensions/Agentstration.Extensions.Foundry/Program.cs', $path)
    Require ($selection.dotnet -and $selection.container -and $selection.extensionImages) "Mixed/unknown changes must retain product and image validation: $path"
}
foreach ($path in @('src/extensions/Agentstration.Extensions.Foundry/Program.cs', 'scripts/packaging/enrollment_smoke.py')) {
    $selection = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @($path)
    Require (($selection.imageIds -join ',') -eq 'foundry') "Foundry-only inputs must not build the Worker: $path"
}
foreach ($path in @('src/Agentstration.Runtime.Worker.MicrosoftAgentFramework/Program.cs', 'Dockerfile.worker', 'deploy/images/workers/maf-worker.json', 'scripts/packaging/worker_smoke.py', 'awp/src/Agentstration.Awp.Abstractions/AwpProtocol.cs')) {
    $selection = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @($path)
    Require ($selection.extensionImages -and ($selection.imageIds -join ',') -eq 'maf-worker') "Worker-only inputs must select the Worker image: $path"
}
$selection = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @('scripts/packaging/images.py')
Require (($selection.imageIds -join ',') -eq 'foundry,maf-worker') 'Shared packaging tooling must validate both image families.'
$selection = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @('src/extensions/Agentstration.Extensions.OpenApi/Program.cs')
Require ($selection.dotnet -and $selection.container -and -not $selection.extensionImages) 'Unimplemented packaging verticals must retain current validation.'
foreach ($project in Get-ChildItem (Join-Path $root 'src') -Directory -Filter '*PostgreSql*') {
    $selection = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @("src/$($project.Name)/Migration.cs")
    Require $selection.storage "PostgreSQL project omitted: $($project.Name)"
}
$result = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -RunEverything
Require ($result.storage -and $result.dotnet -and $result.aep -and $result.container -and $result.extensionImages) 'Manual runs must select everything.'
$browser = Get-Content (Join-Path $root '.github/workflows/browser-ux.yml') -Raw
Require (([regex]::Matches($browser, [regex]::Escape('src/Agentstration.Console.*/**'))).Count -eq 2) 'Console families must trigger browser validation on PRs and pushes.'
foreach ($family in @('Web', 'Client', 'Components')) {
    Require ('src/Agentstration.Console.' + $family + '/Test.cs' -like 'src/Agentstration.Console.*/**') "Browser glob misses Console $family"
}
# A rename is classified as its removed and added paths, just as git diff --no-renames emits it.
$result = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @('src/Agentstration.Security.AspNetCoreIdentity.PostgreSql/Old.cs', 'docs/New.md')
Require $result.storage 'Renamed/deleted storage inputs must retain provider validation.'
# Exercise the same result gate used by the required GitHub check.
$gate = Join-Path $PSScriptRoot 'assert-validation-results.ps1'
$nothing = @{ ClassificationResult = 'success'; DotnetSelected = 'false'; AepSelected = 'false'; ExtensionImagesSelected = 'false'; BuildResult = 'skipped'; FunctionalResult = 'skipped'; AepResult = 'skipped'; ExtensionImagesResult = 'skipped' }
& $gate @nothing | Out-Null
$isolated = $nothing.Clone()
$isolated.ExtensionImagesSelected = 'true'
$isolated.ExtensionImagesResult = 'success'
& $gate @isolated | Out-Null
$complete = $isolated.Clone()
$complete.DotnetSelected = 'true'
$complete.AepSelected = 'true'
$complete.BuildResult = $complete.FunctionalResult = $complete.AepResult = 'success'
& $gate @complete | Out-Null
foreach ($outcome in @('failure', 'cancelled', 'skipped', '')) {
    $failure = $isolated.Clone()
    $failure.ExtensionImagesResult = $outcome
    Must-Fail { & $gate @failure } "Required image validation accepted '$outcome'."
    foreach ($name in @('BuildResult', 'FunctionalResult', 'AepResult', 'ClassificationResult')) {
        $failure = $complete.Clone()
        $failure[$name] = $outcome
        Must-Fail { & $gate @failure } "Required validation $name accepted '$outcome'."
    }
}
foreach ($name in @('DotnetSelected', 'AepSelected', 'ExtensionImagesSelected')) {
    $failure = $nothing.Clone()
    $failure[$name] = ''
    Must-Fail { & $gate @failure } "Required validation accepted a missing selection: $name"
}
$ci = Get-Content (Join-Path $root '.github/workflows/ci.yml') -Raw
Require ($ci.Contains('needs: [changes, build, functional-tests, aep-tests, extension-images]')) 'The required check must wait for the selected image validation.'
Require ($ci.Contains('EXTENSION_IMAGES_SELECTED: ${{ needs.changes.outputs.extension-images }}') -and $ci.Contains('EXTENSION_IMAGES_RESULT: ${{ needs.extension-images.result }}')) 'The result gate must receive image selection and status.'
Require ($ci.Contains('run: ./scripts/ci/assert-validation-results.ps1')) 'CI must execute the tested result gate.'
Require ($ci.Contains('matrix: ${{ fromJSON(needs.changes.outputs.image-matrix) }}') -and $ci.Contains('extension: ${{ matrix.image }}')) 'Image validation must consume the classified matrix.'
$delivery = Get-Content (Join-Path $root '.github/workflows/oci-images.yml') -Raw
Require (-not $delivery.Contains('pull_request:')) 'PR images must not build a second time in the delivery workflow.'
foreach ($workflow in @($ci, $delivery)) {
    Require ($workflow.Contains('uses: ./.github/workflows/validate-oci-image.yml')) 'CI and delivery must share the same OCI validation.'
}
$validation = Get-Content (Join-Path $root '.github/workflows/validate-oci-image.yml') -Raw
Require (-not ($validation.Contains('packages: write') -or $validation.Contains('secrets.') -or $validation.Contains('  publish:'))) 'Reusable PR validation must not include registry publication credentials.'
$enrollmentSteps = @($validation -split '(?m)^      - name: ' | Where-Object { $_.Contains('scripts/packaging/enrollment_smoke.py') })
Require ($enrollmentSteps.Count -eq 1 -and $enrollmentSteps[0].Contains("if: steps.select.outputs.kind == 'aep-extension'")) 'The enrollment smoke must run for every selected AEP image, not a provider ID.'
$resourceUx = Get-Content (Join-Path $root '.github/workflows/resource-plan-ux.yml') -Raw
Require ($resourceUx.Contains("      - 'src/**'") -and $resourceUx.Contains("      - '!src/extensions/Agentstration.Extensions.Foundry/**'")) 'Resource Plan UX must exclude Foundry-only source edits while retaining mixed product edits.'
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
