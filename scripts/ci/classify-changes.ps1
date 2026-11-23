[CmdletBinding()]
param([string[]] $Paths = @(), [switch] $RunEverything)
$documentation = '^(docs/|README\.md$|CONTRIBUTING\.md$|CODE_OF_CONDUCT\.md$|SECURITY\.md$|NOTICE$|LICENSE$|\.github/(CODEOWNERS$|ISSUE_TEMPLATE/|pull_request_template\.md$|rulesets/|workflows/(documentation|publish-documentation)\.yml$))'
$shared = '^(global\.json$|Directory\.(Build|Packages)\.(props|targets)$|\.github/workflows/ci\.yml$|\.github/actions/restore-linux-build/|scripts/ci/)'
# Only the implemented Foundry vertical may replace transverse product validation.
# Unknown extensions, shared test fixtures and mixed product changes stay conservative.
$foundry = '^(src/extensions/Agentstration\.Extensions\.Foundry/|tests/Agentstration\.ModelProviders\.Tests/Foundry(Provider|Live)Tests\.cs$)'
$imageTooling = '^(deploy/images/extensions/foundry\.json$|scripts/packaging/|Dockerfile\.extension$|\.github/workflows/(extension-images|validate-extension-image)\.yml$)'
$productPaths = @($Paths | Where-Object { $_ -notmatch $documentation -and $_ -notmatch $foundry -and $_ -notmatch $imageTooling })
[pscustomobject]@{
    dotnet = $RunEverything.IsPresent -or $productPaths.Count -gt 0
    aep = $RunEverything.IsPresent -or @($Paths | Where-Object { $_ -match "^(aep/)|$shared" }).Count -gt 0
    container = $RunEverything.IsPresent -or @($productPaths | Where-Object { $_ -match "^(src/|aep/src/|deploy/|Dockerfile$|\.dockerignore$|Agentstration\.slnx$)|$shared" }).Count -gt 0
    extensionImages = $RunEverything.IsPresent -or @($Paths | Where-Object { $_ -match "$foundry|$imageTooling|^(aep/|tests/Agentstration\.ModelProviders\.Tests/|\.dockerignore$)|$shared" }).Count -gt 0
    # Include provider implementations as well as their application/composition inputs.
    storage = $RunEverything.IsPresent -or @($Paths | Where-Object { $_ -match "^(src/Agentstration\.(Application|Flow|Infrastructure|Management|ResourceManagement|ResourcePlanning|Runtime|Security|Web|Work)|src/(extensions/)?Agentstration\.[^/]*PostgreSql/|tests/Agentstration\.(Application|Performance|Web)\.Tests/|Agentstration\.Tests\.Performance\.slnx$|\.github/workflows/storage-performance\.yml$)|$shared" }).Count -gt 0
}
