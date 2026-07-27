@echo off
setlocal
cd /d "%~dp0"
title NBA 2K Court Creator Launcher

set "BUNDLED_PY=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"

if exist "%BUNDLED_PY%" (
    "%BUNDLED_PY%" "%~dp0updater.py" >nul 2>nul
    "%BUNDLED_PY%" "%~dp0main.py"
    exit /b %ERRORLEVEL%
)

python -c "import sys" >nul 2>nul
if not errorlevel 1 (
    python "%~dp0updater.py" >nul 2>nul
    python "%~dp0main.py"
    exit /b %ERRORLEVEL%
)

py -3 -c "import sys" >nul 2>nul
if not errorlevel 1 (
    py -3 "%~dp0updater.py" >nul 2>nul
    py -3 "%~dp0main.py"
    exit /b %ERRORLEVEL%
)

echo Python 3 was not found.
echo Install Python 3 or open this project through Codex again so the bundled runtime is available.
pause
