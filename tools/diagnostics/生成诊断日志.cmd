@echo off
chcp 65001 >nul
setlocal
set "PS1=%~dp0collect_diagnostics.ps1"
if not exist "%PS1%" (
  echo 找不到 collect_diagnostics.ps1，请把两个文件放在同一个文件夹里。
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
if errorlevel 1 pause
endlocal
