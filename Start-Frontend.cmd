@echo off
setlocal
set "CAMPUS_SCRIPT=Start-Frontend.ps1"
call "%~dp0scripts\Run-PowerShell.cmd" %*
exit /b %errorlevel%
