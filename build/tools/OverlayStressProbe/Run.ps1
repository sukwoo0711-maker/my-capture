param(
    [Parameter(Mandatory=$true)][ValidateSet('native-normal','native-burst','native-many-windows','native-async-normal','native-async-burst','raster-4k','raster-8k')][string]$Case,
    [Parameter(Mandatory=$true)][string]$TargetDll,
    [string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$RuntimeVersion='10.0.11',
    [string]$Label='baseline',
    [int]$TimeoutSeconds=60
)
$ErrorActionPreference='Stop'
if($Label -notmatch '^[a-zA-Z0-9_-]+$'){throw 'Invalid label'}
if($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 120){throw 'Invalid timeout'}
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if(!$OutputDirectory){$OutputDirectory=Join-Path $repoRoot 'artifacts/overlay-stress'}
$taskRoot=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($taskRoot)|Out-Null
$probe=Join-Path $PSScriptRoot 'bin/Release/net10.0-windows10.0.22000.0/OverlayStress.dll'
if(!(Test-Path -LiteralPath $probe -PathType Leaf)){throw 'Build OverlayStress.csproj in Release before running the probe'}
$target=[IO.Path]::GetFullPath($TargetDll)
if(!(Test-Path -LiteralPath $target -PathType Leaf)){throw 'Missing product DLL'}
$tag=$Label+'-'+$Case+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$output=Join-Path $taskRoot ($tag+'.json')
$receipt=Join-Path $taskRoot ($tag+'.process.json')
$runtimePath=Join-Path $taskRoot ($tag+'.runtimeconfig.json')
$runtime=Get-Content -Raw -Encoding UTF8 -LiteralPath ([IO.Path]::ChangeExtension($probe,'.runtimeconfig.json')) | ConvertFrom-Json
foreach($framework in $runtime.runtimeOptions.frameworks){$framework.version=$RuntimeVersion}
$runtime.runtimeOptions.rollForward='Disable'
[IO.File]::WriteAllText($runtimePath,($runtime|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$info=[Diagnostics.ProcessStartInfo]::new()
$info.FileName=(Get-Command dotnet.exe -ErrorAction Stop).Source
$info.Arguments='exec --runtimeconfig "'+$runtimePath+'" "'+$probe+'" "'+$target+'" "'+$output+'" "'+$Label+'-'+$Case+'"'
$info.WorkingDirectory=$taskRoot
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.RedirectStandardOutput=$true
$info.RedirectStandardError=$true
foreach($key in @('MYCAPTURE_OVERLAY_NATIVE','MYCAPTURE_OVERLAY_ACTUAL','MYCAPTURE_OVERLAY_SNAPSHOT','MYCAPTURE_OVERLAY_DISABLE','STRESS_WIDTH','STRESS_CANDIDATES','STRESS_BURST','STRESS_ASYNC_INPUT')){$info.EnvironmentVariables.Remove($key)}
$info.EnvironmentVariables['MYCAPTURE_OVERLAY_BGR32']='1'
if($Case.StartsWith('native-')){
    $info.EnvironmentVariables['MYCAPTURE_OVERLAY_NATIVE']='1'
    $info.EnvironmentVariables['STRESS_BURST']=if($Case -in @('native-normal','native-async-normal')){'1'}else{'32'}
    if($Case.StartsWith('native-async-')){$info.EnvironmentVariables['STRESS_ASYNC_INPUT']='1'}
    $info.EnvironmentVariables['STRESS_CANDIDATES']=if($Case -eq 'native-many-windows'){'10000'}else{'3'}
}else{
    $info.EnvironmentVariables['MYCAPTURE_OVERLAY_ACTUAL']='1'
    $info.EnvironmentVariables['STRESS_WIDTH']=if($Case -eq 'raster-8k'){'7680'}else{'3840'}
}
$child=[Diagnostics.Process]::new()
$child.StartInfo=$info
$record=[ordered]@{case=$Case;label=$Label;pid=$null;cwd=$taskRoot;command=$info.FileName;probeSha256=(Get-FileHash -LiteralPath $probe -Algorithm SHA256).Hash;arguments=$info.Arguments;runtimeConfig=$runtimePath;runtimeConfigSha256=(Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash;target=$target;targetSha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash;startedUtc=[DateTimeOffset]::UtcNow.ToString('o');timeoutSeconds=$TimeoutSeconds;privateByteLimit=2147483648;exitCode=$null;stopReason=$null;peakPrivateBytes=0;peakWorkingSet=0;samples=@();stdout=$null;stderr=$null}
$clock=[Diagnostics.Stopwatch]::StartNew()
$samples=[Collections.Generic.List[object]]::new()
$started=$false
$outTask=$null
$errTask=$null
try{
    if(!$child.Start()){throw 'Child did not start'}
    $started=$true
    $record.pid=$child.Id
    $outTask=$child.StandardOutput.ReadToEndAsync()
    $errTask=$child.StandardError.ReadToEndAsync()
    [IO.File]::WriteAllText($receipt,($record|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    Write-Output "OWNED_PID=$($child.Id) CASE=$Case RECEIPT=$receipt"
    while(!$child.WaitForExit(200)){
        $child.Refresh()
        if($child.HasExited){break}
        $memory=$child.PrivateMemorySize64
        $working=$child.WorkingSet64
        $record.peakPrivateBytes=[Math]::Max($record.peakPrivateBytes,$memory)
        $record.peakWorkingSet=[Math]::Max($record.peakWorkingSet,$working)
        $samples.Add([pscustomobject]@{elapsedMs=$clock.ElapsedMilliseconds;privateBytes=$memory;workingSet=$working;cpuMs=$child.TotalProcessorTime.TotalMilliseconds})
        if($memory -gt 2147483648){$record.stopReason='OWNED_MEMORY_LIMIT'}
        elseif($clock.Elapsed.TotalSeconds -gt $TimeoutSeconds){$record.stopReason='OWNED_TIMEOUT'}
        if($record.stopReason){
            $child.Kill()
            if(!$child.WaitForExit(5000)){throw 'Owned process exit was not confirmed within 5 seconds'}
            break
        }
    }
    $record.exitCode=$child.ExitCode
    $record.stdout=$outTask.GetAwaiter().GetResult()
    $record.stderr=$errTask.GetAwaiter().GetResult()
}catch{
    $record.stopReason='HARNESS_EXCEPTION'
    $record.harnessError=$_.Exception.ToString()
    throw
}finally{
    # A receipt or process-observation failure must not orphan the owned overlay.
    # Keep the exact Process object; never look up or stop an application by name.
    try{
        if($started){
            if(!$child.HasExited){
                $child.Kill()
                if(!$child.WaitForExit(5000)){throw 'Owned process finalization was not confirmed within 5 seconds'}
            }
            $record.exitCode=$child.ExitCode
            if($null -ne $outTask){$record.stdout=$outTask.GetAwaiter().GetResult()}
            if($null -ne $errTask){$record.stderr=$errTask.GetAwaiter().GetResult()}
        }
    }catch{
        $record.cleanupError=$_.Exception.ToString()
        if(!$record.stopReason){$record.stopReason='OWNED_EXIT_UNCONFIRMED'}
        Write-Warning "Owned process finalization failed: $($_.Exception.Message)"
    }
    $record.elapsedMs=$clock.Elapsed.TotalMilliseconds
    $record.finishedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    $record.samples=$samples.ToArray()
    try{
        [IO.File]::WriteAllText($receipt,($record|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    }finally{
        $child.Dispose()
    }
}
Write-Output "EXIT=$($record.exitCode) STOP=$($record.stopReason) PEAK_PRIVATE=$($record.peakPrivateBytes) OUTPUT=$output"
if($record.stopReason){exit 124}
exit $record.exitCode
