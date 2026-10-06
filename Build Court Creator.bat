@echo off
setlocal
cd /d "%~dp0"
set "COURT_PYTHON=%~dp0runtime\python\python.exe"
if not exist "%COURT_PYTHON%" set "COURT_PYTHON=%~dp0runtime\python\Scripts\python.exe"
if not exist "%COURT_PYTHON%" (
    echo The Python backend is missing. Run Setup Court Creator.bat first.
    pause
    exit /b 1
)
"%COURT_PYTHON%" -B tools\sync_canvas_toolkit.py --build
if errorlevel 1 (
    echo Build failed. The previous desktop build has been retained.
    echo Court Creator requires the .NET 8 SDK and matching published Canvas packages.
    echo See SHARED-ARTWORK.md for CanvasToolkitFeed setup.
    pause
    exit /b 1
)
echo Native build ready. Use your desktop Court Creator launcher.
exit /b 0
