@echo off
setlocal
set "SCRIPT=%~dp0windows\palera1n.ps1"
if not exist "%SCRIPT%" (
  echo [Palera1nWin] Missing packaged launcher: %SCRIPT% 1>&2
  exit /b 90
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -- %*
exit /b %ERRORLEVEL%
