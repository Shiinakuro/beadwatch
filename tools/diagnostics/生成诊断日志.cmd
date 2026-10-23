@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
set "PS1=%~dp0collect_diagnostics.ps1"
if not exist "%PS1%" (
  echo 找不到 collect_diagnostics.ps1。
  echo 请先把 zip 完整解压到一个文件夹，再双击本文件；不要直接在压缩包里运行。
  pause
  exit /b 1
)
echo 正在启动诊断脚本，请稍候...
powershell.exe -NoExit -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
echo.
echo 诊断脚本已结束。按任意键关闭此窗口。
pause
endlocal
