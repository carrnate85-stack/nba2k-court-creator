@echo off
setlocal
cd /d "%~dp0"
set "COURT_STUDIO=%~dp0desktop\NBA2KCourtCreator.exe"
if exist "%COURT_STUDIO%" goto ready
set "COURT_STUDIO=%~dp0src\NBA2KCourtCreator\bin\Release\net8.0-windows\NBA2KCourtCreator.exe"
if exist "%COURT_STUDIO%" goto ready
echo The native Court Creator build is missing. Run Build Court Creator.bat first.
pause
exit /b 1
:ready
set "COURT_PYTHON=%~dp0runtime\python\python.exe"
if not exist "%COURT_PYTHON%" set "COURT_PYTHON=%~dp0runtime\python\Scripts\python.exe"
if not exist "%COURT_PYTHON%" (
    echo The Python backend is missing. Run Setup Court Creator.bat first.
    pause
    exit /b 1
)
rem Parse the entire tail before updating this running batch file.
(
    rem Never replace a binary while an existing native instance is using it.
    powershell.exe -NoProfile -Command "$p=Get-Process NBA2KCourtCreator -ErrorAction SilentlyContinue; if ($p) { exit 0 } else { exit 1 }"
    if errorlevel 1 (
        "%COURT_PYTHON%" -B "%~dp0updater.py" --apply
        if errorlevel 3 (
            echo Update recovery needs attention. Preserve the updates folder and review updates\last-error.txt.
            pause
            exit /b 1
        )
        if errorlevel 2 (
            echo Another update is still running. Please try the launcher again shortly.
            pause
            exit /b 1
        )
    )
    start "" "%COURT_STUDIO%"
    rem Stage compatible standalone updates in the background, without opening a console.
    powershell.exe -NoProfile -Command "Start-Process -FilePath $env:COURT_PYTHON -ArgumentList @('-B', ('{0}{1}{0}' -f [char]34, (Join-Path (Get-Location) 'updater.py'))) -WindowStyle Hidden"
    exit /b 0
)
