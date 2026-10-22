@echo off
rem ============================================================
rem  AppForge - create desktop shortcut (double click this file)
rem  Pass -StartMenu to also add a Start Menu entry
rem ============================================================
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\create-shortcut.ps1" %*
set "RC=%ERRORLEVEL%"

if not "%RC%"=="0" (
    echo.
    echo [!] Failed with exit code %RC%
    echo     Press any key to close...
    pause >nul
) else (
    echo.
    echo Press any key to close...
    pause >nul
)

endlocal & exit /b %RC%
