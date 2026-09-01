@echo off
setlocal
set "APP_ROOT=%~dp0"
set "POLICY_PATH=%LOCALAPPDATA%\DiagramMaker\llm-policy.json"

if not exist "%APP_ROOT%DiagramMaker.Api.exe" (
  echo [ERROR] DiagramMaker.Api.exe was not found.
  exit /b 1
)
if not exist "%APP_ROOT%runtime\node\node.exe" (
  echo [ERROR] The packaged Node.js runtime was not found.
  exit /b 1
)
if not exist "%POLICY_PATH%" (
  echo [ERROR] The local LLM policy does not exist: %POLICY_PATH%
  echo Run configure-llm.cmd first.
  exit /b 1
)
if not exist "%APP_ROOT%data" mkdir "%APP_ROOT%data"

set "DIAGRAMMAKER_LLM_POLICY_PATH=%POLICY_PATH%"
set "ASPNETCORE_ENVIRONMENT=Development"
set "ASPNETCORE_URLS=http://127.0.0.1:5080"
set "Llm__AllowDevelopmentStub=false"
set "Storage__Provider=LocalFile"
set "Storage__LocalFilePath=%APP_ROOT%data\repositories.json"
set "GitWorker__NodeExecutable=%APP_ROOT%runtime\node\node.exe"
set "GitWorker__ScriptPath=%APP_ROOT%tools\git-worker\index.mjs"
set "GitWorker__Backend=Auto"
set "GitWorker__GitExecutable=git"
set "DOTNET_EnableDiagnostics=0"

echo Diagram Maker will listen only on http://127.0.0.1:5080
echo Press Ctrl+C to stop.
pushd "%APP_ROOT%"
"%APP_ROOT%DiagramMaker.Api.exe"
set "APP_EXIT_CODE=%ERRORLEVEL%"
popd
exit /b %APP_EXIT_CODE%
