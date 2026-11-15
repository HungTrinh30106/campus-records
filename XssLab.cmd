@echo off
setlocal
set "CAMPUS_SCRIPT=Run-XssLab.ps1"
call "%~dp0scripts\Run-PowerShell.cmd" %*
exit /b %errorlevel%
