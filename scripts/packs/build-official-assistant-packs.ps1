[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")),
    [string]$OutputDirectory = (Join-Path $RepositoryRoot "deploy/bootstrap/profiles/agentstration-assistant/artifacts")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$fixedTimestamp = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

function Write-DeterministicPackArchive {
    param([string]$SourceDirectory, [string]$DestinationPath)

    $temporaryPath = "$DestinationPath.tmp"
    if (Test-Path $temporaryPath) { Remove-Item $temporaryPath -Force }
    $file = [System.IO.File]::Open($temporaryPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($file, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $relativePaths = Get-ChildItem $SourceDirectory -File -Recurse -Filter "*.yaml" |
                ForEach-Object { [System.IO.Path]::GetRelativePath($SourceDirectory, $_.FullName).Replace('\', '/') } |
                Sort-Object
            foreach ($relativePath in $relativePaths) {
                $sourcePath = Join-Path $SourceDirectory $relativePath
                if (-not (Test-Path $sourcePath -PathType Leaf)) { throw "Missing Pack resource '$relativePath'." }
                $entry = $archive.CreateEntry($relativePath.Replace('\', '/'), [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $fixedTimestamp
                $entryStream = $entry.Open()
                try {
                    $sourceStream = [System.IO.File]::OpenRead($sourcePath)
                    try { $sourceStream.CopyTo($entryStream) } finally { $sourceStream.Dispose() }
                } finally { $entryStream.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $file.Dispose() }
    Move-Item $temporaryPath $DestinationPath -Force
}

New-Item $OutputDirectory -ItemType Directory -Force | Out-Null
Write-DeterministicPackArchive (Join-Path $RepositoryRoot "packs/agentstration-resource-planning") (Join-Path $OutputDirectory "agentstration-resource-planning.zip")
Write-DeterministicPackArchive (Join-Path $RepositoryRoot "packs/agentstration-assistant") (Join-Path $OutputDirectory "agentstration-assistant.zip")
