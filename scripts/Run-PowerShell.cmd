@echo off
setlocal
if not defined CAMPUS_SCRIPT exit /b 1
if not exist "%~dp0%CAMPUS_SCRIPT%" exit /b 1
set "CAMPUS_PWSH="
if exist "%ProgramFiles%\PowerShell\7\pwsh.exe" set "CAMPUS_PWSH=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not defined CAMPUS_PWSH if exist "%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe" set "CAMPUS_PWSH=%USERPROFILE%\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe"
if not defined CAMPUS_PWSH for /f "delims=" %%P in ('where pwsh.exe 2^>nul') do if not defined CAMPUS_PWSH set "CAMPUS_PWSH=%%P"
if not defined CAMPUS_PWSH (
  echo PowerShell 7 was not found. Install PowerShell 7 and retry.
  exit /b 1
)
"%CAMPUS_PWSH%" -NoProfile -Command "if ($PSVersionTable.PSVersion.Major -lt 7) { exit 1 }"
if errorlevel 1 exit /b 1
if "%~1"=="--check" (
  "%CAMPUS_PWSH%" -NoProfile -Command "'PowerShell ' + $PSVersionTable.PSVersion.ToString()"
  echo Script: %CAMPUS_SCRIPT%
  exit /b 0
)
"%CAMPUS_PWSH%" -NoProfile -File "%~dp0%CAMPUS_SCRIPT%" %*
exit /b %errorlevel%
