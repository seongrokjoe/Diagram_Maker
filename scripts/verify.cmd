@echo off
setlocal
cd /d "%~dp0.."

dotnet restore .\DiagramMaker.sln --locked-mode --ignore-failed-sources
if errorlevel 1 exit /b 1
dotnet test .\DiagramMaker.sln -c Release --no-restore
if errorlevel 1 exit /b 1

call npm.cmd ci --prefix .\tools\git-worker || exit /b 1
call npm.cmd test --prefix .\tools\git-worker || exit /b 1
call npm.cmd audit --prefix .\tools\git-worker --audit-level=low || exit /b 1

call npm.cmd ci --prefix .\web || exit /b 1
call npm.cmd run build --prefix .\web -- --outDir ..\artifacts\verify-web-dist --emptyOutDir || exit /b 1
call npm.cmd audit --prefix .\web --audit-level=low || exit /b 1

node .\scripts\check-npm-licenses.mjs .\web\node_modules .\tools\git-worker\node_modules
if errorlevel 1 exit /b 1

echo Diagram Maker verification passed.
exit /b 0
