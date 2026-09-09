param(
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [switch]$IncludeTests,
    [string]$RuntimeAssetsPath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'offline-common.ps1')
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Assert-ApprovedWorkRoot $projectRoot
$globalPackages = $env:NUGET_PACKAGES
if (-not $globalPackages) { $globalPackages = Join-Path $env:USERPROFILE '.nuget\packages' }
Assert-OfflinePath $globalPackages
$output = [IO.Path]::GetFullPath($OutputRoot)
Assert-ApprovedWorkRoot $output
New-Item -ItemType Directory -Path $output -Force | Out-Null
$items = @{}
$lockPaths = @('src/DiagramMaker.Api/packages.lock.json')
if ($IncludeTests) { $lockPaths += 'tests/DiagramMaker.Tests/packages.lock.json' }
foreach ($lockPath in $lockPaths) {
    $locked = Get-Content -LiteralPath (Join-Path $projectRoot $lockPath) -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($tfm in $locked.dependencies.PSObject.Properties) {
        foreach ($entry in $tfm.Value.PSObject.Properties) {
            if (-not $entry.Value.resolved) { continue }
            $key = "$($entry.Name.ToLowerInvariant())/$($entry.Value.resolved)"
            if (-not $items.ContainsKey($key)) {
                $items[$key] = @{name=$entry.Name;version=$entry.Value.resolved;integrity=$entry.Value.contentHash;scope=if ($lockPath.StartsWith('tests/')) {'test'} else {'application'}}
            }
        }
    }
}
if ($RuntimeAssetsPath) {
    $assets = Get-Content -LiteralPath $RuntimeAssetsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        foreach ($download in $framework.Value.downloadDependencies) {
            $bounds = @($download.version.Trim('[', ']').Split(',') | ForEach-Object { $_.Trim() })
            if ($bounds.Count -gt 1 -and $bounds[0] -ne $bounds[1]) { throw 'Unpinned runtime pack version.' }
            $version = $bounds[0]
            $key = "$($download.name.ToLowerInvariant())/$version"
            $items[$key] = @{name=$download.name;version=$version;integrity=$null;scope='runtime-pack'}
        }
    }
}
$records = @()
$hashProject = Join-Path $projectRoot 'tools/license-audit/LicenseAudit.csproj'
dotnet restore $hashProject --locked-mode --configfile (Join-Path $projectRoot 'NuGet.Config')
Assert-LastExitCode 'NuGet hash tool offline restore'
dotnet build $hashProject -c Release --no-restore
Assert-LastExitCode 'NuGet hash tool build'
$archives = @{}
foreach ($key in $items.Keys) {
    $archive = Get-ChildItem -LiteralPath (Join-Path $globalPackages $key) -Filter '*.nupkg' | Select-Object -First 1
    if (-not $archive) { throw "Missing locked archive: $key" }
    $archives[$key] = $archive.FullName
}
$hashJson = & dotnet (Join-Path $projectRoot 'tools/license-audit/bin/Release/net9.0/LicenseAudit.dll') @($archives.Values)
Assert-LastExitCode 'NuGet archive content hashes'
$contentHashes = $hashJson | ConvertFrom-Json
foreach ($key in ($items.Keys | Sort-Object)) {
    $item = $items[$key]
    $source = Join-Path $globalPackages $key
    $specFile = Get-ChildItem -LiteralPath $source -Filter '*.nuspec' -ErrorAction Stop | Select-Object -First 1
    if (-not $specFile) { throw "Missing NuGet manifest: $key" }
    [xml]$spec = Get-Content -LiteralPath $specFile.FullName -Raw -Encoding UTF8
    $license = [string]$spec.package.metadata.license.'#text'
    $licenseSource = 'nuspec'
    if (-not $license -and $key -eq 'xunit.abstractions/2.0.3' -and
        $spec.package.metadata.licenseUrl -eq 'https://raw.githubusercontent.com/xunit/xunit/master/license.txt') {
        $license = 'Apache-2.0'
        $licenseSource = 'reviewed licenseUrl; packaging/licenses/xunit-abstractions-2.0.3-review.md'
    }
    if ($license -notin @('MIT','Apache-2.0','BSD-2-Clause','BSD-3-Clause','ISC','PostgreSQL')) { throw "Unreviewed NuGet license: $key ($license)" }
    $contentHash = $contentHashes.PSObject.Properties[$archives[$key]].Value
    if ($item.integrity -and $contentHash -ne $item.integrity) { throw "NuGet content hash differs from lock: $key" }
    $archiveHash = (Get-FileHash -LiteralPath $archives[$key] -Algorithm SHA256).Hash.ToLowerInvariant()
    $destination = Join-Path $output ($key -replace '[/\\]', '-')
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $texts = @(Get-ChildItem -LiteralPath $source -File | Where-Object Name -Match '^(licen[cs]e|copying|notice|third.?party)|\.nuspec$')
    foreach ($text in $texts) { Copy-Item -LiteralPath $text.FullName -Destination $destination -Force }
    if ($item.name -eq 'Npgsql') { Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging/licenses/NPGSQL_LICENSE.txt') -Destination $destination -Force }
    if ($key -eq 'xunit.abstractions/2.0.3') {
        Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging/licenses/xunit-abstractions-2.0.3-review.md') -Destination $destination -Force
    }
    $licenseText = Join-Path $projectRoot "packaging/licenses/standard/$license.txt"
    if (Test-Path -LiteralPath $licenseText) { Copy-Item -LiteralPath $licenseText -Destination (Join-Path $destination 'LICENSE-EXPRESSION.txt') -Force }
    $evidence = @(Get-ChildItem -LiteralPath $destination -File | ForEach-Object {
        [ordered]@{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    $records += [ordered]@{ecosystem='nuget';name=$item.name;version=$item.version;declared=$license;selected=$license;scope=$item.scope;integrity="sha512-$contentHash";archiveSha256=$archiveHash;locked=[bool]$item.integrity;licenseSource=$licenseSource;evidence=$evidence;manifestSha256=(Get-FileHash -LiteralPath $specFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$records | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output '_inventory.json') -Encoding UTF8
Write-Host "Collected $($records.Count) verified NuGet package records."
