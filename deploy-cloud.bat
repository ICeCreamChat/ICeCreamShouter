@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\deploy-cloud.ps1"
if errorlevel 1 (
  echo.
  echo Deployment did not finish. Read the error above.
  pause
  exit /b 1
)
pause
