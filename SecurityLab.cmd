@echo off
setlocal
set "CAMPUS_SCRIPT=Run-SecurityLab.ps1"
call "%~dp0scripts\Run-PowerShell.cmd" %*
exit /b %errorlevel%
