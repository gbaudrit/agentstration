param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Version,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $repositoryRoot "src/Agentstration.Tools.SourceRegistry/Agentstration.Tools.SourceRegistry.csproj"
$fixture = Join-Path $repositoryRoot "tests/Agentstration.Tools.SourceRegistry.Tests/Fixtures/valid-source.yaml"
$indexFixture = Join-Path $repositoryRoot "tests/Agentstration.Tools.SourceRegistry.Tests/Fixtures/index.yaml"
$fixtureRoot = Split-Path -Parent $indexFixture
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
$previousNugetPackages = $env:NUGET_PACKAGES
$previousDotnetCliHome = $env:DOTNET_CLI_HOME

New-Item -ItemType Directory -Path $feed, $consumer -Force | Out-Null
try {
    $packArguments = @("pack", $project, "--configuration", $Configuration, "--output", $feed)
    if ($NoBuild) { $packArguments += "--no-build" }
    if (-not [string]::IsNullOrWhiteSpace($Version)) { $packArguments += "-p:Version=$Version" }
    & dotnet @packArguments
    if ($LASTEXITCODE -ne 0) { throw "Source Registry tool packaging failed." }

    $packageVersion = if ([string]::IsNullOrWhiteSpace($Version)) {
        (& dotnet msbuild $project -nologo -getProperty:Version).Trim()
    }
    else {
        $Version
    }
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($packageVersion)) {
        throw "Could not resolve the Source Registry tool version."
    }

    Push-Location $consumer
    try {
        $env:DOTNET_CLI_HOME = Join-Path $temporaryRoot "dotnet-home"
        $env:NUGET_PACKAGES = Join-Path $temporaryRoot "packages"
        & dotnet new tool-manifest
        if ($LASTEXITCODE -ne 0) { throw "Temporary tool manifest creation failed." }
        & dotnet tool install Agentstration.SourceRegistry.Tool --version $packageVersion --source $feed --no-cache
        if ($LASTEXITCODE -ne 0) { throw "Local Source Registry tool installation failed." }

        $digest = & dotnet tool run agentstration-source-registry -- source digest $fixture
        if ($LASTEXITCODE -ne 0 -or $digest -notmatch '^sha256:[a-f0-9]{64}$') {
            throw "Installed Source Registry digest command failed."
        }
        & dotnet tool run agentstration-source-registry -- source validate $fixture
        if ($LASTEXITCODE -ne 0) { throw "Installed Source Registry validation command failed." }
        & dotnet tool run agentstration-source-registry -- registry validate $indexFixture --publication-root $fixtureRoot --base-uri https://example.test/v1/
        if ($LASTEXITCODE -ne 0) { throw "Installed Registry publication validation command failed." }
        & dotnet tool run agentstration-source-registry -- registry build $indexFixture --publication-root $fixtureRoot --base-uri https://example.test/v1/ --output $publication
        if ($LASTEXITCODE -ne 0) { throw "Installed Registry publication build command failed." }
        if (-not (Test-Path -LiteralPath (Join-Path $publication "index.json")) -or
            -not (Test-Path -LiteralPath (Join-Path $publication "index.sha256")) -or
            -not (Test-Path -LiteralPath (Join-Path $publication "registry-agentstration-0.2.json")) -or
            -not (Test-Path -LiteralPath (Join-Path $publication "registry-agentstration-0.2.sha256")) -or
            -not (Test-Path -LiteralPath (Join-Path $publication "sources/agentstration/official-samples/1/source.yaml"))) {
            throw "Installed Registry publication build did not emit the allowlisted tree."
        }
        $publishedFiles = @(Get-ChildItem -LiteralPath $publication -File -Recurse)
        if ($publishedFiles.Count -ne 5) { throw "Installed Registry publication build emitted unexpected files." }
        foreach ($digestFile in @("index.sha256", "registry-agentstration-0.2.sha256")) {
            $value = [System.IO.File]::ReadAllText((Join-Path $publication $digestFile))
            if ($value -notmatch '^sha256:[a-f0-9]{64}\n$') { throw "Installed Registry publication emitted an invalid digest file." }
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    if ([string]::IsNullOrEmpty($previousNugetPackages)) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $previousNugetPackages
    }
    if ([string]::IsNullOrEmpty($previousDotnetCliHome)) {
        Remove-Item Env:DOTNET_CLI_HOME -ErrorAction SilentlyContinue
    }
    else {
        $env:DOTNET_CLI_HOME = $previousDotnetCliHome
    }
    $resolvedRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    $resolvedBase = [System.IO.Path]::GetFullPath($temporaryBase) + [System.IO.Path]::DirectorySeparatorChar
    if ($resolvedRoot.StartsWith($resolvedBase, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedRoot)) {
        [System.IO.Directory]::Delete($resolvedRoot, $true)
    }
}
