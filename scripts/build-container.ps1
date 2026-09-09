param(
    [Parameter(Mandatory=$true)][string]$PreparedRoot,
    [Parameter(Mandatory=$true)][string]$RuntimeImage,
    [string]$Tag = 'diagram-maker:internal'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'offline-common.ps1')
Assert-ApprovedWorkRoot $PreparedRoot
if ($RuntimeImage -notmatch '^[-a-zA-Z0-9./_:]+@sha256:[a-f0-9]{64}$') { throw 'An approved runtime image pinned by digest is required.' }
if ($env:DOCKER_HOST -and $env:DOCKER_HOST -notmatch '^(npipe://|unix://)') { throw 'Remote Docker builders are prohibited.' }
$endpoint = docker context inspect --format '{{.Endpoints.docker.Host}}'
Assert-LastExitCode 'inspect local Docker context'
if ($endpoint -notmatch '^(npipe://|unix://)') { throw 'A local Docker engine is required.' }
$driver = docker buildx inspect default --format '{{.Driver}}'
Assert-LastExitCode 'inspect built-in local builder'
if ($driver -ne 'docker') { throw 'Only the built-in local Docker builder is permitted.' }
docker image inspect $RuntimeImage | Out-Null
Assert-LastExitCode 'approved runtime image must already be loaded; pulling is prohibited'
foreach ($required in @('api/DiagramMaker.Api.dll','wwwroot/index.html','worker/index.mjs','worker/local-security.mjs',
    'runtime/node','licenses/sbom.cdx.json','licenses/base-image-sbom.json','licenses/base-image-review.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PreparedRoot $required))) { throw "Prepared container artifact missing: $required" }
}
$review = Get-Content -LiteralPath (Join-Path $PreparedRoot 'licenses/base-image-review.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($review.image -ne $RuntimeImage -or $review.status -ne 'approved-internal-use' -or -not $review.reviewer) {
    throw 'Container OS license review must identify the exact runtime image digest and reviewer.'
}
foreach ($entry in Get-ChildItem -LiteralPath $PreparedRoot -Force) {
    if ($entry.Name -notin @('api','wwwroot','worker','runtime','licenses')) { throw 'Container context must contain only the five reviewed artifact directories.' }
}
foreach ($entry in Get-ChildItem -LiteralPath $PreparedRoot -Recurse -Force) {
    Assert-OfflinePath $entry.FullName
    if ($entry.Name -match '^(data|repositories|\.git|\.codex|\.env|.*policy.*\.json|appsettings.*\.json|auth\.json)$') {
        throw 'Configuration, repository or runtime data found in container context.'
    }
}
docker build --builder default --pull=false --network=none --build-arg "DOTNET_RUNTIME_IMAGE=$RuntimeImage" -t $Tag -f (Join-Path $PSScriptRoot '../Dockerfile') $PreparedRoot
Assert-LastExitCode 'offline container build'
