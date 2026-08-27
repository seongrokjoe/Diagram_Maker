$ErrorActionPreference = 'Stop'

function Assert-LastExitCode([string]$Step) {
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE"
    }
}

dotnet restore .\DiagramMaker.sln --locked-mode --ignore-failed-sources
Assert-LastExitCode 'dotnet restore'
dotnet test .\DiagramMaker.sln -c Release --no-restore
Assert-LastExitCode 'dotnet test'

Push-Location .\tools\git-worker
try {
    npm.cmd ci
    Assert-LastExitCode 'git worker npm ci'
    npm.cmd test
    Assert-LastExitCode 'git worker tests'
    npm.cmd audit --audit-level=low
    Assert-LastExitCode 'git worker audit'
}
finally {
    Pop-Location
}

Push-Location .\web
try {
    npm.cmd ci
    Assert-LastExitCode 'web npm ci'
    $webRoot = (Get-Location).Path
    $distPath = [System.IO.Path]::GetFullPath((Join-Path $webRoot 'dist'))
    if (-not $distPath.StartsWith($webRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an output directory outside the web project: $distPath"
    }
    if (Test-Path -LiteralPath $distPath) {
        Remove-Item -LiteralPath $distPath -Recurse -Force
    }
    npm.cmd run build
    Assert-LastExitCode 'web build'
    npm.cmd audit --audit-level=low
    Assert-LastExitCode 'web audit'
}
finally {
    Pop-Location
}

node .\scripts\check-npm-licenses.mjs .\web\node_modules .\tools\git-worker\node_modules
Assert-LastExitCode 'npm license policy check'
