param([Parameter(Mandatory)][string]$ValidationRoot)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$stage = Join-Path $ValidationRoot 'artifacts/stage/DiagramMaker-0.1.0-perf.3-win-x64'
$archivePath = Join-Path $ValidationRoot 'artifacts/release/DiagramMaker-0.1.0-perf.3-win-x64.zip'
$digest = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$expected = ((Get-Content -LiteralPath ($archivePath + '.sha256') -Raw).Trim() -split '\s+')[0]
if ($digest -ne $expected) { throw 'ZIP digest mismatch' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
$entries = @{}
$hasher = [Security.Cryptography.SHA256]::Create()
try {
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.EndsWith('/')) { continue }
        if ($name -match '(^|/)(\.git|data)(/|$)|(^|/)(llm-policy|network-policy)\.json$' -or
            $name -match '(^|/)\.\.?(/|$)' -or [IO.Path]::IsPathRooted($name)) { throw "Forbidden package entry: $name" }
        if ($entries.ContainsKey($name)) { throw "Duplicate entry: $name" }
        $stream = $entry.Open()
        try { $hash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose() }
        $stagedFile = Join-Path $stage $name
        if (-not (Test-Path -LiteralPath $stagedFile -PathType Leaf) -or
            $hash -ne (Get-FileHash -LiteralPath $stagedFile -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Stage mismatch: $name" }
        $entries[$name] = $hash
    }
} finally { $archive.Dispose(); $hasher.Dispose() }
$stagedFiles = @(Get-ChildItem -LiteralPath $stage -Recurse -File -Force)
if ($entries.Count -ne $stagedFiles.Count) { throw 'ZIP/stage file counts differ' }
foreach ($name in @('DiagramMaker.Api.exe', 'DiagramMaker.Api.dll', 'wwwroot/assets/app.js',
    'wwwroot/vendor/mermaid.min.js', 'tools/git-worker/code-block-parser.mjs', 'runtime/node/node.exe',
    'INTERNAL_TEST_KO.txt', 'start.cmd', 'start-with-network-policy.cmd', 'manifest.json')) {
    if (-not $entries.ContainsKey($name)) { throw "Missing package file: $name" }
}
$manifest = Get-Content -LiteralPath (Join-Path $stage 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.version -ne '0.1.0-perf.3') { throw 'Unexpected package version' }
if ($entries['INTERNAL_TEST_KO.txt'] -ne (Get-FileHash -LiteralPath (Join-Path $sourceRoot 'packaging/windows/INTERNAL_TEST_KO.txt')).Hash.ToLowerInvariant()) { throw 'Packaged guide mismatch' }
foreach ($name in @('config/llm-policy.example.json', 'config/LLM_POLICY_KO.txt')) {
    if (-not $entries.ContainsKey($name) -or $entries[$name] -ne
        (Get-FileHash -LiteralPath (Join-Path $sourceRoot ('packaging/windows/' + $name))).Hash.ToLowerInvariant()) {
        throw "Packaged LLM example mismatch: $name"
    }
}
$policy = (Get-Content -LiteralPath (Join-Path $stage 'config/llm-policy.example.json') -Raw -Encoding UTF8 | ConvertFrom-Json).Llm
if ($policy.MaxInputCharacters -ne 2000000 -or $policy.MaxInputTokens -ne 200000 -or
    $policy.MaxContextTokens -ne 200000 -or $policy.DiagramOutputTokens -ne 8000 -or
    $policy.ReviewOutputTokens -ne 2000 -or $policy.AllowedOrigin -ne 'https://llm.invalid' -or
    $policy.Endpoint -ne 'https://llm.invalid/v1/chat/completions' -or $policy.Model -ne 'approved-model') {
    throw 'Unexpected completed LLM policy example'
}
$sourceFiles = @(& git -C $sourceRoot ls-files --cached --others --exclude-standard) | Where-Object {
    $_ -match '^(src|tests|scripts|web|tools|packaging)/' -or $_ -match '^(DiagramMaker\.sln|NuGet\.Config|global\.json|Directory\.[^/]+|LICENSE_POLICY\.md|THIRD_PARTY_NOTICES\.md|license-allowlist\.json)$'
}
if ($LASTEXITCODE -ne 0) { throw 'Source inventory failed' }
$inventory = foreach ($relative in $sourceFiles) {
    $sourceFile = Join-Path $sourceRoot $relative
    $validatedFile = Join-Path $ValidationRoot $relative
    if (-not (Test-Path -LiteralPath $validatedFile -PathType Leaf)) { throw "Validation file missing: $relative" }
    $hash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne (Get-FileHash -LiteralPath $validatedFile -Algorithm SHA256).Hash.ToLowerInvariant()) { throw "Validation source mismatch: $relative" }
    [ordered]@{ path = $relative; sha256 = $hash }
}
$inventory | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'source-inventory.json') -Encoding UTF8
$previousHash = (Get-FileHash -LiteralPath (Join-Path $sourceRoot 'artifacts/release/DiagramMaker-0.1.0-perf.2-win-x64.zip')).Hash.ToLowerInvariant()
if ($previousHash -ne '711b777085ef85f181bee4ddfb6efcc6afd430adc0d25c6509331aaf876a61f4') { throw 'Previous release changed' }
$result = [ordered]@{ status = 'passed'; version = $manifest.version; sha256 = $digest;
    bytes = (Get-Item -LiteralPath $archivePath).Length; files = $entries.Count; stageFilesMatch = $true;
    sourceFilesMatch = @($inventory).Count; baseCommit = (& git -C $sourceRoot rev-parse HEAD).Trim();
    manifestSourceCommit = $manifest.sourceCommit; manifestSourceTreeDirty = $manifest.sourceTreeDirty;
    sourceNote = 'Built from an uncommitted source mirror without .git; source-inventory.json records the exact validated files.';
    perf2Sha256 = $previousHash; llmPolicyExampleMatch = $true;
    maxInputCharacters = $policy.MaxInputCharacters; maxInputTokens = $policy.MaxInputTokens;
    maxContextTokens = $policy.MaxContextTokens }
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'package-audit.json') -Encoding UTF8
$result | ConvertTo-Json -Depth 4
