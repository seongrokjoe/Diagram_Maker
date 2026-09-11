param([Parameter(Mandatory)][string]$ValidationRoot)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$expectedRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Temp/DiagramMaker-performance-20260910'))
if ([IO.Path]::GetFullPath($ValidationRoot) -ne $expectedRoot) { throw 'Unexpected validation mirror' }
$files = @(& git -C $sourceRoot ls-files --cached --others --exclude-standard) | Where-Object {
    $_ -match '^(src|tests|scripts|web|tools|packaging)/' -or $_ -match '^(DiagramMaker\.sln|NuGet\.Config|global\.json|Directory\.[^/]+|LICENSE_POLICY\.md|THIRD_PARTY_NOTICES\.md|license-allowlist\.json)$'
}
if ($LASTEXITCODE -ne 0) { throw 'Source enumeration failed' }
$copied = 0
foreach ($relative in $files) {
    $source = Join-Path $sourceRoot $relative
    $destination = [IO.Path]::GetFullPath((Join-Path $ValidationRoot $relative))
    if (-not $destination.StartsWith($expectedRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Path outside mirror' }
    if (-not (Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash) {
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
        $copied++
    }
}
Write-Output "Validated source mirror: $($files.Count) files, copied $copied."
