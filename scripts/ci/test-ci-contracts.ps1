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
& (Join-Path $PSScriptRoot 'extensions/test-classify-changes.ps1')
foreach ($path in @('src/Agentstration.ResourcePlanning.Storage.PostgreSql/Migrations/Test.cs', 'src/Agentstration.Security.AspNetCoreIdentity.PostgreSql/Test.cs', 'Directory.Packages.props', 'scripts/ci/classify-changes.ps1', '.github/actions/restore-linux-build/action.yml')) {
    $result = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @($path)
    Require ($result.storage -and $result.dotnet) "Missing storage selection: $path"
}
$result = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @('docs/contributing/testing.md')
Require (-not ($result.storage -or $result.dotnet -or $result.aep -or $result.container -or $result.extensionImages)) 'Documentation should skip expensive jobs.'
$extensions = & (Join-Path $PSScriptRoot 'extensions/classify-changes.ps1') -RunEverything
foreach ($path in @('src/Agentstration.Runtime.Worker.MicrosoftAgentFramework/Program.cs', 'Dockerfile.worker', 'deploy/images/workers/maf-worker.json', 'scripts/packaging/worker_smoke.py', 'scripts/packaging/maf-worker.smoke.compose.yml', 'awp/src/Agentstration.Awp.Abstractions/AwpProtocol.cs')) {
    $selection = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @($path)
    Require ($selection.extensionImages -and ($selection.imageIds -join ',') -eq 'maf-worker,server') "Worker inputs must validate the Worker and its real preview composition: $path"
}
$selection = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @('scripts/packaging/images.py')
Require (($selection.imageIds -join ',') -eq ((@($extensions.imageIds) + @('maf-worker', 'server')) -join ',')) 'Shared packaging tooling must validate all implemented image families.'
foreach ($path in @('Dockerfile', 'deploy/images/applications/server.json', 'deploy/preview/compose.yml', 'deploy/preview/postgresql.yml', 'deploy/preview/postgresql.json', 'scripts/preview/preview.py', '.github/workflows/preview-environment.yml', '.github/workflows/preview-azure-validation.yml', '.github/workflows/preview-azure-environment.yml', '.github/workflows/preview-on-demand.yml')) {
    $selection = & (Join-Path $PSScriptRoot 'classify-changes.ps1') -Paths @($path)
    Require (($selection.imageIds -join ',') -eq 'server') "Server preview inputs must select their executable validation: $path"
}
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
$previewSteps = @($validation -split '(?m)^      - name: ' | Where-Object { $_.Contains('scripts/preview/preview_smoke.py') })
Require ($previewSteps.Count -eq 1 -and $previewSteps[0].Contains("if: steps.select.outputs.kind == 'server'")) 'Server delivery must execute the actual digest-pinned preview before publication.'
Require ($previewSteps[0].Contains("for database in ('sqlite', 'postgresql'):") -and $previewSteps[0].Contains("'--database', database")) 'The same server archive must pass both database previews before publication.'
$lifecycle = Get-Content (Join-Path $root '.github/workflows/preview-environment.yml') -Raw
Require ($lifecycle.Contains('workflow_call:') -and $lifecycle.Contains('workflow_dispatch:')) 'Lifecycle entrypoints must be reusable and manually invokable.'
Require ($lifecycle.Contains('scripts/preview/lifecycle.py') -and $lifecycle.Contains('cancel-in-progress: false')) 'Lifecycle workflows must delegate to versioned scripts and serialize environment mutations.'
Require (-not ($lifecycle.Contains('packages: write') -or $lifecycle.Contains('GHCR_BOOTSTRAP_TOKEN') )) 'Portable Docker lifecycle must have read-only package delivery and no cloud publication credentials.'
$dockerLifecycle = ($lifecycle -split '(?m)^  azure:')[0]
Require (-not $dockerLifecycle.Contains('id-token: write')) 'Docker execution and pure validation must not obtain Azure OIDC permissions.'
Require ($lifecycle.Contains('azure/login@v2') -and $lifecycle.Contains("needs.validate.outputs.provider == 'azure-vm'")) 'Azure execution must be routed explicitly and use OIDC.'
$resourceUx = Get-Content (Join-Path $root '.github/workflows/resource-plan-ux.yml') -Raw
Require ($resourceUx.Contains("      - 'src/**'") -and $resourceUx.Contains("      - '!src/extensions/**'")) 'Resource Plan UX must exclude autonomous extension sources while retaining mixed product edits.'
foreach ($path in @('src/extensions/Agentstration.Extensions.Fixture/Program.cs', 'src/extensions/Unknown/Program.cs')) {
    Require ($path -like 'src/**' -and $path -like 'src/extensions/**') "Autonomous extension must be excluded from Resource Plan UX: $path"
}
foreach ($path in @('src/Agentstration.Web/Program.cs', 'src/Agentstration.ResourcePlanning/Service.cs')) {
    Require ($path -like 'src/**' -and $path -notlike 'src/extensions/**') "Product and mixed changes must retain Resource Plan UX: $path"
}
Require ($browser.Contains('src/extensions/Agentstration.Extensions.Utilities/**')) 'Browser UX must retain its explicit Utilities fixture dependency.'
foreach ($job in @('functional-tests', 'hosted-module-diagnostics', 'functional-coverage', 'windows-host-lifecycle')) {
    $block = ($ci -split "(?m)^  ${job}:")[1] -split '(?m)^  [a-z][a-z-]*:' | Select-Object -First 1
    Require ($block.Contains("if: needs.changes.outputs.dotnet == 'true'")) "Product job must consume the central product selection: $job"
}
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
