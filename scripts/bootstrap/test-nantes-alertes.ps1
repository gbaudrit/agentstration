#requires -Version 7.4
<#
.SYNOPSIS
Opt-in live smoke test using isolated data, real processes and public endpoints.
.DESCRIPTION
Run from the repository root after a Release build. No live model is required.
The default run uses a synthetic, unused database password. Add -Acquire to test
the real PostgreSQL database with a password supplied through a masked prompt.
The test creates only its own processes and data, and always stops those processes.
#>
[CmdletBinding()]
param([switch] $Live, [switch] $Acquire, [securestring] $PostgreSqlPassword, [string] $BrowserSmokeScript)
if (-not $Live) { throw 'Pass -Live to authorize the public Nantes/Google News requests.' }
if ($Acquire -and -not $PostgreSqlPassword) { $PostgreSqlPassword = Read-Host 'PostgreSQL password' -AsSecureString }
$ErrorActionPreference = 'Stop'
$repo = (Get-Location).Path
$root = Join-Path $repo ('.agentstration/nantes-alertes/smoke-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()
function Port {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop(); return $port
}
$authority = 'http://127.0.0.1:' + (Port)
$openapiEndpoint = 'http://127.0.0.1:' + (Port)
$postgresEndpoint = 'http://127.0.0.1:' + (Port)
$fixturePassword = [Guid]::NewGuid().ToString('N')
$key = Join-Path $root 'aep.key'
[IO.File]::WriteAllText($key, [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(40)))
$masterKey = Join-Path $root 'master.key'
[IO.File]::WriteAllText($masterKey, [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
function Start-App($project, $settings, $name) {
    $settings.ASPNETCORE_ENVIRONMENT = 'Development'
    $settings.Logging__LogLevel__Default = 'Warning'
    $sourceRoot = if ($project -like 'Agentstration.Extensions.*') { 'src/extensions' } else { 'src' }
    $projectDirectory = Join-Path (Join-Path $repo $sourceRoot) $project
    $dll = Join-Path $projectDirectory "bin/Release/net10.0/$project.dll"
    $process = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @($dll) -WorkingDirectory $projectDirectory -Environment $settings -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $root "$name.log") -RedirectStandardError (Join-Path $root "$name.err")
    $processes.Add($process)
}
function Until($check, $label) {
    Write-Host "Waiting: $label"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
    do {
        try { if (& $check) { return } } catch { }
        if (@($processes | Where-Object HasExited).Count -gt 0) { throw "Process exited: $root" }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Timeout: $label ($root)"
}
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
function Api($method, $path, $body) {
    $arguments = @{ Uri = $authority + $path; Method = $method; WebSession = $session; Headers = @{ 'X-Agentstration-Context' = '/instance' }; TimeoutSec = 10 }
    if ($null -ne $body) { $arguments.Body = ConvertTo-Json -InputObject $body -Depth 100 -Compress; $arguments.ContentType = 'application/json' }
    Invoke-RestMethod @arguments
}
Write-Host "Isolated smoke directory: $root"
try {
    $web = @{
        ASPNETCORE_URLS = $authority; Data__Directory = (Join-Path $root 'data'); AI__Provider = 'Deterministic'
        AGENTSTRATION_MASTER_KEY_FILE = $masterKey
        Agentstration__Bootstrap__Path = (Join-Path $repo 'deploy/bootstrap/profiles')
        Agentstration__Bootstrap__InitialBootstrapEnabled = 'true'; Agentstration__Bootstrap__InitialProfiles__0 = 'development'
        Agentstration__Bootstrap__Secrets__AdminPassword = $fixturePassword
        Agentstration__Bootstrap__Secrets__NantesPostgreSqlPassword = $(if ($Acquire) { [Net.NetworkCredential]::new('', $PostgreSqlPassword).Password } else { [Guid]::NewGuid().ToString('N') })
        Agentstration__InstanceId = 'nantes-smoke'
        Agentstration__AwpWorkerTrust__Enabled = 'true'; Agentstration__AwpWorkerTrust__InstanceId = 'nantes-smoke'
        Agentstration__AwpWorkerTrust__MaximumBodyBytes = '2097152'
        Agentstration__Aep__Enrollment__AutoEnrollmentLevel = '/instance'
        Agentstration__Aep__SecretAccess__PublicBaseUrl = $authority
        'Agentstration__Extensions__Agentstration.Extensions.OpenApi__EnrollmentMode' = 'SharedKeyFile'
        'Agentstration__Extensions__Agentstration.Extensions.OpenApi__SharedKeyFile__Path' = $key
        'Agentstration__Extensions__agentstration.extensions.postgresql__EnrollmentMode' = 'SharedKeyFile'
        'Agentstration__Extensions__agentstration.extensions.postgresql__SharedKeyFile__Path' = $key
    }
    foreach ($family in @('Management', 'Flow', 'Work', 'Runtime')) { $web["Agentstration__${family}Api__BaseAddress"] = $authority }
    Start-App 'Agentstration.Web' $web web
    Until { (Invoke-WebRequest ($authority + '/health') -SkipHttpErrorCheck -TimeoutSec 2).StatusCode -eq 200 } 'Web health'
    Until { $null = Api POST '/api/auth/local/login' @{ userName = 'admin'; password = $fixturePassword; rememberMe = $false }; $true } 'Development bootstrap and login'
    foreach ($pair in @(@('Agentstration.Extensions.OpenApi', $openapiEndpoint, 'openapi'), @('Agentstration.Extensions.PostgreSql', $postgresEndpoint, 'postgres'))) {
        $settings = @{
            ASPNETCORE_URLS = $pair[1]; Aep__EnrollmentMode = 'SharedKeyFile'; Aep__SharedKeyFile__Path = $key
            Aep__SharedKeyFile__AuthorityUrl = $authority; Aep__SharedKeyFile__PublicEndpoint = $pair[1]
            Aep__SharedKeyFile__StateFile = (Join-Path $root ($pair[2] + '.instance')); Aep__SharedKeyFile__AllowInsecureHttp = 'true'
            Aep__SharedKeyFile__ClientId = 'nantes-smoke'
        }
        if ($pair[2] -eq 'openapi') { $settings.OpenApi__Enabled = 'true'; $settings.OpenApi__AllowedDomains__0 = 'data.nantesmetropole.fr' }
        else {
            $settings.PostgreSqlDocuments__AllowedClients__0 = 'nantes-smoke'
            $settings.PostgreSqlDocuments__ProvisionSchema = 'true'; $settings.PostgreSqlDocuments__AllowLoopbackWithoutTls = 'true'
        }
        Start-App $pair[0] $settings $pair[2]
    }
    Until { $null = Api GET '/api/storage-types/postgresql.jsonb?scopeRef=/instance&namespace=agentstration.extensions.postgresql'; $true } 'PostgreSQL enrollment'
    Until { $null = Api GET '/api/connected-service-types/openapi?scopeRef=/instance&namespace=Agentstration.Extensions.OpenApi'; $true } 'OpenAPI enrollment'
    $workerId = [Guid]::NewGuid().ToString('D')
    $null = Api POST '/api/awp/enrollment/announce' @{ workerId = $workerId; displayName = 'Nantes smoke'; protocolVersion = '1' }
    $pairing = Api POST "/api/identity/awp-workers/$workerId/pairing-code" @{}
    $codeFile = Join-Path $root 'worker.code'
    [IO.File]::WriteAllText($codeFile, $pairing.code)
    Start-App 'Agentstration.Runtime.Worker.MicrosoftAgentFramework' @{
        Agentstration__RuntimeWorker__AuthorityUrl = $authority; Agentstration__RuntimeWorker__WorkerId = $workerId
        Agentstration__RuntimeWorker__DisplayName = 'Nantes smoke'
        Agentstration__RuntimeWorker__InstanceId = 'nantes-smoke'; Agentstration__RuntimeWorker__AllowInsecureHttp = 'true'
        Agentstration__RuntimeWorker__CredentialStateFile = (Join-Path $root 'worker.json'); Agentstration__RuntimeWorker__PairingCodeFile = $codeFile
    } worker
    function Apply-Catalog {
        $preview = Api POST '/api/bootstrap/profiles/preview' @{ profiles = @('nantes-alertes') }
        if (-not $preview.canApply) { throw ('Catalog preview invalid: ' + (($preview.resources | Where-Object disposition -EQ 'invalid' | ForEach-Object { $_.kind + '/' + $_.name + ': ' + $_.message }) -join '; ')) }
        $application = Api POST '/api/bootstrap/applications' @{ profiles = @('nantes-alertes'); expectedDigest = $preview.digest; requestId = [Guid]::NewGuid().ToString('D') }
        $applicationId = $application.metadata.name
        Until {
            $script:catalogApplication = Api GET "/api/bootstrap/applications/$applicationId"
            $script:catalogApplication.definition.status -ne 'running'
        } 'Catalog application'
        if ($catalogApplication.definition.status -ne 'succeeded') {
            throw ('Catalog application failed: ' + (($catalogApplication.definition.resources | Where-Object disposition -EQ 'failed' | ForEach-Object { $_.kind + '/' + $_.name + ': ' + $_.failure.code }) -join '; '))
        }
    }
    Apply-Catalog
    Write-Host 'Applying the catalogue a second time to verify idempotence.'
    Apply-Catalog
    if (@($catalogApplication.definition.resources | Where-Object disposition -NE 'skip').Count -gt 0) { throw 'The second application created resources.' }
    Write-Host 'Both catalogue applications succeeded; every second-application resource was skipped.'
    if ($Acquire) {
        $acquisitions = @{}
        foreach ($sourceName in @('nantes-alertes', 'nantes-actualites')) {
            $run = Api POST "/api/datasources/$sourceName/acquisitions?scopeRef=/instance&namespace=default" @{dataSourceScopeRef='/instance'}
            $runName = $run.metadata.name
            Until { $script:completed = Api GET "/api/datasourceacquisitions/$runName`?namespace=default"; ([string]$script:completed.state) -in @('4','5','6','7','Succeeded','Failed','Cancelled','TimedOut') } "Acquisition $sourceName"
            if (([string]$completed.state) -notin @('4','Succeeded')) { throw "Acquisition failed: $($completed.errorCode)" }
            $acquisitions[$(if($sourceName -eq 'nantes-alertes'){'alertes'}else{'actualites'})] = $runName
        }
        $run = Api POST '/api/knowledgesources/nantes-alertes/projections?scopeRef=/instance&namespace=default' @{acquisitionIds=$acquisitions;idempotencyKey=[Guid]::NewGuid().ToString('N')}
        $runName = $run.metadata.name
        Until { $script:completed = Api GET "/api/knowledgeprojections/$runName`?namespace=default"; ([string]$script:completed.state) -in @('4','5','6','7','Succeeded','Failed','Cancelled','TimedOut') } 'Knowledge projection'
        if (([string]$completed.state) -notin @('4','Succeeded')) { throw "Knowledge projection failed: $($completed.errorCode)" }
        Write-Host 'Live PostgreSQL acquisitions and combined Knowledge projection passed.'
    }
    # Exercise PR #1218 using the full, unmodified RSS query and local publication.
    $run = Api POST '/api/datasources/nantes-actualites/acquisitions?scopeRef=/instance&namespace=default' @{ dataSourceScopeRef = '/instance' }
    $runName = $run.metadata.name
    Until {
        $script:rssRun = Api GET "/api/datasourceacquisitions/$runName`?namespace=default"
        ([string] $script:rssRun.state) -in @('4', '5', '6', '7', 'Succeeded', 'Failed', 'Cancelled', 'TimedOut')
    } 'RSS acquisition'
    if (([string] $rssRun.state) -notin @('4', 'Succeeded')) { throw "RSS acquisition failed: $($rssRun.errorCode)" }
    if ($null -eq $rssRun.publication) { throw 'RSS acquisition has no retained publication.' }
    Write-Host 'RSS acquisition and retained local publication succeeded.'

    # Reapplication retains operator edits; it never reconciles an existing resource.
    $source = Api GET '/api/knowledgesources/nantes-alertes?scopeRef=/instance&namespace=default'
    $source.definition.displayName = 'Operator edited this resource'
    $null = Invoke-RestMethod -Uri ($authority + '/api/knowledgesources/nantes-alertes?scopeRef=/instance&namespace=default') -Method Put -WebSession $session -Headers @{
        'X-Agentstration-Context'='/instance'; 'If-Match'=$source.eTag
    } -ContentType 'application/json' -Body (ConvertTo-Json @{properties=$source.definition} -Depth 100)
    Apply-Catalog
    $retained = Api GET '/api/knowledgesources/nantes-alertes?scopeRef=/instance&namespace=default'
    if ($retained.definition.displayName -ne 'Operator edited this resource') { throw 'Catalogue application overwrote an operator edit.' }
    if ($BrowserSmokeScript) {
        $browser = Start-Process -FilePath (Get-Command node).Source -ArgumentList @($BrowserSmokeScript) -Environment @{
            NANTES_SMOKE_AUTHORITY=$authority; NANTES_SMOKE_PASSWORD=$fixturePassword; NANTES_SMOKE_OUTPUT=$root
        } -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'browser.log') -RedirectStandardError (Join-Path $root 'browser.err')
        $processes.Add($browser)
        if (-not $browser.WaitForExit(120000)) { throw 'Browser smoke timed out.' }
        if ($browser.ExitCode -ne 0) { throw "Browser smoke failed: $root/browser.err" }
        Write-Host 'Desktop and mobile Console smoke passed.'
    }
    Write-Host 'SMOKE PASSED: catalogue application, discoveries, exact references, idempotence, live RSS and retained operator edit.'
}
catch {
    Write-Host $_.ScriptStackTrace
    throw
}
finally {
    foreach ($process in $processes) { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() } }
}
