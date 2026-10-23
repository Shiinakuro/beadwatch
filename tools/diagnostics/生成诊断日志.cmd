@echo off
chcp 65001 >nul
setlocal
set "PS1=%~dp0collect_diagnostics.ps1"
if not exist "%PS1%" (
  echo 找不到 collect_diagnostics.ps1。
  echo 请先把 zip 完整解压到一个文件夹，再双击本文件；不要直接在压缩包里运行。
  pause
  exit /b 1
)
echo 正在收集诊断信息，请稍候...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
echo.
echo 如果上方显示了 zip 路径，请把该 zip 发给开发者。
echo 如果没有生成 zip，请把窗口里的提示或运行日志发给开发者。
pause
endlocal
