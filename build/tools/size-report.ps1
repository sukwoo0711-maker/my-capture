# Optional PowerShell entry point. Under Restricted policy use size-report.cmd.
# Both launch the one .NET file-based implementation; neither changes execution policy.
& dotnet run --file (Join-Path $PSScriptRoot 'size-report.cs') `
    --artifacts-path (Join-Path $env:TEMP 'mycapture-size-report') --disable-build-servers -v q -- @args
exit $LASTEXITCODE
