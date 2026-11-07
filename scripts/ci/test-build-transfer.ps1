$ErrorActionPreference = 'Stop'
$transfer = Join-Path $PSScriptRoot 'transfer-linux-build.ps1'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('build-transfer-' + [guid]::NewGuid())
$checkout = Join-Path $temporary 'checkout'
$archive = Join-Path $temporary 'archive'
function Must-Fail([scriptblock] $Action, [string] $Message) {
    $failed = $false
    try { & $Action } catch { $failed = $true }
    if (-not $failed) { throw $Message }
}
try {
    New-Item -ItemType Directory -Force -Path "$checkout/src/Sample/bin/Release", "$checkout/src/Sample/obj/Release" | Out-Null
    '<Project />' | Set-Content "$checkout/src/Sample/Sample.csproj"
    '#!/bin/sh', 'exit 0' | Set-Content "$checkout/src/Sample/bin/Release/sample"
    & chmod +x "$checkout/src/Sample/bin/Release/sample"
    'generated asset' | Set-Content "$checkout/src/Sample/obj/Release/.asset"
    & git -C $checkout init --quiet
    & git -C $checkout add src/Sample/Sample.csproj
    & git -C $checkout -c user.name=CI -c user.email=ci@example.test commit --quiet -m fixture
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare build-transfer fixture.' }
    & $transfer -Action Export -RepositoryRoot $checkout -ArchiveDirectory $archive
    Remove-Item "$checkout/src/Sample/bin", "$checkout/src/Sample/obj" -Recurse -Force
    & $transfer -Action Import -RepositoryRoot $checkout -ArchiveDirectory $archive
    & "$checkout/src/Sample/bin/Release/sample"
    if ($LASTEXITCODE -ne 0 -or (Get-Content "$checkout/src/Sample/obj/Release/.asset") -ne 'generated asset') {
        throw 'Archive did not preserve executable outputs and hidden generated assets.'
    }
    $manifestPath = Join-Path $archive 'build-context.json'
    $original = Get-Content -LiteralPath $manifestPath -Raw
    foreach ($key in @('version', 'revision', 'sdk', 'os', 'architecture', 'repositoryRoot', 'packagesRoot')) {
        $manifest = $original | ConvertFrom-Json
        $manifest.$key = if ($key -eq 'version') { 999 } else { 'incompatible' }
        $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath
        Must-Fail { & $transfer -Action Import -RepositoryRoot $checkout -ArchiveDirectory $archive } "Accepted incompatible $key."
    }
    $original | Set-Content -LiteralPath $manifestPath
    Remove-Item (Join-Path $archive 'build.tar.zst')
    Must-Fail { & $transfer -Action Import -RepositoryRoot $checkout -ArchiveDirectory $archive } 'Accepted a missing build archive.'
    Write-Host 'Build transfer round-trip and incompatible-context checks passed.'
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
