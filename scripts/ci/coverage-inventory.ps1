$ErrorActionPreference = 'Stop'
function Get-TestProjects([string] $Root, [string] $Solution) {
    [xml] $document = Get-Content -LiteralPath (Join-Path $Root $Solution) -Raw
    @($document.SelectNodes('//Project') | ForEach-Object { [string]$_.Path })
}
function Assert-CoverageInventory([string] $Root) {
    $integration = @(Get-TestProjects $Root 'Agentstration.Tests.Integration.slnx')
    $shards = @('A', 'B' | ForEach-Object { Get-TestProjects $Root "Agentstration.Tests.Coverage$_.slnx" })
    $duplicates = @($shards | Group-Object | Where-Object Count -gt 1)
    $difference = @(Compare-Object $integration $shards)
    $fast = @(Get-TestProjects $Root 'Agentstration.Tests.Fast.slnx')
    if ($duplicates.Count -or $difference.Count -or @($fast | Where-Object { $_ -in $integration }).Count) {
        throw "Coverage inventory mismatch. Duplicates: $($duplicates.Name -join ', '); differences: $($difference.InputObject -join ', '); Fast must be disjoint from Integration."
    }
}
function Assert-CoverageReports([string] $Root, [string[]] $Projects) {
    $files = @(Get-ChildItem -LiteralPath $Root -Filter '*.cobertura.xml' -File -Recurse)
    # TRX creates attachment copies under its deployment directory. Merge each
    # logical report once, accepting copies only when their bytes agree.
    $files = @($files | Group-Object Name | ForEach-Object {
        $hashes = @($_.Group | Get-FileHash -Algorithm SHA256 | Select-Object -ExpandProperty Hash -Unique)
        if ($hashes.Count -ne 1) { throw "Conflicting coverage copies: $($_.Name)." }
        $_.Group | Sort-Object { $_.FullName.Length } | Select-Object -First 1
    })
    $used = @()
    foreach ($project in $Projects) {
        $module = [IO.Path]::GetFileNameWithoutExtension($project)
        $trx = @(Get-ChildItem -LiteralPath $Root -Filter "$module.trx" -File -Recurse)
        if ($trx.Count -ne 1) { throw "Expected one TRX report for $module, found $($trx.Count)." }
        [xml] $report = Get-Content -LiteralPath $trx[0].FullName -Raw
        $links = @($report.SelectNodes("//*[local-name()='CollectorDataEntries']//*[local-name()='A']") | Where-Object { $_.href -like '*.cobertura.xml' })
        if ($links.Count -ne 1) { throw "Expected one coverage attachment for $module." }
        $leaf = [IO.Path]::GetFileName(([string]$links[0].href).Replace('\', '/'))
        $coverage = @($files | Where-Object { $_.Name -eq $leaf })
        if ($coverage.Count -ne 1 -or $coverage[0].FullName -in $used) { throw "Missing or duplicate coverage report for $module ($leaf)." }
        [xml] $xml = Get-Content -LiteralPath $coverage[0].FullName -Raw
        if ($null -eq $xml.SelectSingleNode("/*[local-name()='coverage']")) { throw "Invalid Cobertura report for $module." }
        $used += $coverage[0].FullName
    }
    if ($used.Count -ne $files.Count) { throw 'Unexpected coverage reports outside the required module inventory.' }
    $files
}
