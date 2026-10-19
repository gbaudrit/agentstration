[CmdletBinding()]
param(
    [string] $AuthorityUrl = "http://localhost:5100",
    [string] $StateDirectory = ".agentstration/runtime-worker/maf-1",
    [switch] $RestartOnFailure
)

$resolvedStateDirectory = [System.IO.Path]::GetFullPath($StateDirectory)
[System.IO.Directory]::CreateDirectory($resolvedStateDirectory) | Out-Null
$workerIdPath = [System.IO.Path]::Combine($resolvedStateDirectory, "worker-id")
$credentialPath = [System.IO.Path]::Combine($resolvedStateDirectory, "credential.json")

if ([System.IO.File]::Exists($workerIdPath)) {
    $workerId = [Guid]::Parse(([System.IO.File]::ReadAllText($workerIdPath)).Trim())
}
else {
    $workerId = [Guid]::NewGuid()
    [System.IO.File]::WriteAllText($workerIdPath, $workerId.ToString("D") + [Environment]::NewLine)
}

$allowInsecureHttp = ([Uri]$AuthorityUrl).Scheme -eq "http"
$arguments = @(
    "run",
    "--project", "src/Agentstration.Runtime.Worker.MicrosoftAgentFramework",
    "--no-launch-profile",
    "--",
    "--Agentstration:RuntimeWorker:AuthorityUrl=$AuthorityUrl",
    "--Agentstration:RuntimeWorker:WorkerId=$($workerId.ToString('D'))",
    "--Agentstration:RuntimeWorker:CredentialStateFile=$credentialPath",
    "--Agentstration:RuntimeWorker:AllowInsecureHttp=$($allowInsecureHttp.ToString().ToLowerInvariant())"
)

do {
    & dotnet @arguments
    $workerExitCode = $LASTEXITCODE
    if ($workerExitCode -eq 0 -or -not $RestartOnFailure) { break }
    Write-Warning "Runtime Worker exited with code $workerExitCode; restarting in 2 seconds."
    Start-Sleep -Seconds 2
} while ($true)

exit $workerExitCode
