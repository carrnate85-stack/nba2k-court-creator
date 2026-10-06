@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0node_modules\electron\dist\electron.exe" (
    echo The optional Electron fallback needs npm install.
    pause
    exit /b 1
)
start "" "%~dp0node_modules\electron\dist\electron.exe" "%~dp0."
exit /b 0
