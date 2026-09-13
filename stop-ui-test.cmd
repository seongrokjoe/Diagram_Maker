@echo off
setlocal
title Diagram Maker - Stop Local UI Test
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\ui-test.ps1" -Action Stop
set "UI_EXIT=%ERRORLEVEL%"
if not "%UI_EXIT%"=="0" pause
exit /b %UI_EXIT%
