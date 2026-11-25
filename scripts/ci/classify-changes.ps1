[CmdletBinding()]
param([string[]] $Paths = @(), [switch] $RunEverything)
$documentation = '^(docs/|README\.md$|CONTRIBUTING\.md$|CODE_OF_CONDUCT\.md$|SECURITY\.md$|NOTICE$|LICENSE$|\.github/(CODEOWNERS$|ISSUE_TEMPLATE/|pull_request_template\.md$|rulesets/|workflows/(documentation|publish-documentation)\.yml$))'
$shared = '^(global\.json$|Directory\.(Build|Packages)\.(props|targets)$|\.github/workflows/ci\.yml$|\.github/actions/restore-linux-build/|scripts/ci/)'
# Only the implemented Foundry vertical may replace transverse product validation.
# Unknown extensions, shared test fixtures and mixed product changes stay conservative.
$foundry = '^(src/extensions/Agentstration\.Extensions\.Foundry/|tests/Agentstration\.ModelProviders\.Tests/Foundry(Provider|Live)Tests\.cs$)'
$imageTooling = '^(deploy/images/(extensions/foundry|workers/maf-worker|applications/server)\.json$|scripts/(packaging|preview)/|deploy/preview/|Dockerfile\.extension$|\.github/workflows/(oci-images|validate-oci-image)\.yml$)'
$commonImages = '^(scripts/packaging/(images|test_images|smoke)\.py$|Dockerfile\.extension$|\.github/workflows/(oci-images|validate-oci-image)\.yml$|\.dockerignore$)'
$foundryImages = "$foundry|^(deploy/images/extensions/foundry\.json$|scripts/packaging/(enrollment_smoke|test_enrollment_smoke)\.py$|scripts/packaging/aep-extension\.smoke\.compose\.yml$|aep/|tests/Agentstration\.ModelProviders\.Tests/)"
$workerImages = '^(src/Agentstration\.Runtime\.(Worker\.MicrosoftAgentFramework|Worker\.Core|MicrosoftAgentFramework|Abstractions|Core|Api)/|src/Agentstration\.Identity[^/]*/[^/]*Awp|awp/|tests/Agentstration\.(Runtime|Awp)\.Tests/|Dockerfile\.worker$|deploy/images/workers/maf-worker\.json$|scripts/packaging/(worker_smoke|test_worker_smoke)\.py$|scripts/packaging/maf-worker\.smoke\.compose\.yml$)'
$serverImages = "^(src/Agentstration\.|aep/|awp/|Dockerfile$|deploy/images/applications/server\.json$|deploy/preview/|scripts/preview/|tests/Agentstration\.(Api|Web)[./])|$workerImages"
$imageIds = @()
if ($RunEverything.IsPresent -or @($Paths | Where-Object { $_ -match "$foundryImages|$commonImages|$shared" }).Count -gt 0) { $imageIds += 'foundry' }
if ($RunEverything.IsPresent -or @($Paths | Where-Object { $_ -match "$workerImages|$commonImages|$shared" }).Count -gt 0) { $imageIds += 'maf-worker' }
if ($RunEverything.IsPresent -or @($Paths | Where-Object { $_ -match "$serverImages|$commonImages|$shared" }).Count -gt 0) { $imageIds += 'server' }
$productPaths = @($Paths | Where-Object { $_ -notmatch $documentation -and $_ -notmatch $foundry -and $_ -notmatch $imageTooling })
[pscustomobject]@{
    dotnet = $RunEverything.IsPresent -or $productPaths.Count -gt 0
    aep = $RunEverything.IsPresent -or @($Paths | Where-Object { $_ -match "^(aep/)|$shared" }).Count -gt 0
    container = $RunEverything.IsPresent -or @($productPaths | Where-Object { $_ -match "^(src/|aep/src/|deploy/|Dockerfile$|\.dockerignore$|Agentstration\.slnx$)|$shared" }).Count -gt 0
    extensionImages = $imageIds.Count -gt 0
    imageIds = @($imageIds)
    # Include provider implementations as well as their application/composition inputs.
    storage = $RunEverything.IsPresent -or @($Paths | Where-Object { $_ -match "^(src/Agentstration\.(Application|Flow|Infrastructure|Management|ResourceManagement|ResourcePlanning|Runtime|Security|Web|Work)|src/(extensions/)?Agentstration\.[^/]*PostgreSql/|tests/Agentstration\.(Application|Performance|Web)\.Tests/|Agentstration\.Tests\.Performance\.slnx$|\.github/workflows/storage-performance\.yml$)|$shared" }).Count -gt 0
}
