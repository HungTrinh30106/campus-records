@echo off
set "CAMPUS_SCRIPT=Setup-Operations.ps1"
call "%~dp0scripts\Run-PowerShell.cmd" %*
exit /b %errorlevel%
