[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter()]
    [ValidatePattern('^[^/\s]+/[^/\s]+$')]
    [string] $Repository,

    [Parameter()]
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$rulesetName = 'main-protection'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$rulesetPath = Join-Path $repositoryRoot '.github\rulesets\main.json'

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

if (-not (Test-Path -LiteralPath $rulesetPath -PathType Leaf)) {
    throw "Ruleset definition not found: $rulesetPath"
}

$ruleset = Get-Content -LiteralPath $rulesetPath -Raw | ConvertFrom-Json
if ($ruleset.name -ne $rulesetName) {
    throw "Expected ruleset '$rulesetName', found '$($ruleset.name)'."
}

if ($DryRun) {
    Write-Host "Dry run: would create or update '$rulesetName' in $Repository from $rulesetPath."
    Write-Host 'No Rulesets API call was made. Repository plan and permissions were not validated.'
    Get-Content -LiteralPath $rulesetPath -Raw
    return
}

try {
    $existingJson = Invoke-GitHubCli -Arguments @('api', "repos/$Repository/rulesets?per_page=100")
}
catch {
    if ($_.Exception.Message -match 'Upgrade to GitHub Pro or make this repository public') {
        throw "GitHub Rulesets are unavailable for the private repository '$Repository' on its current plan. Keep the versioned definition and retry after making the repository public or upgrading the owner to GitHub Pro (or Team/Enterprise for an organization)."
    }

    throw
}

$existingRulesets = @($existingJson | ConvertFrom-Json)
$matches = @($existingRulesets | Where-Object { $_.name -eq $rulesetName -and $_.target -eq 'branch' })

if ($matches.Count -gt 1) {
    throw "Multiple branch rulesets named '$rulesetName' exist in $Repository. Resolve the duplicate rulesets manually."
}

$action = if ($matches.Count -eq 1) { 'update' } else { 'create' }
$endpoint = if ($action -eq 'update') {
    "repos/$Repository/rulesets/$($matches[0].id)"
} else {
    "repos/$Repository/rulesets"
}
$method = if ($action -eq 'update') { 'PUT' } else { 'POST' }

if ($PSCmdlet.ShouldProcess($Repository, "$action ruleset '$rulesetName'")) {
    try {
        $result = Invoke-GitHubCli -Arguments @(
            'api',
            '--method', $method,
            $endpoint,
            '--header', 'Accept: application/vnd.github+json',
            '--input', $rulesetPath
        ) | ConvertFrom-Json
    }
    catch {
        if ($_.Exception.Message -match 'Upgrade to GitHub Pro or make this repository public') {
            throw "GitHub Rulesets are unavailable for the private repository '$Repository' on its current plan. Keep the versioned definition and retry after making the repository public or upgrading the owner to GitHub Pro (or Team/Enterprise for an organization)."
        }

        throw
    }

    Write-Host "Ruleset '$($result.name)' is $($result.enforcement) in $Repository (id: $($result.id))."
}
