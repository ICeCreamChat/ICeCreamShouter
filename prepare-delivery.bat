@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\prepare-delivery.ps1"
if errorlevel 1 (
  echo.
  echo Delivery package was not generated. Read the error above.
  if /i not "%CRS_NO_PAUSE%"=="1" pause
  exit /b 1
)
echo.
if /i not "%CRS_NO_PAUSE%"=="1" pause
