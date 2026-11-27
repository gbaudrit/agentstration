$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$classifier = Join-Path $PSScriptRoot '../classify-changes.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('extension-ci-' + [guid]::NewGuid())
$script:assertions = 0
function Require($Condition, [string] $Message) {
    $script:assertions++
    if (-not $Condition) { throw $Message }
}
function Write-Catalogue($Entry) {
    foreach ($field in @('project', 'testProject', 'versionFile', 'dockerfile')) {
        $path = Join-Path $temporary $Entry[$field]
        New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
        Set-Content -LiteralPath $path -Value 'offline classifier fixture'
    }
    $path = Join-Path $temporary "deploy/images/extensions/$($Entry.id).json"
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $Entry | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path
}
function Get-FixtureSelection([string[]] $Changed) {
    & $classifier -Paths $Changed -RepositoryRoot $temporary
}
function Isolated([string[]] $Changed, [string[]] $Expected) {
    $selection = Get-FixtureSelection $Changed
    Require (-not ($selection.dotnet -or $selection.aep -or $selection.container -or $selection.storage)) "Unexpected product validation: $Changed"
    Require (($selection.imageIds -join ',') -ceq ($Expected -join ',')) "Wrong image selection for ${Changed}: $($selection.imageIds -join ',')"
    Require ($selection.extensionImages -eq ($Expected.Count -gt 0)) "Image result gate lost selection: $Changed"
}
try {
    $catalogues = @(Get-ChildItem (Join-Path $root 'deploy/images/extensions') -File -Filter '*.json' | Sort-Object Name)
    foreach ($file in $catalogues) {
        $entry = Get-Content $file.FullName -Raw | ConvertFrom-Json -AsHashtable
        Write-Catalogue $entry
        Isolated @($entry.project, 'docs/contributing/testing.md') @($entry.id)
        Isolated @($entry.versionFile) @($entry.id)
        Isolated @("deploy/images/extensions/$($entry.id).json") @($entry.id)
        $projectName = [IO.Path]::GetFileNameWithoutExtension($entry.project)
        if ([IO.Path]::GetFileNameWithoutExtension($entry.testProject) -eq "$projectName.Tests") {
            Isolated @($entry.testProject) @($entry.id)
            Isolated @("$($entry.testProject.Substring(0, $entry.testProject.LastIndexOf('/')))/OwnedTests.cs") @($entry.id)
        }
        elseif ($entry.ContainsKey('ci')) {
            foreach ($path in $entry.ci.testPaths) { Isolated @($path) @($entry.id) }
        }
    }
    # New extensions are discovered without changing the central classifier or committing catalogues.
    $synthetic = [ordered]@{
        openapi = 'OpenApi'; postgresql = 'PostgreSql'; ollama = 'Ollama'; 'llama-cpp' = 'LlamaCpp'
        opendal = 'OpenDal'; git = 'Git'; utilities = 'Utilities'; crawl4ai = 'Crawl4AI'; localai = 'LocalAI'
    }
    foreach ($id in $synthetic.Keys) {
        if (Test-Path (Join-Path $temporary "deploy/images/extensions/$id.json")) { continue }
        $name = "Agentstration.Extensions.$($synthetic[$id])"
        $entry = @{
            schemaVersion = 1; id = $id; project = "src/extensions/$name/$name.csproj"
            testProject = "tests/$name.Tests/$name.Tests.csproj"
            versionFile = "src/extensions/$name/version.txt"; dockerfile = 'Dockerfile.extension'
        }
        Write-Catalogue $entry
        foreach ($path in @($entry.project, "$($entry.project.Substring(0, $entry.project.LastIndexOf('/')))/Program.cs",
                $entry.testProject, "tests/$name.Tests/ManifestTests.cs", $entry.versionFile,
                "deploy/images/extensions/$id.json", "scripts/packaging/test_$($id.Replace('-', '_'))_image.py")) {
            Isolated @($path, 'docs/contributing/image-packaging.md') @($id)
        }
    }
    # Multiple catalogued extensions may own distinct or overlapping files in one test project.
    $sharedProject = 'tests/SharedProviders.Tests/SharedProviders.Tests.csproj'
    $sharedEntries = @{}
    foreach ($name in @('Alpha', 'Beta', 'Gamma')) {
        $id = $name.ToLowerInvariant()
        $ownedTests = @("tests/SharedProviders.Tests/${name}Tests.cs")
        if ($id -ne 'gamma') { $ownedTests += 'tests/SharedProviders.Tests/CoOwnedTests.cs' }
        $entry = @{
            schemaVersion = 1; id = $id; project = "src/extensions/Agentstration.Extensions.$name/Agentstration.Extensions.$name.csproj"
            testProject = $sharedProject; versionFile = "src/extensions/Agentstration.Extensions.$name/version.txt"
            dockerfile = 'Dockerfile.extension'; ci = @{ testPaths = $ownedTests }
        }
        Write-Catalogue $entry
        $sharedEntries[$id] = $entry
    }
    foreach ($name in @('Alpha', 'Beta', 'Gamma')) {
        Isolated @("tests/SharedProviders.Tests/${name}Tests.cs") @($name.ToLowerInvariant())
    }
    Isolated @('tests/SharedProviders.Tests/AlphaTests.cs', 'tests/SharedProviders.Tests/GammaTests.cs') @('alpha', 'gamma')
    Isolated @('tests/SharedProviders.Tests/CoOwnedTests.cs') @('alpha', 'beta')
    foreach ($path in @($sharedProject, 'tests/SharedProviders.Tests/SharedFixture.cs', 'tests/SharedProviders.Tests/UnclaimedDeletedTests.cs')) {
        $selection = Get-FixtureSelection @($path)
        Require $selection.dotnet "Shared or unclaimed test inputs must retain product validation: $path"
        Require (($selection.imageIds -join ',') -ceq 'alpha,beta,gamma') "Shared test inputs must validate every dependent image: $path"
    }
    # Explicit shared dependencies still select an image when another extension owns the file.
    $sharedEntries.beta.ci.sharedPaths = @('tests/SharedProviders.Tests/AlphaTests.cs')
    Write-Catalogue $sharedEntries.beta
    Isolated @('tests/SharedProviders.Tests/AlphaTests.cs') @('alpha', 'beta')
    $sharedEntries.beta.ci.Remove('sharedPaths')
    Write-Catalogue $sharedEntries.beta
    $selection = Get-FixtureSelection @('tests/SharedProviders.Tests/AlphaTests.cs', 'src/Agentstration.Web/Program.cs')
    Require ($selection.dotnet -and $selection.container) 'Mixed product inputs must retain transverse validation.'
    Require (($selection.imageIds -join ',') -ceq 'alpha,server') 'Mixed product inputs must retain their selected images.'
    $all = @(Get-ChildItem (Join-Path $temporary 'deploy/images/extensions') -File -Filter '*.json' | Sort-Object Name | ForEach-Object { $_.BaseName })
    foreach ($path in @('scripts/ci/extensions/classify-changes.ps1', 'scripts/ci/extensions/test-classify-changes.ps1', 'scripts/ci/extensions/new-helper.ps1')) {
        Isolated @($path) $all
    }
    foreach ($path in @('scripts/packaging/enrollment_smoke.py', 'scripts/packaging/test_enrollment_smoke.py', 'scripts/packaging/aep-extension.smoke.compose.yml')) {
        Isolated @($path) $all
    }
    foreach ($path in @('scripts/packaging/images.py', 'scripts/packaging/test_images.py', 'scripts/packaging/smoke.py',
            'Dockerfile.extension', 'scripts/packaging/new-common-helper.py', '.github/workflows/oci-images.yml', '.github/workflows/validate-oci-image.yml')) {
        Isolated @($path) @($all + @('maf-worker', 'server'))
    }
    foreach ($path in @('global.json', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
            'scripts/ci/classify-changes.ps1', 'scripts/ci/test-ci-contracts.ps1', '.github/workflows/ci.yml', '.github/actions/restore-linux-build/action.yml')) {
        $selection = Get-FixtureSelection @($path)
        Require ($selection.dotnet -and $selection.aep -and $selection.container -and $selection.storage) "Global input lost transverse checks: $path"
        Require (($selection.imageIds -join ',') -ceq (($all + @('maf-worker', 'server')) -join ',')) "Global input lost images: $path"
    }
    $selection = Get-FixtureSelection @('aep/src/Agentstration.Aep.Abstractions/Protocol.cs')
    Require ($selection.dotnet -and $selection.aep -and $selection.container) 'Shared AEP must retain product/protocol checks.'
    Require (($selection.imageIds -join ',') -ceq (($all + @('server')) -join ',')) 'Shared AEP must validate every extension and the authority.'
    foreach ($path in @('src/Agentstration.Web/Program.cs', 'src/Agentstration.Extensions.Contracts/Contracts.cs', 'src/extensions/Unknown/Program.cs')) {
        $selection = Get-FixtureSelection @('src/extensions/Agentstration.Extensions.OpenApi/Program.cs', $path)
        Require ($selection.dotnet -and $selection.container -and 'openapi' -in $selection.imageIds) "Mixed/unknown paths lost checks: $path"
    }
    # The PostgreSQL AEP document provider is independent from server relational storage.
    Isolated @('src/extensions/Agentstration.Extensions.PostgreSql/Program.cs') @('postgresql')
    $selection = Get-FixtureSelection @('src/Agentstration.Management.Storage.PostgreSql/Migration.cs')
    Require ($selection.dotnet -and $selection.storage -and $selection.container) 'Server PostgreSQL lost product validation.'
    foreach ($path in @('tests/Agentstration.ModelProviders.Tests/ProviderFixture.cs', 'tests/Agentstration.ModelProviders.Tests/Agentstration.ModelProviders.Tests.csproj')) {
        $selection = Get-FixtureSelection @($path)
        Require ($selection.dotnet -and 'foundry' -in $selection.imageIds) "Shared provider fixture/project was exempted: $path"
    }
    foreach ($path in @('tests/Agentstration.ModelProviders.Tests/FoundryProviderTests.cs', 'tests/Agentstration.ModelProviders.Tests/FoundryLiveTests.cs')) {
        Isolated @($path) @('foundry')
    }
    Isolated @('.github/workflows/resource-plan-ux.yml') @()
    Isolated @('src/extensions/Agentstration.Extensions.OpenApi/Program.cs', '.github/workflows/resource-plan-ux.yml') @('openapi')
    # Extension-owned native assets and dedicated packaging workflows are explicit metadata.
    $nativePath = Join-Path $temporary 'deploy/images/extensions/opendal.json'
    $native = Get-Content $nativePath -Raw | ConvertFrom-Json -AsHashtable
    $native.dockerfile = 'deploy/opendal/Dockerfile'
    $native.ci = @{ sharedPaths = @('asc/**', 'asp/**'); additionalPaths = @('deploy/opendal/**', 'scripts/packaging/opendal_smoke.py', '.github/workflows/opendal-packaging.yml') }
    Write-Catalogue $native
    foreach ($path in @('deploy/opendal/Dockerfile', 'deploy/opendal/native/licenses/LICENSE', 'scripts/packaging/opendal_smoke.py', '.github/workflows/opendal-packaging.yml')) {
        Isolated @($path) @('opendal')
    }
    $selection = Get-FixtureSelection @('asc/src/Contracts.cs')
    Require ($selection.dotnet -and ($selection.imageIds -join ',') -eq 'opendal') 'Shared native protocol changes must retain product and dependent-image checks.'
    # Deletions and renames use both paths; an absent catalogue never becomes an exemption.
    Remove-Item (Join-Path $temporary 'deploy/images/extensions/ollama.json')
    $remaining = @($all | Where-Object { $_ -ne 'ollama' })
    $selection = Get-FixtureSelection @('deploy/images/extensions/ollama.json')
    Require ($selection.dotnet -and $selection.container) 'Removed catalogue skipped product checks.'
    Require (($selection.imageIds -join ',') -ceq ($remaining -join ',')) 'Removed catalogue must validate remaining extensions.'
    $selection = Get-FixtureSelection @('src/extensions/Agentstration.Extensions.OpenApi/Old.cs', 'src/Agentstration.Web/New.cs')
    Require ($selection.dotnet -and $selection.container -and 'openapi' -in $selection.imageIds) 'Rename across ownership lost checks.'
    $selection = Get-FixtureSelection @('src/extensions/Agentstration.Extensions.Ollama/Program.cs')
    Require ($selection.dotnet -and $selection.container) 'Uncatalogued extension source skipped validation.'
    $manual = & $classifier -RepositoryRoot $temporary -RunEverything
    Require ($manual.dotnet -and $manual.aep -and $manual.container -and $manual.storage) 'Manual run lost transverse checks.'
    Require (($manual.imageIds -join ',') -ceq (($remaining + @('maf-worker', 'server')) -join ',')) 'Manual run lost images.'
    $validBase = @{
        schemaVersion = 1; id = 'broken'; project = 'src/extensions/Agentstration.Extensions.Fixture/Agentstration.Extensions.Fixture.csproj'
        testProject = 'tests/Agentstration.Extensions.Fixture.Tests/Agentstration.Extensions.Fixture.Tests.csproj'
        versionFile = 'src/extensions/Agentstration.Extensions.Fixture/version.txt'; dockerfile = 'Dockerfile.extension'
    }
    Write-Catalogue $validBase
    Isolated @($validBase.project) @('broken')
    $invalidPath = Join-Path $temporary 'deploy/images/extensions/broken.json'
    Set-Content (Join-Path $temporary 'Dockerfile.worker') 'offline unrelated recipe'
    foreach ($mutation in @(
        @{ schemaVersion = 2 }, @{ schemaVersion = $true }, @{ schemaVersion = '1' }, @{ id = 'mismatched' }, @{ kind = 'awp-worker' },
        @{ project = 'src/Agentstration.Web/Agentstration.Web.csproj' }, @{ versionFile = '../outside' },
        @{ dockerfile = 'Dockerfile.worker' }, @{ ci = @{ testPaths = 'not-an-array' } }, @{ ci = @{ testPaths = @('tests/Other.Tests/**') } },
        @{ testProject = 'tests/Agentstration.ModelProviders.Tests/Agentstration.ModelProviders.Tests.csproj'; ci = @{ testPaths = @('tests/Agentstration.ModelProviders.Tests/*') } }, @{ ci = @{ additionalPaths = @('src/Agentstration.Web/**') } }
    )) {
        $invalid = $validBase.Clone()
        foreach ($key in $mutation.Keys) { $invalid[$key] = $mutation[$key] }
        $invalid | ConvertTo-Json -Depth 10 | Set-Content $invalidPath
        $failed = $false
        try { Get-FixtureSelection @('docs/README.md') | Out-Null } catch { $failed = $true }
        Require $failed 'Malformed catalogue must fail classification, not silently skip validation.'
        Remove-Item $invalidPath
    }
    Write-Host "Extension catalogue classification contracts passed ($script:assertions assertions)."
}
finally {
    if (Test-Path $temporary) { Remove-Item $temporary -Recurse -Force }
}
