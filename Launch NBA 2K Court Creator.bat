@echo off
setlocal
cd /d "%~dp0"
title NBA 2K Court Creator Launcher

set "BUNDLED_PY=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
set "APP_PROJECT=%~dp0src\NBA2KCourtCreator\NBA2KCourtCreator.csproj"
set "APP_EXE=%~dp0src\NBA2KCourtCreator\bin\Release\net8.0-windows\NBA2KCourtCreator.exe"

if exist "%BUNDLED_PY%" (
    "%BUNDLED_PY%" "%~dp0updater.py" >nul 2>nul
) else (
    python "%~dp0updater.py" >nul 2>nul
)

if exist "%APP_EXE%" (
    start "" "%APP_EXE%"
    exit /b 0
)

dotnet --version >nul 2>nul
if not errorlevel 1 (
    dotnet run --project "%APP_PROJECT%" --configuration Release
    exit /b %ERRORLEVEL%
)

if exist "%BUNDLED_PY%" (
    "%BUNDLED_PY%" "%~dp0main.py"
    exit /b %ERRORLEVEL%
)

python -c "import sys" >nul 2>nul
if not errorlevel 1 (
    python "%~dp0main.py"
    exit /b %ERRORLEVEL%
)

py -3 -c "import sys" >nul 2>nul
if not errorlevel 1 (
    py -3 "%~dp0main.py"
    exit /b %ERRORLEVEL%
)

echo .NET 8 or Python 3 was not found.
pause
