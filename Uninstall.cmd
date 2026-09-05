@echo off
REM ============================================================
REM  Uninstall.cmd - remove Claude Session Backup.
REM
REM  Unregisters the backup task FIRST (before deleting the
REM  executable it points at), then removes the shortcuts and
REM  the application folder.
REM
REM  KEPT AFTER UNINSTALL:
REM      Your backup destination and everything in it.
REM      Your settings (%APPDATA%\ClaudeSessionBackup).
REM
REM  Options:
REM      -WhatIf    show what would be removed without removing it
REM ============================================================
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1" %*
set RC=%ERRORLEVEL%

echo.
if not "%RC%"=="0" (
    echo Uninstall reported exit code %RC%.
)
pause
exit /b %RC%
