param(
    [string]$Version = '0.1.0-offline.9',
    [string]$NodeVersion = '24.12.0',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

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
if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
if (Test-Path -LiteralPath $buildRoot) { Remove-Item -LiteralPath $buildRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stageRoot, $releaseRoot, $cacheRoot, $webRoot, $workerRoot -Force | Out-Null

foreach ($name in @('package.json', 'package-lock.json', 'index.html', 'tsconfig.json', 'tsconfig.app.json', 'tsconfig.node.json', 'vite.config.mjs', 'build.mjs')) {
    Copy-Item -LiteralPath (Join-Path $sourceWebRoot $name) -Destination $webRoot
}
Copy-Item -LiteralPath (Join-Path $sourceWebRoot 'src') -Destination $webRoot -Recurse
foreach ($name in @('package.json', 'package-lock.json', 'index.mjs', 'cpp-indexer.mjs')) {
    Copy-Item -LiteralPath (Join-Path $sourceWorkerRoot $name) -Destination $workerRoot
}
$workerTestRoot = Join-Path $workerRoot 'test'
New-Item -ItemType Directory -Path $workerTestRoot -Force | Out-Null
Copy-Item -Path (Join-Path $sourceWorkerRoot 'test\*.mjs') -Destination $workerTestRoot

$nodeArchiveName = "node-v$NodeVersion-win-x64.zip"
$nodeArchive = Join-Path $cacheRoot $nodeArchiveName
$nodeChecksums = Join-Path $cacheRoot "node-v$NodeVersion-SHASUMS256.txt"
$nodeBaseUri = "https://nodejs.org/dist/v$NodeVersion"
if (-not (Test-Path -LiteralPath $nodeArchive)) {
    Invoke-WebRequest -UseBasicParsing -Uri "$nodeBaseUri/$nodeArchiveName" -OutFile $nodeArchive
}
if (-not (Test-Path -LiteralPath $nodeChecksums)) {
    Invoke-WebRequest -UseBasicParsing -Uri "$nodeBaseUri/SHASUMS256.txt" -OutFile $nodeChecksums
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
    dotnet restore (Join-Path $projectRoot 'DiagramMaker.sln') --locked-mode
    Assert-LastExitCode 'dotnet restore'
    dotnet test (Join-Path $projectRoot 'DiagramMaker.sln') -c Release --no-restore
    Assert-LastExitCode 'dotnet test'
}

& $targetNode $targetNpmCli ci --prefix $workerRoot --ignore-scripts
Assert-LastExitCode 'git worker npm ci'
Push-Location $workerRoot
try {
    & $targetNode --test
    Assert-LastExitCode 'git worker tests'
}
finally {
    Pop-Location
}
& $targetNode $targetNpmCli audit --prefix $workerRoot --audit-level=low
Assert-LastExitCode 'git worker audit'

& $targetNode $targetNpmCli ci --prefix $webRoot
Assert-LastExitCode 'web npm ci'
Push-Location $webRoot
try {
    & $targetNode $targetNpmCli run build
}
finally {
    Pop-Location
}
Assert-LastExitCode 'web build'
& $targetNode $targetNpmCli audit --prefix $webRoot --audit-level=low
Assert-LastExitCode 'web audit'
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

dotnet publish $apiProject -c Release -r win-x64 --self-contained true -o $stageRoot
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
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\windows\config') -Destination $stageRoot -Recurse

$licenseRoot = Join-Path $stageRoot 'licenses'
$npmLicenseRoot = Join-Path $licenseRoot 'npm'
New-Item -ItemType Directory -Path $licenseRoot, $npmLicenseRoot -Force | Out-Null
& $targetNode (Join-Path $projectRoot 'scripts\collect-npm-licenses.mjs') $npmLicenseRoot (Join-Path $webRoot 'node_modules') (Join-Path $workerRoot 'node_modules')
Assert-LastExitCode 'npm license collection'
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $licenseRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE_POLICY.md') -Destination $licenseRoot
Copy-Item -LiteralPath (Join-Path $nodeExtractRoot 'LICENSE') -Destination (Join-Path $licenseRoot 'NODE_LICENSE.txt')

$packageLockPath = Join-Path $projectRoot 'src\DiagramMaker.Api\packages.lock.json'
$packageLock = Get-Content -LiteralPath $packageLockPath -Raw | ConvertFrom-Json
$targetFramework = $packageLock.dependencies.'net9.0'
if (-not $targetFramework) { throw 'NuGet lock file does not contain the net9.0 target framework.' }
$globalPackages = if ($env:NUGET_PACKAGES) {
    [System.IO.Path]::GetFullPath($env:NUGET_PACKAGES)
} else {
    Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
}
if (-not (Test-Path -LiteralPath $globalPackages)) {
    throw "NuGet global package directory does not exist: $globalPackages"
}
$nugetLicenseRoot = Join-Path $licenseRoot 'nuget'
New-Item -ItemType Directory -Path $nugetLicenseRoot -Force | Out-Null
$nugetInventory = @()
foreach ($library in $targetFramework.PSObject.Properties) {
    $packageId = $library.Name
    $packageVersion = [string]$library.Value.resolved
    $source = Join-Path $globalPackages (Join-Path $packageId.ToLowerInvariant() $packageVersion.ToLowerInvariant())
    if (-not (Test-Path -LiteralPath $source)) {
        throw "NuGet package directory does not exist: $packageId $packageVersion"
    }
    $destination = Join-Path $nugetLicenseRoot ("$packageId-$packageVersion" -replace '[^A-Za-z0-9._-]', '_')
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $source -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(license|copying|notice|third.?party|.+\.nuspec)' } |
        Copy-Item -Destination $destination -Force
    if ($packageId -eq 'Npgsql') {
        Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\licenses\NPGSQL_LICENSE.txt') `
            -Destination (Join-Path $destination 'LICENSE.txt') -Force
    }
    $nugetInventory += "$packageId`t$packageVersion`t$($library.Value.type)"
}
$nugetInventory | Sort-Object | Set-Content -LiteralPath (Join-Path $nugetLicenseRoot '_inventory.tsv') -Encoding UTF8

$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Destination (Join-Path $licenseRoot 'DOTNET_LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $licenseRoot 'DOTNET_THIRD_PARTY_NOTICES.txt')

$sourceCommit = try { (git -C $projectRoot rev-parse HEAD).Trim() } catch { 'unavailable' }
$sourceTreeDirty = try { @((git -C $projectRoot status --porcelain --untracked-files=no)).Count -gt 0 } catch { $null }
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

if (Test-Path -LiteralPath $assetPath) { Remove-Item -LiteralPath $assetPath -Force }
Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $assetPath -CompressionLevel Optimal
$assetHash = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$assetHash  $assetName" | Set-Content -LiteralPath "$assetPath.sha256" -Encoding ASCII

Write-Host 'Offline package created.' -ForegroundColor Green
Write-Host $assetPath
Write-Host "$assetPath.sha256"
