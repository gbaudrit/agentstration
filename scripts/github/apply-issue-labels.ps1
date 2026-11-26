[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter()]
    [ValidatePattern('^[^/\s]+/[^/\s]+$')]
    [string] $Repository,

    [Parameter()]
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$labelsPath = Join-Path $repositoryRoot '.github\labels.json'

function Invoke-GitHubCli {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    $output = & gh @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI failed: $($output -join [Environment]::NewLine)"
    }

    return $output -join [Environment]::NewLine
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI (gh) is required. Install it from https://cli.github.com/.'
}

Invoke-GitHubCli -Arguments @('auth', 'status') | Out-Null

if (-not $Repository) {
    $Repository = (Invoke-GitHubCli -Arguments @('repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner')).Trim()
}

if ($Repository -notmatch '^[^/\s]+/[^/\s]+$') {
    throw "Could not determine a valid owner/repository value. Received '$Repository'."
}

if (-not (Test-Path -LiteralPath $labelsPath -PathType Leaf)) {
    throw "Label definition not found: $labelsPath"
}

$labels = @(Get-Content -LiteralPath $labelsPath -Raw | ConvertFrom-Json)
# Deployment selection follows the implemented OCI catalogue, independently of
# extension:* backlog classification. New packaged AEP components need no new list.
$extensionCatalogue = Join-Path $repositoryRoot 'deploy\images\extensions'
foreach ($file in Get-ChildItem -LiteralPath $extensionCatalogue -Filter '*.json' | Sort-Object Name) {
    $entry = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    if ($entry.schemaVersion -ne 1 -or $entry.id -notmatch '^[a-z][a-z0-9-]{0,31}$' -or
        ($entry.kind -and $entry.kind -ne 'aep-extension')) {
        throw "Invalid preview extension catalogue: $($file.Name)"
    }
    $labels += [pscustomobject]@{
        name = "preview:extension:$($entry.id)"
        color = '1D76DB'
        description = "Deploy the packaged $($entry.id) AEP extension on the next /preview deploy."
    }
}
if ($labels.Count -eq 0) {
    throw "Label definition is empty: $labelsPath"
}

$knownNames = @{}
foreach ($label in $labels) {
    $name = [string] $label.name
    $color = [string] $label.color
    $description = [string] $label.description

    if ([string]::IsNullOrWhiteSpace($name)) {
        throw 'Every label must define a non-empty name.'
    }

    $normalizedName = $name.ToLowerInvariant()
    if ($knownNames.ContainsKey($normalizedName)) {
        throw "Duplicate label name '$name' in $labelsPath."
    }

    $knownNames[$normalizedName] = $true

    if ($color -notmatch '^[0-9A-Fa-f]{6}$') {
        throw "Label '$name' has invalid color '$color'. Expected a six-character hexadecimal value."
    }

    if ([string]::IsNullOrWhiteSpace($description)) {
        throw "Label '$name' must define a non-empty description."
    }
}

if ($DryRun) {
    Write-Host "Dry run: would create or update $($labels.Count) labels in $Repository from $labelsPath."
    Write-Host 'No label changes were made.'
    $labels | Format-Table -Property name, color, description
    return
}

foreach ($label in $labels) {
    $name = [string] $label.name

    if ($PSCmdlet.ShouldProcess($Repository, "create or update label '$name'")) {
        Invoke-GitHubCli -Arguments @(
            'label', 'create', $name,
            '--repo', $Repository,
            '--color', ([string] $label.color),
            '--description', ([string] $label.description),
            '--force'
        ) | Out-Null

        Write-Host "Label '$name' is configured in $Repository."
    }
}
