[CmdletBinding()]
param([string[]] $Paths = @(), [switch] $RunEverything)
$documentation = '^(docs/|README\.md$|CONTRIBUTING\.md$|CODE_OF_CONDUCT\.md$|SECURITY\.md$|NOTICE$|LICENSE$|\.github/(CODEOWNERS$|ISSUE_TEMPLATE/|pull_request_template\.md$|rulesets/|workflows/(documentation|publish-documentation)\.yml$))'
$shared = '^(global\.json$|Directory\.(Build|Packages)\.(props|targets)$|\.github/workflows/ci\.yml$|scripts/ci/)'
[pscustomobject]@{
    dotnet = $RunEverything.IsPresent -or @($Paths | Where-Object { $_ -notmatch $documentation }).Count -gt 0
    aep = $RunEverything.IsPresent -or @($Paths | Where-Object { $_ -match "^(aep/)|$shared" }).Count -gt 0
    container = $RunEverything.IsPresent -or @($Paths | Where-Object { $_ -match "^(src/|aep/src/|deploy/|Dockerfile$|\.dockerignore$|Agentstration\.slnx$)|$shared" }).Count -gt 0
    # Include provider implementations as well as their application/composition inputs.
    storage = $RunEverything.IsPresent -or @($Paths | Where-Object { $_ -match "^(src/Agentstration\.(Application|Flow|Infrastructure|Management|ResourceManagement|ResourcePlanning|Runtime|Security|Web|Work)|src/Agentstration\.[^/]*PostgreSql/|tests/Agentstration\.(Application|Performance|Web)\.Tests/|Agentstration\.Tests\.Performance\.slnx$|\.github/workflows/storage-performance\.yml$)|$shared" }).Count -gt 0
}
