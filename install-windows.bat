@echo off
rem Installs MelonLoader 0.7.3 and the GH3 gameplay skin. Usage: install-windows.bat ["C:\path\to\Clone Hero"]
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-windows.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-windows.ps1" -GameDir "%~1"
)
set result=%errorlevel%
pause
exit /b %result%
