param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot "src/Agentstration.Tools.SourceRegistry/Agentstration.Tools.SourceRegistry.csproj"
$fixture = Join-Path $repositoryRoot "tests/Agentstration.Tools.SourceRegistry.Tests/Fixtures/valid-source.yaml"
$registryFixture = Join-Path $repositoryRoot "tests/Agentstration.Tools.SourceRegistry.Tests/Fixtures/registry.yaml"
$fixtureRoot = Split-Path -Parent $registryFixture
$temporaryDirectory = if ($IsWindows) {
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "Temp"
}
else {
    [System.IO.Path]::GetTempPath()
}
$temporaryBase = Join-Path $temporaryDirectory "agentstration-source-registry-tool-smoke"
$temporaryRoot = Join-Path $temporaryBase ([Guid]::NewGuid().ToString("N"))
$feed = Join-Path $temporaryRoot "feed"
$consumer = Join-Path $temporaryRoot "consumer with spaces"
$publication = Join-Path $temporaryRoot "publication output"

New-Item -ItemType Directory -Path $feed, $consumer -Force | Out-Null
try {
    $packArguments = @("pack", $project, "--configuration", $Configuration, "--output", $feed)
    if ($NoBuild) { $packArguments += "--no-build" }
    & dotnet @packArguments
    if ($LASTEXITCODE -ne 0) { throw "Source Registry tool packaging failed." }

    $version = (& dotnet msbuild $project -nologo -getProperty:Version).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($version)) {
        throw "Could not resolve the Source Registry tool version."
    }

    Push-Location $consumer
    try {
        & dotnet new tool-manifest
        if ($LASTEXITCODE -ne 0) { throw "Temporary tool manifest creation failed." }
        & dotnet tool install Agentstration.SourceRegistry.Tool --version $version --add-source $feed --ignore-failed-sources
        if ($LASTEXITCODE -ne 0) { throw "Local Source Registry tool installation failed." }

        $digest = & dotnet tool run agentstration-source-registry source digest $fixture
        if ($LASTEXITCODE -ne 0 -or $digest -notmatch '^sha256:[a-f0-9]{64}$') {
            throw "Installed Source Registry digest command failed."
        }
        & dotnet tool run agentstration-source-registry source validate $fixture
        if ($LASTEXITCODE -ne 0) { throw "Installed Source Registry validation command failed." }
        & dotnet tool run agentstration-source-registry registry validate $registryFixture --publication-root $fixtureRoot --base-uri https://example.test/
        if ($LASTEXITCODE -ne 0) { throw "Installed Registry publication validation command failed." }
        & dotnet tool run agentstration-source-registry registry build $registryFixture --publication-root $fixtureRoot --base-uri https://example.test/ --output $publication
        if ($LASTEXITCODE -ne 0) { throw "Installed Registry publication build command failed." }
        if (-not (Test-Path -LiteralPath (Join-Path $publication "registry.json")) -or
            -not (Test-Path -LiteralPath (Join-Path $publication "registry.sha256")) -or
            -not (Test-Path -LiteralPath (Join-Path $publication "valid-source.yaml"))) {
            throw "Installed Registry publication build did not emit the allowlisted tree."
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    $resolvedRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    $resolvedBase = [System.IO.Path]::GetFullPath($temporaryBase) + [System.IO.Path]::DirectorySeparatorChar
    if ($resolvedRoot.StartsWith($resolvedBase, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedRoot)) {
        [System.IO.Directory]::Delete($resolvedRoot, $true)
    }
}
