@echo off
setlocal
set "POLICY_DIR=%LOCALAPPDATA%\DiagramMaker"
set "POLICY_PATH=%POLICY_DIR%\llm-policy.json"
set "EXAMPLE_PATH=%~dp0config\llm-policy.example.json"

if not exist "%EXAMPLE_PATH%" (
  echo [ERROR] LLM policy example was not found.
  exit /b 1
)
if not exist "%POLICY_DIR%" mkdir "%POLICY_DIR%"
if not exist "%POLICY_PATH%" copy "%EXAMPLE_PATH%" "%POLICY_PATH%" >nul

echo Edit Endpoint, AllowedOrigin, and Model using approved internal values.
echo No API key is required by the current internal vLLM contract.
start "Diagram Maker LLM Policy" notepad.exe "%POLICY_PATH%"
exit /b 0
