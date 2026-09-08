@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-codex-test.ps1" %*
if errorlevel 1 pause
