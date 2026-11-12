@echo off
dotnet run --file "%~dp0size-report.cs" --artifacts-path "%TEMP%\mycapture-size-report" --disable-build-servers -v q -- %*
exit /b %errorlevel%
