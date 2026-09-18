@echo off
setlocal
cd /d "%~dp0"
title ICeCream Shouter Tencent Cloud Deployment

echo ============================================================
echo   ICeCream Shouter - Tencent Cloud one-click deployment
echo ============================================================
echo.
echo Before continuing, the server must be Ubuntu Server 24.04 LTS.
echo Tencent Cloud firewall must allow TCP ports 22, 80 and 443.
echo The default Ubuntu login username is ubuntu.
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$code=[IO.File]::ReadAllText('%~dp0scripts\deploy-tencent-server.ps1',[Text.Encoding]::UTF8); & ([ScriptBlock]::Create($code))"
if errorlevel 1 (
  echo.
  echo Deployment did not finish. Read the last error above.
  pause
  exit /b 1
)
echo.
pause
