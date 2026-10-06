@echo off
setlocal
cd /d "%~dp0"
set "COURT_SETUP_PYTHON=%~dp0runtime\python\python.exe"
if exist "%COURT_SETUP_PYTHON%" goto existing
set "COURT_SETUP_PYTHON=%~dp0runtime\python\Scripts\python.exe"
if exist "%COURT_SETUP_PYTHON%" goto existing
where py >nul 2>nul
if not errorlevel 1 (
    py -3 -B "%~dp0tools\setup_court_creator.py" --project-root "%~dp0." %*
    goto result
)
where python >nul 2>nul
if errorlevel 1 goto missingpython
python -B "%~dp0tools\setup_court_creator.py" --project-root "%~dp0." %*
goto result
:existing
"%COURT_SETUP_PYTHON%" -B "%~dp0tools\setup_court_creator.py" --project-root "%~dp0." %*
:result
if errorlevel 1 goto failed
if "%~1"=="" (
    echo Setup complete. The app has not been opened.
    pause
)
exit /b 0
:missingpython
echo No Python interpreter was found. Install 64-bit Python 3.12 or newer.
:failed
if "%~1"=="" (
    echo Setup did not complete. Existing runtime files have not been replaced by a new environment.
    pause
)
exit /b 1
