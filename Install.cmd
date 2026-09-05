@echo off
REM ============================================================
REM  Install.cmd - install Claude Session Backup.
REM
REM  Installs for you only, under
REM    %LOCALAPPDATA%\Programs\ClaudeSessionBackup
REM  and adds a Start Menu shortcut.
REM
REM  No admin rights needed or wanted: your Claude stores live
REM  in your own profile, and so does the backup destination.
REM
REM  Packaged build (zip):
REM      An app\ folder beside this file is copied directly.
REM      No .NET SDK is required.
REM
REM  From the repo:
REM      If no app\ folder is present and the .NET SDK is on
REM      PATH, the tool is built from source instead.
REM
REM  Options (pass through to Install.ps1):
REM      -RegisterTask        register the daily backup task
REM      -AddDesktopShortcut  add a desktop shortcut
REM ============================================================
cd /d "%~dp0"

echo Unblocking files...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -Path '%~dp0' -Include *.ps1,*.cmd -Recurse -ErrorAction SilentlyContinue | Unblock-File" 2>nul

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
set RC=%ERRORLEVEL%

echo.
if not "%RC%"=="0" (
    echo Install failed with exit code %RC%.
)
pause
exit /b %RC%
