@echo off
setlocal EnableExtensions DisableDelayedExpansion
set "LOGROOT=%TEMP%"
if not defined LOGROOT set "LOGROOT=%USERPROFILE%"
if not defined LOGROOT set "LOGROOT=%~dp0"
for %%I in ("%LOGROOT%") do set "LOGROOT=%%~fI"
set "BOOTSTRAP_LOG=%LOGROOT%\MyCapture-Setup-bootstrap.log"
set "POWERSHELL_EXE=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "POWERSHELL_EXE=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%POWERSHELL_EXE%" (
  >>"%BOOTSTRAP_LOG%" echo [%date% %time%] Windows PowerShell was not found. Use the portable ZIP.
  exit /b 19
)
set "QUIET_SWITCH="
if /I "%~1"=="/quiet" set "QUIET_SWITCH=-Quiet"

:ChooseRun
set "RUN_ID=%RANDOM%-%RANDOM%-%RANDOM%"
set "MYCAPTURE_SETUP_STARTED=%LOGROOT%\MyCapture-Setup-%RUN_ID%.started"
set "RUN_LOG=%LOGROOT%\MyCapture-Setup-%RUN_ID%.log"
if exist "%MYCAPTURE_SETUP_STARTED%" goto ChooseRun
if exist "%RUN_LOG%" goto ChooseRun
>>"%BOOTSTRAP_LOG%" echo [%date% %time%] Starting MyCapture offline installer from "%~dp0".
rem A separate log makes policy evidence specific to this invocation, even across appended runs.
call :RunInstaller >"%RUN_LOG%" 2>&1
set "RESULT=%ERRORLEVEL%"
set "POLICY_EVIDENCE=not found"
"%SystemRoot%\System32\findstr.exe" /I /L /C:"PSSecurityException" /C:"about_Execution_Policies" /C:"execution policy" /C:"running scripts is disabled" "%RUN_LOG%" >nul 2>&1
if not errorlevel 1 set "POLICY_EVIDENCE=found"
type "%RUN_LOG%" >>"%BOOTSTRAP_LOG%"
rem The run output now lives in the bootstrap log; do not leave one file per run in TEMP.
del /q "%RUN_LOG%" >nul 2>&1
>>"%BOOTSTRAP_LOG%" echo [%date% %time%] Installer exit code %RESULT%.
if not exist "%MYCAPTURE_SETUP_STARTED%" goto NotStarted
del /q "%MYCAPTURE_SETUP_STARTED%"
exit /b %RESULT%

:NotStarted
rem 22 is reserved for script-start failure; 20 already means an existing-install error.
>>"%BOOTSTRAP_LOG%" echo [%date% %time%] Installer script did not start; bootstrap exit code 22. PowerShell execution-policy evidence in this run: %POLICY_EVIDENCE%. PowerShell exit code: %RESULT%.
set "SETUP_HELP=%LOGROOT%\MyCapture-Setup-Help.txt"
copy /y "%~dp0SETUP-HELP.txt" "%SETUP_HELP%" >nul 2>&1
if errorlevel 1 (
  >>"%BOOTSTRAP_LOG%" echo [%date% %time%] Could not copy setup guidance to "%SETUP_HELP%".
  exit /b 22
)
>>"%BOOTSTRAP_LOG%" echo [%date% %time%] Setup guidance: "%SETUP_HELP%".
if not defined QUIET_SWITCH start "" "%SystemRoot%\System32\notepad.exe" "%SETUP_HELP%"
exit /b 22

:RunInstaller
rem Keep the launch arguments unchanged.
"%POWERSHELL_EXE%" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0install.ps1" %QUIET_SWITCH%
exit /b %ERRORLEVEL%
