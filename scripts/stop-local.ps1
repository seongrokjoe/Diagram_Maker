$ErrorActionPreference = 'Stop'

$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$statePath = Join-Path $projectRoot 'data\local-processes.json'

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
        Stop-Process -Id $target.Id -Force
    }
}

Remove-Item -LiteralPath $statePath -Force
Write-Host 'Diagram Maker has stopped.' -ForegroundColor Green
