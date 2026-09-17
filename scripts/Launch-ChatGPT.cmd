@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launch-ChatGPT.ps1"
if errorlevel 1 (
  echo.
  echo Launch failed. This window will stay open so you can read the error.
  pause
)
endlocal
