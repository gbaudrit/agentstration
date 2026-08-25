[CmdletBinding()]
param()

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'slot-tools.ps1')

$repositoryRoot = Get-AgentstrationRepositoryRoot -StartPath $PSScriptRoot
Get-AgentstrationWorktreeInfo -RepositoryRoot $repositoryRoot |
    Format-Table -Property SLOT, BRANCH, WORKTREE -AutoSize -Wrap
