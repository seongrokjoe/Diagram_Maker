@echo off
setlocal
call "%~dp0health-check.cmd"
if errorlevel 1 exit /b 1
if not exist "%~dp0diagnostics" mkdir "%~dp0diagnostics"
if errorlevel 1 exit /b 1
set "REPORT=%~dp0diagnostics\natural-diagram-%RANDOM%-%RANDOM%.txt"
echo Testing fixed synthetic natural requirements and four diagram formats with Thinking OFF.
echo The test uses the real generation pipeline and can take up to 15 minutes.
curl.exe --fail-with-body --silent --show-error --max-time 930 -X POST "http://127.0.0.1:5080/api/v1/llm/tests/natural-diagram-contract?format=text" --output "%REPORT%"
set "TEST_RESULT=%ERRORLEVEL%"
echo Report: "%REPORT%"
if not "%TEST_RESULT%"=="0" echo [ERROR] Natural diagram test incomplete. Check the report before retrying.
exit /b %TEST_RESULT%
