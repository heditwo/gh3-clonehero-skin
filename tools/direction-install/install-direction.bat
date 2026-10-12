@echo off
rem Copies the stage-events.json files of this GH3 direction archive into your songs folder. Usage: install-direction.bat ["C:\path\to\Songs"]
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-direction.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-direction.ps1" -SongsDir "%~1"
)
set result=%errorlevel%
pause
exit /b %result%
