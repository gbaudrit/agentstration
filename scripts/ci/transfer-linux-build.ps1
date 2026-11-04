[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Export', 'Import')]
    [string] $Action,

    [Parameter(Mandatory)]
    [string] $ArchiveDirectory,

    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '../..')
)

$ErrorActionPreference = 'Stop'
if (-not $IsLinux) { throw 'Build transfer requires Linux runners.' }
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$archiveDirectoryPath = [IO.Path]::GetFullPath($ArchiveDirectory)
$archive = Join-Path $archiveDirectoryPath 'build.tar.zst'
$manifestPath = Join-Path $archiveDirectoryPath 'build-context.json'
$revision = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the checkout revision.' }
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the .NET SDK version.' }
$packages = (& dotnet nuget locals global-packages --list) -replace '^global-packages: ', ''
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the NuGet package directory.' }
$context = [ordered]@{
    version = 1
    revision = $revision
    sdk = $sdk
    os = 'Linux'
    architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    repositoryRoot = $root
    packagesRoot = $packages.Trim()
}

Push-Location $root
try {
    if ($Action -eq 'Export') {
        New-Item -ItemType Directory -Force -Path $archiveDirectoryPath | Out-Null
        # Only archive build directories belonging to tracked projects. Tar keeps
        # executable modes, timestamps, hidden generated files and relative paths.
        $projects = @(& git ls-files -- '*.csproj')
        if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tracked projects.' }
        $directories = @($projects | ForEach-Object {
            $parent = Split-Path -Parent $_
            foreach ($name in @('bin', 'obj')) {
                $relative = Join-Path $parent $name
                if (Test-Path -LiteralPath $relative -PathType Container) { $relative }
            }
        } | Sort-Object -Unique)
        if ($directories.Count -eq 0) { throw 'No build outputs were found.' }
        $list = Join-Path $archiveDirectoryPath 'directories.list'
        [IO.File]::WriteAllText($list, ($directories -join "`0") + "`0")
        $timer = [Diagnostics.Stopwatch]::StartNew()
        & tar '--use-compress-program=zstd -T2 -3 --long=27' -cf $archive --null -T $list
        if ($LASTEXITCODE -ne 0) { throw 'Could not archive the Linux build.' }
        Remove-Item -LiteralPath $list
        $context | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
        $size = [Math]::Round((Get-Item -LiteralPath $archive).Length / 1MB, 1)
        $summary = "Linux build archive: $size MiB, $([Math]::Round($timer.Elapsed.TotalSeconds, 1)) s to package."
        Write-Host $summary
        if ($env:GITHUB_STEP_SUMMARY) { $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
    }
    else {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        # Generated assets can contain absolute checkout and package paths. Reject
        # incompatible runners instead of silently rebuilding or using stale files.
        foreach ($key in $context.Keys) {
            if ([string]$manifest.$key -cne [string]$context[$key]) {
                throw "Incompatible build context '$key': expected '$($context[$key])', found '$($manifest.$key)'."
            }
        }
        if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw 'The Linux build archive is missing.' }
        & tar --zstd -xf $archive
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract the Linux build.' }
        Write-Host "Restored Linux build for $revision with SDK $sdk."
    }
}
finally { Pop-Location }
