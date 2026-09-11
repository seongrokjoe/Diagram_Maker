param([Parameter(Mandatory)][string]$ValidationRoot, [Parameter(Mandatory)][string]$FixtureList)
$ErrorActionPreference = 'Stop'
$expectedRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Temp/DiagramMaker-performance-20260910'))
if ([IO.Path]::GetFullPath($ValidationRoot) -ne $expectedRoot) { throw 'Unexpected validation root' }
$artifactRoot = Join-Path $ValidationRoot 'artifacts'
$inventory = [Collections.Generic.List[object]]::new()
foreach ($log in Get-ChildItem -LiteralPath $artifactRoot -File -Filter 'perf3-*.log') {
    $destination = Join-Path $PSScriptRoot $log.Name
    # ReadAllText detects the UTF-16 BOM produced by Windows PowerShell redirection.
    [IO.File]::WriteAllText($destination, [IO.File]::ReadAllText($log.FullName), [Text.UTF8Encoding]::new($false))
    $inventory.Add([ordered]@{ path = $log.Name; normalizedToUtf8 = $true;
        sha256 = (Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() })
}
foreach ($name in $FixtureList.Split(',')) {
    if ($name -notmatch '^(api-smoke|internal-policy|shared-semantics|code-block-ui|offline-preview|packaged-llm|windows-launchers)-[A-Za-z0-9]+$') {
        throw "Unexpected fixture name: $name"
    }
    $fixture = Join-Path $artifactRoot $name
    $destinationRoot = Join-Path $PSScriptRoot $name
    New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $fixture -File | Where-Object {
        $_.Extension -in @('.png', '.log') -or $_.Name -in @('result.json', 'downloaded-diagnostics.json')
    }) {
        $destination = Join-Path $destinationRoot $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        $hash = (Get-FileHash -LiteralPath $file.FullName).Hash
        if ($hash -ne (Get-FileHash -LiteralPath $destination).Hash) { throw "Evidence copy mismatch: $name/$($file.Name)" }
        $inventory.Add([ordered]@{ path = "$name/$($file.Name)"; sha256 = $hash.ToLowerInvariant() })
    }
}
$inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'evidence-inventory.json') -Encoding UTF8
Write-Output "Saved $($inventory.Count) selected evidence files."
