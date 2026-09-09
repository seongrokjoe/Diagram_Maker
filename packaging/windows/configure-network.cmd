@echo off
setlocal
set "POLICY_DIR=%LOCALAPPDATA%\DiagramMaker"
set "POLICY_PATH=%POLICY_DIR%\network-policy.json"
if not exist "%POLICY_DIR%" mkdir "%POLICY_DIR%"
if not exist "%POLICY_PATH%" copy "%~dp0config\network-policy.example.json" "%POLICY_PATH%" >nul
echo Ask the network administrator to approve exact LLM origins, IP ranges and local roots.
echo Empty lists deny access. Protect this file with the approved local ACL.
echo This is optional. Normal start.cmd does not load this file automatically.
echo Use start-with-network-policy.cmd after configuring it to enable the allowlist.
start "Diagram Maker Network Policy" notepad.exe "%POLICY_PATH%"
