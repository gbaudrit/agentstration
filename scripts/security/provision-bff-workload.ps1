[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $OutputPath
)

$resolvedParent = [System.IO.Path]::GetFullPath((Split-Path -Parent $OutputPath))
[System.IO.Directory]::CreateDirectory($resolvedParent) | Out-Null
$resolvedPath = [System.IO.Path]::GetFullPath((Join-Path $resolvedParent (Split-Path -Leaf $OutputPath)))
if ([System.IO.File]::Exists($resolvedPath)) {
    throw "The BFF workload credential already exists. Rotation requires a new credential id and path."
}

$bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
try {
    $value = [Convert]::ToBase64String($bytes)
    [System.IO.File]::WriteAllText($resolvedPath, $value + [Environment]::NewLine)
}
finally {
    [System.Security.Cryptography.CryptographicOperations]::ZeroMemory($bytes)
    $value = $null
}

Write-Output "BFF workload credential provisioned at $resolvedPath. Its value was not displayed."
