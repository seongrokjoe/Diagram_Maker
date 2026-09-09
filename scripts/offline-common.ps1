$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:NUGET_CERT_REVOCATION_MODE = 'offline'
if (-not $env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages' }
if (-not $env:npm_config_cache) { $env:npm_config_cache = Join-Path $env:LOCALAPPDATA 'npm-cache' }
$env:VSTEST_TELEMETRY_OPTEDIN = '0'
$env:npm_config_offline = 'true'
$env:npm_config_audit = 'false'
$env:npm_config_fund = 'false'
$env:npm_config_ignore_scripts = 'true'
$env:npm_config_update_notifier = 'false'
# Do not inherit loader hooks or proxy/config injection into build children.
foreach ($name in @('NODE_OPTIONS', 'NODE_PATH', 'DOTNET_STARTUP_HOOKS', 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY',
    'GIT_CONFIG_COUNT', 'GIT_CONFIG_PARAMETERS', 'GIT_TRACE', 'GIT_TRACE2', 'GIT_TRACE2_EVENT')) {
    [Environment]::SetEnvironmentVariable($name, $null, 'Process')
}
$env:GIT_CONFIG_NOSYSTEM = '1'
$env:GIT_CONFIG_GLOBAL = '/dev/null'
$env:GIT_ALLOW_PROTOCOL = ''
$env:GIT_NO_LAZY_FETCH = '1'
$env:GIT_TERMINAL_PROMPT = '0'

function Assert-OfflinePath([string]$Candidate) {
    if ($Candidate -notmatch '^[A-Za-z]:[\\/]' -or $Candidate.StartsWith('\\')) { throw 'Absolute non-network path required.' }
    $resolvedAuditPath = [IO.Path]::GetFullPath($Candidate)
    if ($resolvedAuditPath -match '(?i)(^|[\\/])OneDrive($|[\\/]| - )') { throw "Cloud synchronized path is prohibited: $resolvedAuditPath" }
    foreach ($name in @('OneDrive', 'OneDriveConsumer', 'OneDriveCommercial')) {
        $cloudAuditRoot = [Environment]::GetEnvironmentVariable($name)
        if ($cloudAuditRoot -and ($resolvedAuditPath.TrimEnd('\') + '\').StartsWith($cloudAuditRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Cloud synchronized path is prohibited.'
        }
    }
    $walkAuditPath = $resolvedAuditPath
    while ($walkAuditPath) {
        if (Test-Path -LiteralPath $walkAuditPath) {
            $entryAuditPath = Get-Item -LiteralPath $walkAuditPath -Force
            if (($entryAuditPath.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked paths and cloud placeholders are prohibited.' }
        }
        $walkAuditPath = [IO.Path]::GetDirectoryName($walkAuditPath)
    }
}

function Assert-ApprovedWorkRoot([string]$Candidate) {
    Assert-OfflinePath $Candidate
    $policyAuditPath = $env:DIAGRAMMAKER_NETWORK_POLICY_PATH
    if (-not $policyAuditPath) { $policyAuditPath = Join-Path $env:LOCALAPPDATA 'DiagramMaker\network-policy.json' }
    Assert-OfflinePath $policyAuditPath
    if (-not (Test-Path -LiteralPath $policyAuditPath)) { throw 'An administrator-approved network-policy.json is required.' }
    $policyAuditData = Get-Content -LiteralPath $policyAuditPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $policyAuditData.LocalRoots -or $policyAuditData.LocalRoots -is [string]) { throw 'Network policy requires a LocalRoots array.' }
    $candidateAuditRoot = [IO.Path]::GetFullPath($Candidate).TrimEnd('\') + '\'
    $permittedAuditRoot = @($policyAuditData.LocalRoots | Where-Object {
        Assert-OfflinePath $_
        $candidateAuditRoot.StartsWith([IO.Path]::GetFullPath($_).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($permittedAuditRoot.Count -eq 0) { throw 'Work directory is outside the approved local roots.' }
}

function Assert-LastExitCode([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE" }
}
