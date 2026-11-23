[CmdletBinding()]
param(
    [string] $ClassificationResult = $env:CLASSIFICATION_RESULT,
    [string] $DotnetSelected = $env:DOTNET_SELECTED,
    [string] $BuildResult = $env:BUILD_RESULT,
    [string] $FunctionalResult = $env:FUNCTIONAL_RESULT,
    [string] $AepSelected = $env:AEP_SELECTED,
    [string] $AepResult = $env:AEP_RESULT,
    [string] $ExtensionImagesSelected = $env:EXTENSION_IMAGES_SELECTED,
    [string] $ExtensionImagesResult = $env:EXTENSION_IMAGES_RESULT
)

$ErrorActionPreference = 'Stop'
if ($ClassificationResult -cne 'success') {
    throw "Change classification did not succeed: '$ClassificationResult'."
}

function Assert-SelectedValidation([string] $Selected, [hashtable] $Results) {
    if ($Selected -ceq 'false') { return }
    if ($Selected -cne 'true') { throw "Missing or invalid selection: '$Selected'." }
    foreach ($result in $Results.GetEnumerator()) {
        if ($result.Value -cne 'success') {
            throw "Selected validation '$($result.Key)' did not succeed: '$($result.Value)'."
        }
    }
}

Assert-SelectedValidation $DotnetSelected @{ build = $BuildResult; functional = $FunctionalResult }
Assert-SelectedValidation $AepSelected @{ aep = $AepResult }
Assert-SelectedValidation $ExtensionImagesSelected @{ 'extension-images' = $ExtensionImagesResult }
Write-Host 'All selected validation passed.'
