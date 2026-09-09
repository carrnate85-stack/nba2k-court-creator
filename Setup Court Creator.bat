@echo off
setlocal
cd /d "%~dp0"
py -3 -m venv runtime\python
if errorlevel 1 goto failed
runtime\python\Scripts\python.exe -m pip install -r requirements.txt
if errorlevel 1 goto failed
call npm install
if errorlevel 1 goto failed
echo Setup complete. Open Launch NBA 2K Court Creator.bat.
pause
exit /b 0
:failed
echo Setup failed. Install Python 3.12 and Node.js, then run setup again.
pause
exit /b 1
