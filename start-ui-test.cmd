@echo off
setlocal
title Diagram Maker - Local UI Test
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\ui-test.ps1" -Action Start %*
set "UI_EXIT=%ERRORLEVEL%"
if not "%UI_EXIT%"=="0" (
  echo.
  echo [ERROR] See the message above and UI_TEST_KO.md.
  pause
)
exit /b %UI_EXIT%
