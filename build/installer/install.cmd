@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "LOGROOT=%TEMP%"
if not defined LOGROOT set "LOGROOT=%USERPROFILE%"
if not defined LOGROOT set "LOGROOT=%~dp0"
set "BOOTSTRAP_LOG=%LOGROOT%\MyCapture-Setup-bootstrap.log"
set "POWERSHELL_EXE=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "POWERSHELL_EXE=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%POWERSHELL_EXE%" (
  >>"%BOOTSTRAP_LOG%" echo [%date% %time%] Windows PowerShell was not found. Use the portable ZIP.
  exit /b 19
)
set "QUIET_SWITCH="
if /I "%~1"=="/quiet" set "QUIET_SWITCH=-Quiet"
>>"%BOOTSTRAP_LOG%" echo [%date% %time%] Starting MyCapture offline installer from "%~dp0".
"%POWERSHELL_EXE%" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0install.ps1" %QUIET_SWITCH% >>"%BOOTSTRAP_LOG%" 2>&1
set "RESULT=%ERRORLEVEL%"
>>"%BOOTSTRAP_LOG%" echo [%date% %time%] Installer exit code %RESULT%.
exit /b %RESULT%
