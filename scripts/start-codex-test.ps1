param(
    [switch]$NoBrowser,
    [ValidateRange(1024, 65535)][int]$Port = 5081,
    [string]$CodexExecutable,
    [string]$Model
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'start-local.ps1') -CodexTest -NoBrowser:$NoBrowser -Port $Port -CodexExecutable $CodexExecutable -CodexModel $Model
