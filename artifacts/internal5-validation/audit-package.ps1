param(
    [Parameter(Mandatory = $true)][string]$RepositoryRoot,
    [Parameter(Mandatory = $true)][string]$ValidationRoot
)
$ErrorActionPreference = 'Stop'
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$ValidationRoot = [IO.Path]::GetFullPath($ValidationRoot)
$version = '0.1.0-internal.5'
$expectedCommit = '7bb42f689f6fc169b7fa5b2516bc2af144171cd8'
$zipPath = Join-Path $ValidationRoot "artifacts/release/DiagramMaker-$version-win-x64.zip"
$stage = Join-Path $ValidationRoot "artifacts/stage/DiagramMaker-$version-win-x64"
$evidence = Join-Path $RepositoryRoot 'artifacts/internal5-validation'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
function Get-StreamHash([IO.Stream]$Stream) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}
function Get-Hash([string]$FilePath) {
    $stream = [IO.File]::OpenRead($FilePath)
    try { return Get-StreamHash $stream }
    finally { $stream.Dispose() }
}
$zipHash = Get-Hash $zipPath
$declared = ((Get-Content -LiteralPath "$zipPath.sha256" -Raw).Trim() -split '\s+')[0]
if ($declared -ne $zipHash) { throw 'ZIP checksum mismatch' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @()
    foreach ($entry in $archive.Entries) {
        if (-not $entry.Name) { continue }
        $name = $entry.FullName.Replace('\', '/')
        if ($name -match '^/|(^|/)\.\.(/|$)|^(data/|repositories/|\.git/|auth/|\.env$|config/(llm|network)-policy\.json$)') {
            throw "Forbidden ZIP path: $name"
        }
        $names += $name
        $stream = $entry.Open()
        try { $entryHash = Get-StreamHash $stream }
        finally { $stream.Dispose() }
        if ($entryHash -ne (Get-Hash (Join-Path $stage $name))) { throw "ZIP/stage mismatch: $name" }
    }
    foreach ($required in @('DiagramMaker.Api.exe', 'DiagramMaker.Api.dll', 'manifest.json', 'runtime/node/node.exe',
        'tools/git-worker/code-block-parser.mjs', 'wwwroot/assets/app.js', 'wwwroot/vendor/mermaid.min.js',
        'config/llm-policy.example.json', 'licenses/sbom.cdx.json', 'start.cmd', 'configure-llm.cmd',
        'OFFLINE_INSTALL_KO.txt', 'INTERNAL_TEST_KO.txt')) {
        if ($names -notcontains $required) { throw "Missing package file: $required" }
    }
} finally { $archive.Dispose() }
if ((git -C $ValidationRoot rev-parse HEAD).Trim() -ne $expectedCommit) { throw 'Source commit mismatch' }
git -C $ValidationRoot diff --exit-code HEAD --
if ($LASTEXITCODE -ne 0) { throw 'Tracked source modified during validation' }
$manifest = Get-Content -LiteralPath (Join-Path $stage 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.sourceCommit -ne $expectedCommit) { throw 'Package must identify the validated source commit' }
$untracked = @(git -C $ValidationRoot ls-files --others --exclude-standard)
$expectedArtifacts = @("artifacts/release/DiagramMaker-$version-win-x64.zip", "artifacts/release/DiagramMaker-$version-win-x64.zip.sha256")
if (@($untracked | Where-Object { $_ -notin $expectedArtifacts }).Count -gt 0) { throw 'Unexpected untracked source in release worktree' }
foreach ($relative in @('tools/git-worker/index.mjs', 'tools/git-worker/cpp-indexer.mjs', 'tools/git-worker/code-block-parser.mjs', 'tools/git-worker/local-security.mjs')) {
    if ((Get-Hash (Join-Path $ValidationRoot $relative)) -ne (Get-Hash (Join-Path $stage $relative))) { throw "Worker mismatch: $relative" }
}
foreach ($asset in Get-ChildItem -LiteralPath (Join-Path $ValidationRoot 'packaging/windows') -File | Where-Object { $_.Extension -in @('.cmd', '.txt') }) {
    if ((Get-Hash $asset.FullName) -ne (Get-Hash (Join-Path $stage $asset.Name))) { throw "Packaging asset mismatch: $($asset.Name)" }
}
$policy = Get-Content -LiteralPath (Join-Path $stage 'config/llm-policy.example.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($policy.Llm.Endpoint -ne 'https://llm.invalid/v1/chat/completions' -or $policy.Llm.SemanticJobBudgetSeconds -ne 900 -or $policy.Llm.MaxContextTokens -ne 200000) { throw 'Example policy mismatch' }
$result = [ordered]@{
    status = 'passed'; version = $version; sourceCommit = $manifest.sourceCommit; sourceTreeDirty = $manifest.sourceTreeDirty
    files = $names.Count; bytes = (Get-Item -LiteralPath $zipPath).Length; sha256 = $zipHash
    allZipBytesMatchStage = $true; sourceWorkersAndLaunchersMatch = $true; internalTestGuideIncluded = $true
    trackedSourceUnchanged = $true; onlyUntrackedFilesAreGeneratedZipAndChecksum = $true
    noRuntimeDataOrActualPolicies = $true; corporateLlmTested = $false; livePostgresTested = $false
}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'package-audit.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 5
