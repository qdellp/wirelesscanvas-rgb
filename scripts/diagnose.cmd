@echo off
rem Read-only USB diagnostics: writes diagnostics.json next to the app and opens it.
cd /d "%~dp0"
start "" /wait "WirelessCanvasRGB.exe" --diagnose "%~dp0diagnostics.json"
if errorlevel 1 exit /b 1
start "" notepad.exe "%~dp0diagnostics.json"
