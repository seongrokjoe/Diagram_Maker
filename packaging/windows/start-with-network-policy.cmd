@echo off
setlocal
if not defined DIAGRAMMAKER_NETWORK_POLICY_PATH set "DIAGRAMMAKER_NETWORK_POLICY_PATH=%LOCALAPPDATA%\DiagramMaker\network-policy.json"
if not exist "%DIAGRAMMAKER_NETWORK_POLICY_PATH%" (
  echo [ERROR] Optional network policy was not found. Run configure-network.cmd first.
  exit /b 1
)
call "%~dp0start.cmd"
exit /b %ERRORLEVEL%
