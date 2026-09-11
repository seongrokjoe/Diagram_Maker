param([switch]$UseInstalledDependencies)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'offline-common.ps1')
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Assert-ApprovedWorkRoot $projectRoot
Push-Location $projectRoot
try {
    dotnet restore ./DiagramMaker.sln --locked-mode --configfile ./NuGet.Config
    Assert-LastExitCode 'offline locked restore'
    dotnet test ./DiagramMaker.sln -c Release --no-restore
    Assert-LastExitCode '.NET regression tests'
    foreach ($packageRoot in @('tools/git-worker', 'web')) {
        if (-not $UseInstalledDependencies) {
            npm.cmd ci --prefix $packageRoot --offline --ignore-scripts --no-audit --no-fund
            Assert-LastExitCode "$packageRoot offline npm ci"
        }
        npm.cmd test --prefix $packageRoot
        Assert-LastExitCode "$packageRoot tests"
    }
    node --test scripts/license-policy.test.mjs scripts/check-internal-only.test.mjs scripts/offline-policy.test.mjs
    Assert-LastExitCode 'license policy regression tests'
    npm.cmd run build --prefix web -- --outDir ../artifacts/verify-web-dist --emptyOutDir
    Assert-LastExitCode 'frontend build'
    node scripts/check-npm-licenses.mjs web/node_modules tools/git-worker/node_modules
    Assert-LastExitCode 'locked npm license review'
    node scripts/collect-npm-licenses.mjs artifacts/license-audit/npm web/node_modules tools/git-worker/node_modules
    Assert-LastExitCode 'npm license evidence'
    & ./scripts/collect-nuget-licenses.ps1 -OutputRoot artifacts/license-audit/nuget -IncludeTests -RuntimeAssetsPath src/DiagramMaker.Api/obj/project.assets.json
    node scripts/create-sbom.mjs artifacts/license-audit artifacts/license-audit/sbom.cdx.json
    Assert-LastExitCode 'CycloneDX inventory'
    node scripts/check-internal-only.mjs
    Assert-LastExitCode 'external inference and build source guard'
    node scripts/smoke-internal-policy.mjs
    Assert-LastExitCode 'internal startup rejection checks'
    node scripts/smoke-diagram-api.mjs
    Assert-LastExitCode 'synthetic loopback API regression'
    node scripts/smoke-diagram-api.mjs --basic
    Assert-LastExitCode 'basic mode synthetic loopback API regression'
    node scripts/smoke-shared-semantics.mjs
    Assert-LastExitCode 'shared semantic C++ and Git request-count regression'
    node scripts/smoke-shared-semantics.mjs --characters-60000
    Assert-LastExitCode 'shared semantic 60000-character C++ and Git regression'
    if (Test-Path -LiteralPath artifacts/ui-check/node_modules/playwright/package.json) {
        node scripts/smoke-svg-safety.mjs
        Assert-LastExitCode 'SVG paint preservation and security regression'
        node scripts/smoke-code-block-ui.mjs
        Assert-LastExitCode 'synthetic code block UI regression'
    } else { Write-Host 'Edge UI regression: NOT RUN (pre-provisioned Playwright is unavailable).' }
    Write-Host 'Vulnerability feed, corporate LLM/DB and host egress capture: NOT RUN. Use approved internal evidence; no public audit request is sent.'
}
finally { Pop-Location }
