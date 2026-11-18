@echo off
rem Installs the AI models of Boulder Video Editor (S1, D066 §3): a double click runs Install-Models.ps1 from this folder.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Models.ps1"
echo.
pause
