@echo off
setlocal
set "CAMPUS_SCRIPT=Collect-Evidence.ps1"
call "%~dp0scripts\Run-PowerShell.cmd" %*
exit /b %errorlevel%
