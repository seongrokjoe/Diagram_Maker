param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$')][string]$Version = '0.1.0-internal.6',
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$NodeVersion = '24.12.0',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'offline-common.ps1')

function Assert-LastExitCode([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE" }
}

function Assert-ChildPath([string]$Parent, [string]$Candidate) {
    $parentPath = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $candidatePath = [System.IO.Path]::GetFullPath($Candidate)
    if (-not $candidatePath.StartsWith($parentPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside $Parent`: $candidatePath"
    }
}

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Assert-ApprovedWorkRoot $projectRoot
$artifactRoot = Join-Path $projectRoot 'artifacts'
$releaseRoot = Join-Path $artifactRoot 'release'
$cacheRoot = Join-Path $artifactRoot 'cache'
$buildRoot = Join-Path $artifactRoot 'build'
$stageRoot = Join-Path $artifactRoot "stage\DiagramMaker-$Version-win-x64"
$apiProject = Join-Path $projectRoot 'src\DiagramMaker.Api\DiagramMaker.Api.csproj'
$sourceWebRoot = Join-Path $projectRoot 'web'
$sourceWorkerRoot = Join-Path $projectRoot 'tools\git-worker'
$webRoot = Join-Path $buildRoot 'web'
$workerRoot = Join-Path $buildRoot 'git-worker'
$assetName = "DiagramMaker-$Version-win-x64.zip"
$assetPath = Join-Path $releaseRoot $assetName

foreach ($path in @($stageRoot, $releaseRoot, $buildRoot)) { Assert-ChildPath $artifactRoot $path }
foreach ($path in @($artifactRoot, $stageRoot, $releaseRoot, $buildRoot, $cacheRoot)) { Assert-ApprovedWorkRoot $path }
if (Test-Path -LiteralPath $assetPath) { throw 'This release already exists. Choose a new version to preserve prior packages.' }
if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
if (Test-Path -LiteralPath $buildRoot) { Remove-Item -LiteralPath $buildRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stageRoot, $releaseRoot, $cacheRoot, $webRoot, $workerRoot -Force | Out-Null

foreach ($name in @('package.json', 'package-lock.json', 'index.html', 'tsconfig.json', 'tsconfig.app.json', 'tsconfig.node.json', 'vite.config.mjs', 'build.mjs', 'offline-policy.mjs')) {
    Copy-Item -LiteralPath (Join-Path $sourceWebRoot $name) -Destination $webRoot
}
Copy-Item -LiteralPath (Join-Path $sourceWebRoot 'src') -Destination $webRoot -Recurse
Copy-Item -LiteralPath (Join-Path $sourceWebRoot 'test') -Destination $webRoot -Recurse
$buildScriptsRoot = Join-Path $buildRoot 'scripts'
New-Item -ItemType Directory -Path $buildScriptsRoot -Force | Out-Null
foreach ($name in @('font-assets.mjs', 'license-policy.mjs')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $buildScriptsRoot
}
foreach ($name in @('package.json', 'package-lock.json', 'index.mjs', 'cpp-indexer.mjs', 'execution-facts.mjs', 'code-block-parser.mjs', 'local-security.mjs')) {
    Copy-Item -LiteralPath (Join-Path $sourceWorkerRoot $name) -Destination $workerRoot
}
$workerTestRoot = Join-Path $workerRoot 'test'
New-Item -ItemType Directory -Path $workerTestRoot -Force | Out-Null
Copy-Item -Path (Join-Path $sourceWorkerRoot 'test\*.mjs') -Destination $workerTestRoot

$nodeArchiveName = "node-v$NodeVersion-win-x64.zip"
$nodeArchive = Join-Path $cacheRoot $nodeArchiveName
$nodeChecksums = Join-Path $cacheRoot "node-v$NodeVersion-SHASUMS256.txt"
if (-not (Test-Path -LiteralPath $nodeArchive) -or -not (Test-Path -LiteralPath $nodeChecksums)) {
    throw 'Pre-provisioned Node archive and approved checksums are required. No download is permitted.'
}
$expectedLine = Get-Content -LiteralPath $nodeChecksums | Where-Object { $_ -match "\s$([regex]::Escape($nodeArchiveName))$" } | Select-Object -First 1
if (-not $expectedLine) { throw "Node.js checksum was not found for $nodeArchiveName" }
$expectedHash = ($expectedLine -split '\s+')[0].ToUpperInvariant()
$actualHash = (Get-FileHash -LiteralPath $nodeArchive -Algorithm SHA256).Hash
if ($actualHash -ne $expectedHash) { throw 'Node.js archive SHA-256 verification failed.' }

$nodeExtractRoot = Join-Path $cacheRoot "node-v$NodeVersion-win-x64"
Assert-ChildPath $cacheRoot $nodeExtractRoot
if (Test-Path -LiteralPath $nodeExtractRoot) { Remove-Item -LiteralPath $nodeExtractRoot -Recurse -Force }
Expand-Archive -LiteralPath $nodeArchive -DestinationPath $cacheRoot -Force
$targetNode = Join-Path $nodeExtractRoot 'node.exe'
$targetNpmCli = Join-Path $nodeExtractRoot 'node_modules\npm\bin\npm-cli.js'

if (-not $SkipTests) {
    dotnet restore (Join-Path $projectRoot 'DiagramMaker.sln') --locked-mode --configfile (Join-Path $projectRoot 'NuGet.Config')
    Assert-LastExitCode 'dotnet restore'
    dotnet test (Join-Path $projectRoot 'DiagramMaker.sln') -c Release --no-restore
    Assert-LastExitCode 'dotnet test'
}

& $targetNode $targetNpmCli ci --prefix $workerRoot --offline --ignore-scripts --no-audit --no-fund
Assert-LastExitCode 'git worker npm ci'
Push-Location $workerRoot
$priorTestFixtureRoot = $env:DIAGRAMMAKER_TEST_FIXTURE_ROOT
try {
    $env:DIAGRAMMAKER_TEST_FIXTURE_ROOT = Join-Path $projectRoot 'tests/fixtures'
    & $targetNode --test
    Assert-LastExitCode 'git worker tests'
}
finally {
    $env:DIAGRAMMAKER_TEST_FIXTURE_ROOT = $priorTestFixtureRoot
    Pop-Location
}

& $targetNode $targetNpmCli ci --prefix $webRoot --offline --ignore-scripts --no-audit --no-fund
Assert-LastExitCode 'web npm ci'
Push-Location $webRoot
try {
    & $targetNode --test @(Get-ChildItem -LiteralPath 'test' -Filter '*.test.mjs' | ForEach-Object { $_.FullName })
    Assert-LastExitCode 'web interaction tests'
    & $targetNode (Join-Path $webRoot 'node_modules/typescript/bin/tsc') -b
    Assert-LastExitCode 'web TypeScript build'
    & $targetNode (Join-Path $webRoot 'build.mjs')
}
finally {
    Pop-Location
}
Assert-LastExitCode 'web build'
& $targetNode (Join-Path $projectRoot 'scripts\check-npm-licenses.mjs') (Join-Path $webRoot 'node_modules') (Join-Path $workerRoot 'node_modules')
Assert-LastExitCode 'npm license policy check'

foreach ($nativePrebuildRoot in @(
    (Join-Path $workerRoot 'node_modules\tree-sitter-cpp\prebuilds'),
    (Join-Path $workerRoot 'node_modules\tree-sitter-c\prebuilds')
)) {
    Assert-ChildPath (Join-Path $workerRoot 'node_modules') $nativePrebuildRoot
    if (Test-Path -LiteralPath $nativePrebuildRoot) {
        Remove-Item -LiteralPath $nativePrebuildRoot -Recurse -Force
    }
}

dotnet restore $apiProject -r win-x64 --locked-mode --configfile (Join-Path $projectRoot 'NuGet.Config')
Assert-LastExitCode 'offline publish restore'
dotnet publish $apiProject -c Release -r win-x64 --self-contained true --no-restore -o $stageRoot
Assert-LastExitCode 'win-x64 self-contained publish'

$wwwroot = Join-Path $stageRoot 'wwwroot'
Assert-ChildPath $stageRoot $wwwroot
if (Test-Path -LiteralPath $wwwroot) { Remove-Item -LiteralPath $wwwroot -Recurse -Force }
New-Item -ItemType Directory -Path $wwwroot | Out-Null
Copy-Item -Path (Join-Path $webRoot 'dist\*') -Destination $wwwroot -Recurse -Force

$packagedWorker = Join-Path $stageRoot 'tools\git-worker'
New-Item -ItemType Directory -Path $packagedWorker -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $workerRoot 'index.mjs') -Destination $packagedWorker
Copy-Item -LiteralPath (Join-Path $workerRoot 'cpp-indexer.mjs') -Destination $packagedWorker
Copy-Item -LiteralPath (Join-Path $workerRoot 'execution-facts.mjs') -Destination $packagedWorker
Copy-Item -LiteralPath (Join-Path $workerRoot 'code-block-parser.mjs') -Destination $packagedWorker
Copy-Item -LiteralPath (Join-Path $workerRoot 'local-security.mjs') -Destination $packagedWorker
Copy-Item -LiteralPath (Join-Path $workerRoot 'package.json') -Destination $packagedWorker
Copy-Item -LiteralPath (Join-Path $workerRoot 'package-lock.json') -Destination $packagedWorker
Copy-Item -LiteralPath (Join-Path $workerRoot 'node_modules') -Destination $packagedWorker -Recurse
if (Get-ChildItem -LiteralPath (Join-Path $packagedWorker 'node_modules') -Recurse -File -Filter '*.node' | Select-Object -First 1) {
    throw 'The Git Worker runtime contains a native Node module and cannot be copied across architectures.'
}

$packagedNode = Join-Path $stageRoot 'runtime\node'
New-Item -ItemType Directory -Path $packagedNode -Force | Out-Null
Copy-Item -LiteralPath $targetNode -Destination $packagedNode
Copy-Item -LiteralPath (Join-Path $nodeExtractRoot 'LICENSE') -Destination $packagedNode

Copy-Item -Path (Join-Path $projectRoot 'packaging\windows\*.cmd') -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\windows\OFFLINE_INSTALL_KO.txt') -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\windows\INTERNAL_TEST_KO.txt') -Destination $stageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\windows\config') -Destination $stageRoot -Recurse

$licenseRoot = Join-Path $stageRoot 'licenses'
$npmLicenseRoot = Join-Path $licenseRoot 'npm'
New-Item -ItemType Directory -Path $licenseRoot, $npmLicenseRoot -Force | Out-Null
& $targetNode (Join-Path $projectRoot 'scripts\collect-npm-licenses.mjs') $npmLicenseRoot (Join-Path $webRoot 'node_modules') (Join-Path $workerRoot 'node_modules')
Assert-LastExitCode 'npm license collection'
& $targetNode (Join-Path $projectRoot 'scripts\font-assets.mjs') $licenseRoot
Assert-LastExitCode 'font asset and license collection'
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $licenseRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE_POLICY.md') -Destination $licenseRoot
Copy-Item -LiteralPath (Join-Path $nodeExtractRoot 'LICENSE') -Destination (Join-Path $licenseRoot 'NODE_LICENSE.txt')

& (Join-Path $PSScriptRoot 'collect-nuget-licenses.ps1') -OutputRoot (Join-Path $licenseRoot 'nuget') -RuntimeAssetsPath (Join-Path $projectRoot 'src/DiagramMaker.Api/obj/project.assets.json')
$runtimeInfo = & $targetNode -p 'JSON.stringify({version: process.version, versions: process.versions})' | ConvertFrom-Json
$runtimeInfo | Add-Member -NotePropertyName sha256 -NotePropertyValue (Get-FileHash -LiteralPath $targetNode -Algorithm SHA256).Hash.ToLowerInvariant()
$runtimeInfo | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $licenseRoot 'node-runtime.json') -Encoding UTF8
& $targetNode (Join-Path $PSScriptRoot 'create-sbom.mjs') $licenseRoot (Join-Path $licenseRoot 'sbom.cdx.json')
Assert-LastExitCode 'package SBOM'
& $targetNode (Join-Path $PSScriptRoot 'check-internal-only.mjs') $stageRoot
Assert-LastExitCode 'package external inference exclusion'

$sourceCommit = 'unavailable'
$sourceTreeDirty = $null
if (Test-Path -LiteralPath (Join-Path $projectRoot '.git')) {
    $sourceCommit = (git -c "safe.directory=$projectRoot" -C $projectRoot rev-parse HEAD).Trim()
    Assert-LastExitCode 'source commit metadata'
    $sourceChanges = @(git -c "safe.directory=$projectRoot" -C $projectRoot status --porcelain)
    Assert-LastExitCode 'source working tree metadata'
    $sourceTreeDirty = $sourceChanges.Count -gt 0
}
$manifest = [ordered]@{
    product = 'Diagram Maker'
    version = $Version
    target = 'win-x64'
    sourceCommit = $sourceCommit
    sourceTreeDirty = $sourceTreeDirty
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    dotnetSdk = (dotnet --version).Trim()
    nodeRuntime = "v$NodeVersion"
    gitBackend = 'Auto (native git preferred, isomorphic-git fallback)'
    gitRuntime = 'external command from PATH'
    llmAuthentication = 'none'
    developmentStub = $false
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stageRoot 'manifest.json') -Encoding UTF8

Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $assetPath -CompressionLevel Optimal
$assetHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$assetHash  $assetName" | Set-Content -LiteralPath "$assetPath.sha256" -Encoding ASCII

Write-Host 'Offline package created.' -ForegroundColor Green
Write-Host $assetPath
Write-Host "$assetPath.sha256"
