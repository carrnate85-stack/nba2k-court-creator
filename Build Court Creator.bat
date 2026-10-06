@echo off
setlocal
cd /d "%~dp0"
dotnet publish src\NBA2KCourtCreator\NBA2KCourtCreator.csproj -c Release --self-contained false -o desktop --nologo
if errorlevel 1 (
    echo Build failed. Court Creator requires the .NET 8 SDK and the shared Canvas 0.3.1 package feed.
    echo See SHARED-ARTWORK.md for CanvasToolkitFeed setup.
    pause
    exit /b 1
)
echo Native build ready. Use your desktop Court Creator launcher.
exit /b 0
