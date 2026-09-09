@echo off
setlocal
cd /d "%~dp0"
set "COURT_ELECTRON=%~dp0node_modules\electron\dist\electron.exe"
if not exist "%COURT_ELECTRON%" (
    echo Court Creator needs its Electron dependencies installed. Run pnpm install in this folder.
    pause
    exit /b 1
)
rem Focus existing work; only apply updates while the app is closed.
powershell.exe -NoProfile -Command "$p=Get-Process electron -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $env:COURT_ELECTRON }; if ($p) { exit 0 } else { exit 1 }"
if not errorlevel 1 goto launch
if exist "%~dp0runtime\python\Scripts\python.exe" (
    "%~dp0runtime\python\Scripts\python.exe" "%~dp0updater.py" --apply
    goto launch
)
if exist "%~dp0runtime\python\python.exe" "%~dp0runtime\python\python.exe" "%~dp0updater.py" --apply
:launch
start "" "%COURT_ELECTRON%" "%~dp0."
exit /b 0
