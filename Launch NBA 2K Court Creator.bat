@echo off
setlocal
set "APP_ROOT=%~dp0."
cd /d "%APP_ROOT%"
title NBA 2K Court Creator Launcher

set "BUNDLED_PY=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe"
set "BUNDLED_NODE_BIN=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin"
set "PNPM_CMD=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\bin\fallback\pnpm.cmd"
set "ELECTRON_EXE=%APP_ROOT%\node_modules\electron\dist\electron.exe"
set "ELECTRON_CMD=%APP_ROOT%\node_modules\.bin\electron.cmd"
set "APP_PROJECT=%~dp0src\NBA2KCourtCreator\NBA2KCourtCreator.csproj"
set "APP_EXE=%~dp0src\NBA2KCourtCreator\bin\Release\net8.0-windows\NBA2KCourtCreator.exe"

rem Close only an existing Electron instance launched from this exact project folder.
powershell.exe -NoProfile -WindowStyle Hidden -Command "$appRoot=[IO.Path]::GetFullPath('%APP_ROOT%'); Get-CimInstance Win32_Process -Filter 'Name = ''electron.exe''' | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($appRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0 } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>nul

if exist "%BUNDLED_NODE_BIN%\node.exe" (
    set "PATH=%BUNDLED_NODE_BIN%;%PATH%"
)

if exist "%BUNDLED_PY%" (
    "%BUNDLED_PY%" "%APP_ROOT%\updater.py" >nul 2>nul
) else (
    python "%APP_ROOT%\updater.py" >nul 2>nul
)

if exist "%APP_ROOT%\package.json" (
    if not exist "%ELECTRON_EXE%" (
        if exist "%PNPM_CMD%" (
            call "%PNPM_CMD%" -C "%APP_ROOT%" install
        )
    )
    if exist "%ELECTRON_EXE%" (
        start "" "%ELECTRON_EXE%" "%APP_ROOT%"
        exit /b 0
    )
    if exist "%ELECTRON_CMD%" (
        start "" "%ComSpec%" /d "%APP_ROOT%" /c ""%ELECTRON_CMD%" "%APP_ROOT%""
        exit /b 0
    )
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
