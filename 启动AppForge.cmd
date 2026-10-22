@echo off
rem ============================================================
rem  AppForge - one click launcher (double click this file)
rem  Builds only when needed, then starts AppForge.exe
rem  Optional args are passed to scripts\launch.ps1
rem    examples:  AppForge-start.cmd -NoBuild
rem               AppForge-start.cmd -DataDir D:\afdata
rem ============================================================
chcp 65001 >nul
setlocal
cd /d "%~dp0"

set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"

"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\launch.ps1" %*
set "RC=%ERRORLEVEL%"

if not "%RC%"=="0" (
    echo.
    echo [!] Launch failed with exit code %RC%
    echo     See messages above. Press any key to close...
    pause >nul
)

endlocal & exit /b %RC%
