param([string]$Destination, [switch]$IncludeDependencies)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
if (-not $Destination) { $Destination = Join-Path $tempRoot ('DiagramMaker-natural-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$validationRoot = [IO.Path]::GetFullPath($Destination)
if (-not $validationRoot.StartsWith($tempRoot + 'DiagramMaker-natural-', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Validation destination must be a dedicated DiagramMaker-natural-* folder under the local temporary directory.'
}
New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null
$files = @(git -c core.quotepath=false -C $sourceRoot ls-files --cached --others --exclude-standard) |
    Where-Object { $_ -notmatch '^(artifacts|data|repositories)/' -and $_ -notmatch '\.zip$' } | Sort-Object -Unique
if ($LASTEXITCODE -ne 0) { throw 'Source inventory failed.' }
$hashes = @()
foreach ($relative in $files) {
    $sourceFile = [IO.Path]::GetFullPath((Join-Path $sourceRoot $relative))
    $targetFile = [IO.Path]::GetFullPath((Join-Path $validationRoot $relative))
    if (-not $sourceFile.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not $targetFile.StartsWith($validationRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Source path escapes workspace.' }
    if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { continue }
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($targetFile)) -Force | Out-Null
    Copy-Item -LiteralPath $sourceFile -Destination $targetFile -Force
    $hash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash
    if ($hash -ne (Get-FileHash -LiteralPath $targetFile -Algorithm SHA256).Hash) { throw "Source mismatch: $relative" }
    $hashes += [ordered]@{ path = $relative; sha256 = $hash.ToLowerInvariant() }
}
if ($IncludeDependencies) {
    foreach ($relative in @('web/node_modules', 'tools/git-worker/node_modules', 'artifacts/ui-check', 'artifacts/cache')) {
        $sourceDirectory = Join-Path $sourceRoot $relative
        $targetDirectory = Join-Path $validationRoot $relative
        if (Test-Path -LiteralPath $sourceDirectory) {
            New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
            & robocopy $sourceDirectory $targetDirectory /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP
            if ($LASTEXITCODE -ge 8) { throw "Dependency copy failed: $relative" }
        }
    }
}
$evidenceRoot = Join-Path $sourceRoot 'artifacts/natural-reliability'
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'source-hashes.json') -Encoding UTF8
[ordered]@{ validationRoot = $validationRoot; sourceCommit = (git -C $sourceRoot rev-parse HEAD).Trim(); sourceTreeDirty = $true;
    files = $hashes.Count; copiedAt = [DateTimeOffset]::UtcNow.ToString('O') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidenceRoot 'validation-copy.json') -Encoding UTF8
Write-Output $validationRoot
