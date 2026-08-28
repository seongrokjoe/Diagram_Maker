@echo off
setlocal
set "BASE_URL=http://127.0.0.1:5080/api/v1/llm/tests"

call "%~dp0health-check.cmd"
if errorlevel 1 exit /b 1

echo [1/3] Basic connection test
curl.exe --fail-with-body --silent --show-error -X POST "%BASE_URL%/connection"
if errorlevel 1 goto :failed
echo.

echo [2/3] DiagramIR structured contract test
curl.exe --fail-with-body --silent --show-error -X POST "%BASE_URL%/diagram-contract"
if errorlevel 1 goto :failed
echo.

echo [3/3] Thinking structured protocol test
curl.exe --fail-with-body --silent --show-error -X POST "%BASE_URL%/thinking-contract"
if errorlevel 1 goto :failed
echo.
echo All internal LLM tests passed.
exit /b 0

:failed
echo.
echo [ERROR] An internal LLM test failed. No project data was sent by these tests.
exit /b 1
