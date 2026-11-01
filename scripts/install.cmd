@echo off
rem Double-click to install. Runs install.ps1 regardless of PowerShell execution policy.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
pause
