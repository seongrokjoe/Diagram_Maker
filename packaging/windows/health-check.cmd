@echo off
setlocal
curl.exe --fail --silent --show-error http://127.0.0.1:5080/health
if errorlevel 1 (
  echo.
  echo [ERROR] Diagram Maker health check failed.
  exit /b 1
)
echo.
exit /b 0
