param(
    [ValidateSet('Start', 'Stop')][string]$Action = 'Start',
    [switch]$NoBrowser,
    [ValidateRange(1024, 65515)][int]$Port = 5081,
    [switch]$FunctionsOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'offline-common.ps1')
Add-Type -AssemblyName System.Net.Http

function Get-UiTestRoot([string]$SourceRoot) {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $key = ([BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($SourceRoot.ToLowerInvariant())))).Replace('-', '').Substring(0, 12).ToLowerInvariant() }
    finally { $hasher.Dispose() }
    $root = Join-Path $env:LOCALAPPDATA "DiagramMaker\UiTest\$key"
    Assert-ApprovedWorkRoot $root
    return $root
}

function Resolve-UiTestApp([string]$Root, [string]$Folder) {
    if ($Folder -notmatch '^[a-f0-9]{12}-[a-f0-9]{8}$') { throw 'Invalid UI test application folder.' }
    $app = Join-Path $Root "apps\$Folder"
    Assert-ApprovedWorkRoot $app
    return $app
}

function Get-UiTestProcess($State, [string]$Root) {
    if (-not $State) { return $null }
    $app = Resolve-UiTestApp $Root $State.appFolder
    $process = Get-Process -Id ([int]$State.apiPid) -ErrorAction SilentlyContinue
    if (-not $process -or $process.ProcessName -ne 'DiagramMaker.Api') { return $null }
    $started = [DateTime]::Parse($State.apiStartedAt).ToUniversalTime()
    if ([Math]::Abs(($process.StartTime.ToUniversalTime() - $started).TotalSeconds) -gt 1) { return $null }
    if ($process.Path -ne (Join-Path $app 'DiagramMaker.Api.exe')) { return $null }
    return $process
}

function Test-UiTestReady([int]$ListenPort, [string]$WebHash) {
    if ($ListenPort -lt 1024 -or $ListenPort -gt 65535) { return $false }
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(2)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $health = $client.GetStringAsync("http://127.0.0.1:$ListenPort/health").GetAwaiter().GetResult() | ConvertFrom-Json
        if ($health.status -ne 'healthy' -or $health.service -ne 'diagram-maker-api') { return $false }
        $bytes = $client.GetByteArrayAsync("http://127.0.0.1:$ListenPort/assets/app.js").GetAwaiter().GetResult()
        $hash = ([BitConverter]::ToString($hasher.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
        return $hash -eq $WebHash
    } catch { return $false }
    finally { $hasher.Dispose(); $client.Dispose() }
}

function Get-UiTestBuild([string]$SourceRoot, [string]$Root) {
    $node = Get-Command node.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $node -or -not $dotnet) { throw 'Node.js 24+ with npm and the .NET SDK from global.json are required. See UI_TEST_KO.md.' }
    $logs = Join-Path $Root 'logs'
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    Assert-ApprovedWorkRoot $logs
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $resultPath = Join-Path $Root 'build-result.json'
    $outLog = Join-Path $logs "$stamp.build.out.log"
    $errorLog = Join-Path $logs "$stamp.build.error.log"
    $previousDotnet = $env:DIAGRAMMAKER_UI_DOTNET
    try {
        $env:DIAGRAMMAKER_UI_DOTNET = $dotnet.Source
        $startArguments = @{
            FilePath = $node.Source
            ArgumentList = @(('"' + (Join-Path $SourceRoot 'scripts/ui-test-build.mjs') + '"'),
                ('"' + $SourceRoot + '"'), ('"' + $Root + '"'), ('"' + $resultPath + '"'))
            WorkingDirectory = $Root; WindowStyle = 'Hidden'; PassThru = $true
            RedirectStandardOutput = $outLog; RedirectStandardError = $errorLog
        }
        $buildProcess = Start-Process @startArguments
        $null = $buildProcess.Handle
        $buildProcess.WaitForExit()
        if ($buildProcess.ExitCode -ne 0) {
            Get-Content -LiteralPath $errorLog -Encoding UTF8 -Tail 8 | Write-Host
            throw "Current-project UI build failed. Existing UI server/data were kept. Build logs: $logs"
        }
        Get-Content -LiteralPath $outLog -Encoding UTF8 -Tail 2 | Write-Host
        return Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    } finally { $env:DIAGRAMMAKER_UI_DOTNET = $previousDotnet }
}

if ($FunctionsOnly) { return }
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtimeRoot = Get-UiTestRoot $sourceRoot
$statePath = Join-Path $runtimeRoot 'process.json'
$lock = $null
$launched = $null
try {
    New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
    try { $lock = [IO.File]::Open((Join-Path $runtimeRoot 'launcher.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch { throw 'UI test is already starting or stopping. Please try again shortly.' }
    $state = if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    $running = Get-UiTestProcess $state $runtimeRoot
    if ($Action -eq 'Stop') {
        if ($running) { Stop-Process -Id $running.Id -Force; $running.WaitForExit() }
        if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath }
        Write-Host 'UI test stopped. Saved test workspaces and logs were kept.' -ForegroundColor Green
        return
    }
    Write-Host 'Checking the current project and local build tools...'
    $build = Get-UiTestBuild $sourceRoot $runtimeRoot
    $folder = $build.appFolder
    $app = Resolve-UiTestApp $runtimeRoot $folder
    if ($running -and $state.sourceHash -eq $build.sourceHash -and $state.appFolder -eq $folder -and
        (Test-UiTestReady $state.port $build.webSha256)) {
        $address = "http://127.0.0.1:$($state.port)"
        Write-Host "Already running with the current project: $address"
        if (-not $NoBrowser) { Start-Process "$address/?uiBuild=$($build.sourceHash)" }
        return
    }
    # Keep the old server available until the current project has built successfully.
    $preferredPort = $Port
    $running = Get-UiTestProcess $state $runtimeRoot
    if ($running) {
        $preferredPort = [int]$state.port
        if ($preferredPort -lt 1024 -or $preferredPort -gt 65515) { $preferredPort = $Port }
        Write-Host 'Current build is ready. Restarting this UI test server...'
        Stop-Process -Id $running.Id -Force
        $running.WaitForExit()
        Remove-Item -LiteralPath $statePath
    }
    $selectedPort = $null
    foreach ($candidatePort in $preferredPort..($preferredPort + 20)) {
        $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $candidatePort)
        try { $probe.Start(); $selectedPort = $candidatePort; break }
        catch [Net.Sockets.SocketException] { }
        finally { $probe.Stop() }
    }
    if (-not $selectedPort) { throw "No free local port in $preferredPort..$($preferredPort + 20). Other services were not stopped." }
    $address = "http://127.0.0.1:$selectedPort"
    $logs = Join-Path $runtimeRoot 'logs'
    $data = Join-Path $runtimeRoot 'data'
    New-Item -ItemType Directory -Path $logs, $data -Force | Out-Null
    Assert-ApprovedWorkRoot $logs
    Assert-ApprovedWorkRoot $data
    $policy = Join-Path $runtimeRoot 'ui-llm-disabled.json'
    @{ Llm = @{ Enabled = $false; AllowDevelopmentStub = $false } } | ConvertTo-Json | Set-Content -LiteralPath $policy -Encoding UTF8
    $environment = @{
        ASPNETCORE_ENVIRONMENT = 'Development'; DOTNET_ENVIRONMENT = 'Development'; ASPNETCORE_URLS = $address
        Llm__Enabled = 'false'; Llm__AllowDevelopmentStub = 'false'; DIAGRAMMAKER_LLM_POLICY_PATH = $policy
        Storage__Provider = 'LocalFile'; Storage__LocalFilePath = (Join-Path $data 'store.json')
        Security__TrustReverseProxyHeaders = 'false'; CodexTest__Enabled = 'false'; DOTNET_EnableDiagnostics = '0'
        GitWorker__NodeExecutable = $build.nodeExecutable; GitWorker__ScriptPath = (Join-Path $app 'tools\git-worker\index.mjs')
        GitWorker__Backend = 'Auto'; GitWorker__GitExecutable = 'git'
    }
    $previousEnvironment = @{}
    try {
        foreach ($item in @(Get-ChildItem Env: | Where-Object { $_.Name -match '^(ASPNETCORE_|DOTNET_|CORECLR_|COR_|Llm__|Storage__|Security__|GitWorker__|CodexTest__|Kestrel__|Urls$)' })) {
            $previousEnvironment[$item.Name] = $item.Value
            [Environment]::SetEnvironmentVariable($item.Name, $null, 'Process')
        }
        foreach ($name in $environment.Keys) {
            if (-not $previousEnvironment.ContainsKey($name)) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
            [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
        }
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        $launched = Start-Process -FilePath (Join-Path $app 'DiagramMaker.Api.exe') -ArgumentList @('--urls', $address, '--environment', 'Development') `
            -WorkingDirectory $app -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $logs "$stamp.out.log") -RedirectStandardError (Join-Path $logs "$stamp.error.log")
        $null = $launched.Handle
    } finally {
        foreach ($name in $previousEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process') }
    }
    $state = @{ apiPid = $launched.Id; apiStartedAt = $launched.StartTime.ToUniversalTime().ToString('O'); appFolder = $folder; port = $selectedPort; sourceHash = $build.sourceHash; webSha256 = $build.webSha256 }
    $state | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $launched.Refresh()
        if ($launched.HasExited) { break }
        if (Test-UiTestReady $selectedPort $build.webSha256) { $ready = $true; break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready -or $launched.HasExited) { throw "UI test failed to become ready. Logs: $logs" }
    Write-Host "UI test ready: $address" -ForegroundColor Green
    Write-Host "Current project: $($build.sourceHash.Substring(0, 12)). LLM is disabled."
    Write-Host "Test data and logs: $runtimeRoot"
    Write-Host 'Stop the server with stop-ui-test.cmd. Closing the browser does not stop it.'
    # Browser launch errors must not terminate a healthy server or discard its state.
    $launched = $null
    if (-not $NoBrowser) { Start-Process "$address/?uiBuild=$($build.sourceHash)" }
} catch {
    if ($launched) {
        if (-not $launched.HasExited) { Stop-Process -Id $launched.Id -Force }
        if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath }
    }
    Write-Host "[ERROR] $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally { if ($lock) { $lock.Dispose() } }
