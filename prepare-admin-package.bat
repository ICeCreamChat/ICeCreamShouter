@echo off
setlocal
cd /d "%~dp0"

echo This will build the classroom EXE and create the delivery package.
set "CRS_NO_PAUSE=1"
call "%~dp0build.bat"
if errorlevel 1 (
  echo.
  echo The EXE build failed. Read the error above.
  pause
  exit /b 1
)

call "%~dp0prepare-delivery.bat"
if errorlevel 1 (
  echo.
  echo The delivery package was not generated. Read the error above.
  pause
  exit /b 1
)

echo.
echo All administrator delivery files are ready in:
echo   %~dp0dist\delivery
pause
