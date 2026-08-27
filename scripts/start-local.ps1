param(
    [switch]$NoBrowser,
    [ValidateRange(1024, 65535)][int]$Port = 5080
)

$ErrorActionPreference = 'Stop'

function Assert-LastExitCode([string]$Step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE"
    }
}

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$apiRoot = Join-Path $projectRoot 'src\DiagramMaker.Api'
$webRoot = Join-Path $projectRoot 'web'
$dataRoot = Join-Path $projectRoot 'data'
$logRoot = Join-Path $dataRoot 'logs'
$statePath = Join-Path $dataRoot 'local-processes.json'
$apiProject = Join-Path $apiRoot 'DiagramMaker.Api.csproj'
$apiDll = Join-Path $apiRoot 'bin\Release\net9.0\DiagramMaker.Api.dll'
$viteScript = Join-Path $webRoot 'node_modules\vite\bin\vite.js'
$webIndex = Join-Path $webRoot 'dist\index.html'

New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

if (Test-Path -LiteralPath $statePath) {
    $previous = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $running = @($previous.apiPid) | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }
    if ($running.Count -gt 0) {
        throw 'Diagram Maker is already running. Run scripts\stop-local.ps1 first.'
    }
    Remove-Item -LiteralPath $statePath -Force
}

$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
$nodePath = (Get-Command node -ErrorAction Stop).Source
$env:ASPNETCORE_ENVIRONMENT = 'Development'
# Some managed shells expose both Path and PATH; Windows Start-Process treats them as duplicate keys.
[System.Environment]::SetEnvironmentVariable('PATH', $null, [System.EnvironmentVariableTarget]::Process)

if (-not (Test-Path -LiteralPath $viteScript)) {
    npm.cmd ci --prefix $webRoot
    Assert-LastExitCode 'web npm ci'
}
if (-not (Test-Path -LiteralPath (Join-Path $projectRoot 'tools\git-worker\node_modules\isomorphic-git'))) {
    npm.cmd ci --prefix (Join-Path $projectRoot 'tools\git-worker') --ignore-scripts
    Assert-LastExitCode 'git worker npm ci'
}

$webNeedsBuild = -not (Test-Path -LiteralPath $webIndex)
if (-not $webNeedsBuild) {
    $webBuildTime = (Get-Item -LiteralPath $webIndex).LastWriteTimeUtc
    $webNeedsBuild = Get-ChildItem -LiteralPath $webRoot -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](node_modules|dist)[\\/]' -and $_.Extension -in @('.ts', '.tsx', '.css', '.html', '.json') } |
        Where-Object { $_.LastWriteTimeUtc -gt $webBuildTime } |
        Select-Object -First 1
}
if ($webNeedsBuild) {
    npm.cmd run build --prefix $webRoot
    Assert-LastExitCode 'web build'
}

$apiNeedsBuild = -not (Test-Path -LiteralPath $apiDll)
if (-not $apiNeedsBuild) {
    $apiBuildTime = (Get-Item -LiteralPath $apiDll).LastWriteTimeUtc
    $apiNeedsBuild = Get-ChildItem -LiteralPath $apiRoot -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs', '.csproj') } |
        Where-Object { $_.LastWriteTimeUtc -gt $apiBuildTime } |
        Select-Object -First 1
}
if ($apiNeedsBuild) {
    if (-not (Test-Path -LiteralPath (Join-Path $apiRoot 'obj\project.assets.json'))) {
        dotnet restore $apiProject --ignore-failed-sources
        Assert-LastExitCode 'API restore'
    }
    dotnet build $apiProject -c Release --no-restore
    Assert-LastExitCode 'API build'
}

$apiOut = Join-Path $logRoot 'api.out.log'
$apiError = Join-Path $logRoot 'api.error.log'
foreach ($log in @($apiOut, $apiError)) {
    if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
}

$portProbe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
try {
    $portProbe.Start()
}
catch {
    throw "Local port $Port is already in use. Stop the existing server or choose -Port <number>."
}
finally {
    $portProbe.Stop()
}

$apiProcess = Start-Process -FilePath $dotnetPath `
    -ArgumentList @('bin\Release\net9.0\DiagramMaker.Api.dll', '--environment', 'Development', '--urls', "http://127.0.0.1:$Port") `
    -WorkingDirectory $apiRoot -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $apiOut -RedirectStandardError $apiError

[pscustomobject]@{
    apiPid = $apiProcess.Id
    apiStartedAt = $apiProcess.StartTime.ToUniversalTime().ToString('O')
    port = $Port
    startedAt = [DateTimeOffset]::Now
} | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8

$ready = $false
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    if ($apiProcess.HasExited) { break }
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 1
        $web = Invoke-WebRequest -Uri "http://127.0.0.1:$Port" -UseBasicParsing -TimeoutSec 1
        $apiProcess.Refresh()
        if (-not $apiProcess.HasExited -and $health.status -eq 'healthy' -and $web.StatusCode -eq 200) {
            $ready = $true
            break
        }
    }
    catch {
        Start-Sleep -Milliseconds 250
    }
}

if (-not $ready) {
    if (-not $apiProcess.HasExited) { Stop-Process -Id $apiProcess.Id -Force }
    throw "Diagram Maker failed to start. Check logs in $logRoot"
}

Write-Host 'Diagram Maker is running.' -ForegroundColor Green
Write-Host "Web/API: http://localhost:$Port"
Write-Host "Logs: $logRoot"
Write-Host 'Stop: powershell -ExecutionPolicy Bypass -File .\scripts\stop-local.ps1'

if (-not $NoBrowser) {
    Start-Process "http://localhost:$Port"
}
