@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0.."
if errorlevel 1 exit /b 1

set "configuration=Debug"
set "run_tests="
set "no_restore="
set "doctor_only="
:parse
if "%~1"=="" goto configured
if /i "%~1"=="-Test" (
    set "run_tests=1"
    shift
    goto parse
)
if /i "%~1"=="-NoRestore" (
    set "no_restore=--no-restore"
    shift
    goto parse
)
if /i "%~1"=="-Doctor" (
    set "doctor_only=1"
    shift
    goto parse
)
if /i "%~1"=="-Configuration" (
    if /i not "%~2"=="Debug" if /i not "%~2"=="Release" goto usage_error
    set "configuration=%~2"
    shift
    shift
    goto parse
)
if /i "%~1"=="-Help" goto usage
goto usage_error

:configured
if not exist "global.json" (
    echo Missing global.json in the repository root.
    exit /b 1
)
if not exist "build\logs" mkdir "build\logs"
if not exist "build\logs" exit /b 1
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_CLI_UI_LANGUAGE=en"
set "sdk_exe="
if defined LOCALAPPDATA call :try_sdk "%LOCALAPPDATA%\MyCapture\dotnet-sdk\dotnet.exe"
if defined LOCALAPPDATA call :try_sdk "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
for /f "delims=" %%D in ('where dotnet.exe 2^>nul') do call :try_sdk "%%D"
if not defined sdk_exe (
    echo No SDK compatible with global.json was found.
    echo Install the repository SDK in %%LOCALAPPDATA%%\MyCapture\dotnet-sdk or put it on PATH.
    if exist "build\logs\sdk-cmd.log" type "build\logs\sdk-cmd.log"
    exit /b 1
)
for %%D in ("%sdk_exe%") do set "DOTNET_ROOT=%%~dpD"
set "PATH=%DOTNET_ROOT%;%PATH%"
echo SDK: %sdk_exe%
type "build\logs\sdk-cmd.log"
if defined doctor_only exit /b 0

echo Building %configuration%...
"%sdk_exe%" build "%CD%\MyCapture.slnx" -c "%configuration%" %no_restore% > "build\logs\build.log" 2>&1
set "step_exit=%errorlevel%"
if not "%step_exit%"=="0" (
    type "build\logs\build.log"
    exit /b %step_exit%
)
if not defined run_tests goto done
echo Running all %configuration% tests...
"%sdk_exe%" test "%CD%\MyCapture.slnx" -c "%configuration%" --no-build --verbosity normal > "build\logs\test.log" 2>&1
set "step_exit=%errorlevel%"
if not "%step_exit%"=="0" (
    type "build\logs\test.log"
    exit /b %step_exit%
)
findstr /c:"Passed!" /c:"Total tests" "build\logs\test.log"
:done
echo Build succeeded (%configuration%). Logs: %CD%\build\logs
exit /b 0

:try_sdk
if defined sdk_exe exit /b 0
if not exist "%~1" exit /b 0
rem The SDK resolver enforces global.json, including its stable version and roll-forward policy.
"%~1" --version > "build\logs\sdk-cmd.log" 2>&1
if not errorlevel 1 set "sdk_exe=%~1"
exit /b 0

:usage_error
echo Invalid build argument.
call :usage
exit /b 2
:usage
echo Usage: build\build.cmd [-Configuration Debug^|Release] [-Test] [-NoRestore] [-Doctor]
exit /b 0
