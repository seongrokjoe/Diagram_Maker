@echo off
setlocal
call "%~dp0health-check.cmd"
if errorlevel 1 exit /b 1
if not exist "%~dp0diagnostics" mkdir "%~dp0diagnostics"
if errorlevel 1 exit /b 1
set "REPORT=%~dp0diagnostics\code-diagram-%RANDOM%-%RANDOM%.txt"
echo Testing fixed synthetic C#/C++ code with Thinking OFF. This can take several minutes.
curl.exe --fail-with-body --silent --show-error --max-time 930 -X POST "http://127.0.0.1:5080/api/v1/llm/tests/code-diagram-contract?format=text" --output "%REPORT%"
set "TEST_RESULT=%ERRORLEVEL%"
echo Report: "%REPORT%"
if not "%TEST_RESULT%"=="0" echo [ERROR] Code diagram test incomplete. Check the report and server settings.
exit /b %TEST_RESULT%
