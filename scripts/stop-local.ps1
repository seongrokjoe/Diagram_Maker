param([switch]$CodexTest)
$ErrorActionPreference = 'Stop'

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$statePath = Join-Path $projectRoot 'data\local-processes.json'
if ($CodexTest) { $statePath = Join-Path $projectRoot 'artifacts\codex-test\local-processes.json' }

if (-not (Test-Path -LiteralPath $statePath)) {
    Write-Host 'Diagram Maker local processes are not running.'
    exit 0
}

$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
$targets = @(
    [pscustomobject]@{ Id = $state.apiPid; Name = 'dotnet'; StartedAt = [DateTime]::Parse($state.apiStartedAt).ToUniversalTime() }
)
foreach ($target in $targets) {
    $process = Get-Process -Id $target.Id -ErrorAction SilentlyContinue
    $sameStart = $null -ne $process -and [Math]::Abs(($process.StartTime.ToUniversalTime() - $target.StartedAt).TotalSeconds) -lt 2
    if ($sameStart -and $process.ProcessName -eq $target.Name) {
        if ($CodexTest) {
            try { Invoke-RestMethod -Uri "http://127.0.0.1:$($state.port)/api/v1/sample-tests/shutdown" -Method Post -TimeoutSec 3 | Out-Null } catch { }
            if (-not $process.WaitForExit(10000)) {
                # The PID/name/start time above identify only this launcher's process tree.
                & taskkill.exe /PID $target.Id /T /F | Out-Null
            }
        } else { Stop-Process -Id $target.Id -Force }
    }
}

Remove-Item -LiteralPath $statePath -Force
Write-Host 'Diagram Maker has stopped.' -ForegroundColor Green
