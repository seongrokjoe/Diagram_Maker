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
    npm.cmd run build -- --outDir ..\artifacts\verify-web-dist --emptyOutDir
    Assert-LastExitCode 'web build'
    npm.cmd audit --audit-level=low
    Assert-LastExitCode 'web audit'
}
finally {
    Pop-Location
}

node .\scripts\check-npm-licenses.mjs .\web\node_modules .\tools\git-worker\node_modules
Assert-LastExitCode 'npm license policy check'
