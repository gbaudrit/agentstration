Set-StrictMode -Version 3.0

function Get-AgentstrationSlotId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Name
    )

    $normalized = $Name.ToLowerInvariant() -replace '[^a-z0-9]+', '-'
    $normalized = $normalized.Trim('-')
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        $normalized = 'slot'
    }

    $reservedWindowsNames = @(
        'con', 'prn', 'aux', 'nul',
        'com1', 'com2', 'com3', 'com4', 'com5', 'com6', 'com7', 'com8', 'com9',
        'lpt1', 'lpt2', 'lpt3', 'lpt4', 'lpt5', 'lpt6', 'lpt7', 'lpt8', 'lpt9'
    )
    if ($normalized -in $reservedWindowsNames) {
        $normalized = "$normalized-slot"
    }

    if ($normalized.Length -gt 63) {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($normalized)
        $hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 8).ToLowerInvariant()
        $normalized = $normalized.Substring(0, 54).TrimEnd('-') + '-' + $hash
    }

    return $normalized
}

function Get-AgentstrationRepositoryRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $StartPath
    )

    $root = & git -C $StartPath rev-parse --show-toplevel 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root)) {
        throw "Unable to find an Agentstration Git worktree from '$StartPath'."
    }

    return [System.IO.Path]::GetFullPath(($root | Select-Object -First 1))
}

function Get-AgentstrationWorktreeInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot
    )

    $lines = @(& git -C $RepositoryRoot worktree list --porcelain)
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to list Git worktrees.'
    }

    $records = [System.Collections.Generic.List[object]]::new()
    $current = @{}
    foreach ($line in $lines + '') {
        if ([string]::IsNullOrWhiteSpace($line)) {
            if ($current.ContainsKey('worktree')) {
                $commit = if ($current.ContainsKey('HEAD')) { $current['HEAD'] } else { '' }
                $shortCommit = $commit.Substring(0, [Math]::Min(12, $commit.Length))
                $branch = if ($current.ContainsKey('branch')) {
                    $current['branch'] -replace '^refs/heads/', ''
                }
                else {
                    "(detached at $shortCommit)"
                }
                $slotSource = if ($current.ContainsKey('branch')) { $branch } else { "detached-$shortCommit" }
                $records.Add([pscustomobject]@{
                    SLOT = Get-AgentstrationSlotId -Name $slotSource
                    BRANCH = $branch
                    WORKTREE = [System.IO.Path]::GetFullPath($current['worktree'])
                })
            }
            $current = @{}
            continue
        }

        $key, $value = $line.Split(' ', 2)
        $current[$key] = if ($null -eq $value) { $true } else { $value }
    }

    return $records
}

function Get-AgentstrationFreeTcpPorts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateRange(1, 16)]
        [int] $Count
    )

    $listeners = [System.Collections.Generic.List[System.Net.Sockets.TcpListener]]::new()
    try {
        $ports = for ($index = 0; $index -lt $Count; $index++) {
            $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
            $listener.Start()
            $listeners.Add($listener)
            ([System.Net.IPEndPoint] $listener.LocalEndpoint).Port
        }
        return $ports
    }
    finally {
        foreach ($listener in $listeners) {
            $listener.Stop()
        }
    }
}

function Get-AgentstrationDashboardLoginUrl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $Line
    )

    $match = [System.Text.RegularExpressions.Regex]::Match(
        $Line,
        'https?://[^\s\x1b]+/login\?t=[^\s\x1b]+'
    )
    if (-not $match.Success) {
        return $null
    }

    $candidate = $match.Value.TrimEnd('.', ',', ';')
    $uri = $null
    if ([Uri]::TryCreate($candidate, [UriKind]::Absolute, [ref] $uri)) {
        return $uri.AbsoluteUri
    }

    return $null
}
