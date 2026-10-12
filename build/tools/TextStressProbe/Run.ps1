param(
    [Parameter(Mandatory=$true)][string]$AppPayload,
    [Parameter(Mandatory=$true)][ValidateSet('ocr','editor','editor-render','editor-hold','ocr-hold','ocr-editor-hold')][string]$Case,
    [ValidateRange(1,1000000)][int]$Characters=100000,
    [ValidateSet('long','lines')][string]$Shape='lines',
    [ValidateSet('readonly-no-ime','bounded-view','content-scroll','settled-edits','line-selection','no-auto-selection')][string[]]$Diagnostic=@(),
    [string]$OutputDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$RuntimeVersion='10.0.11',
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Label='baseline',
    [string]$ProbeDll
)
$ErrorActionPreference='Stop'
$hold=$Case.EndsWith('-hold')
if($hold -and ($Characters -gt 100000 -or $Diagnostic.Count -gt 0)){throw 'Hold modes use actual product settings and at most 100000 synthetic characters'}
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
if(!$OutputDirectory){$OutputDirectory=Join-Path $repoRoot 'artifacts/text-stress'}
$taskRoot=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($taskRoot)|Out-Null
if(!$ProbeDll){$ProbeDll=Join-Path $PSScriptRoot 'bin/Release/net10.0-windows10.0.22000.0/MyCapture.App.Tests.dll'}
$probe=[IO.Path]::GetFullPath($ProbeDll)
$payload=[IO.Path]::GetFullPath($AppPayload)
$target=Join-Path $payload 'MyCapture.dll'
if(!(Test-Path -LiteralPath $probe -PathType Leaf)){throw 'Build TextStress.csproj in Release before running'}
if(!(Test-Path -LiteralPath $target -PathType Leaf)){throw 'AppPayload must contain MyCapture.dll'}
$tag=$Label+'-'+$Case+'-'+$Characters+'-'+$Shape+'-'+[Guid]::NewGuid().ToString('N')
$output=Join-Path $taskRoot ($tag+'.json')
$receipt=Join-Path $taskRoot ($tag+'.process.json')
$runtimePath=Join-Path $taskRoot ($tag+'.runtimeconfig.json')
foreach($path in @($output,$receipt,$runtimePath)){
    if(Test-Path -LiteralPath $path){throw 'Generated output already exists; preserve it'}
}
$runtime=Get-Content -Raw -Encoding UTF8 -LiteralPath ([IO.Path]::ChangeExtension($probe,'.runtimeconfig.json'))|ConvertFrom-Json
$frameworkNames=@($runtime.runtimeOptions.frameworks | ForEach-Object {$_.name})
if($frameworkNames -notcontains 'Microsoft.NETCore.App' -or $frameworkNames -notcontains 'Microsoft.WindowsDesktop.App'){throw 'Probe runtimeconfig must declare both Core and Desktop frameworks'}
foreach($framework in $runtime.runtimeOptions.frameworks){
    if($framework.name -in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')){$framework.version=$RuntimeVersion}
}
$runtime.runtimeOptions | Add-Member -MemberType NoteProperty -Name rollForward -Value 'Disable' -Force
[IO.File]::WriteAllText($runtimePath,($runtime|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$info=[Diagnostics.ProcessStartInfo]::new()
$info.FileName=(Get-Command dotnet.exe -ErrorAction Stop).Source
$options=[string]::Join(',', $Diagnostic)
function Quote-OwnedArgument([string]$value){
    # Windows command-line parsing doubles trailing backslashes before a closing quote.
    return '"'+[regex]::Replace($value,'(\\+)$','$1$1')+'"'
}
$arguments=@('exec','--runtimeconfig',$runtimePath,$probe,$payload,$Case,[string]$Characters,$Shape,$output)
if($options){$arguments+=,$options}
$info.Arguments=[string]::Join(' ',@($arguments|ForEach-Object {Quote-OwnedArgument $_}))
$info.WorkingDirectory=$taskRoot
$info.UseShellExecute=$false
$info.CreateNoWindow=$true
$info.RedirectStandardOutput=$true
$info.RedirectStandardError=$true
$timeout=if($hold){600}else{60}
$record=[ordered]@{case=$Case;label=$Label;manualHold=$hold;characters=$Characters;shape=$Shape;diagnostic=$options;pid=$null;cwd=$taskRoot;command=$info.FileName;arguments=$info.Arguments;probe=$probe;probeSha256=(Get-FileHash -LiteralPath $probe -Algorithm SHA256).Hash;runtimeConfig=$runtimePath;runtimeConfigSha256=(Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash;payload=$payload;targetSha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash;startedUtc=[DateTimeOffset]::UtcNow.ToString('o');timeoutSeconds=$timeout;privateByteLimit=2147483648;exitCode=$null;stopReason=$null;peakPrivateBytes=0;peakWorkingSet=0;samples=@();stdout=$null;stderr=$null;resultStatus=$null}
$child=[Diagnostics.Process]::new()
$child.StartInfo=$info
$started=$false
$outTask=$null
$errTask=$null
$clock=[Diagnostics.Stopwatch]::StartNew()
$samples=[Collections.Generic.List[object]]::new()
function Read-OwnedStream($task){
    if($null -eq $task){return $null}
    if(!$task.Wait(5000)){throw 'Owned stdout/stderr drain was not confirmed within 5 seconds'}
    return $task.GetAwaiter().GetResult()
}
try{
    if(!$child.Start()){throw 'Owned child did not start'}
    $started=$true
    $record.pid=$child.Id
    $outTask=$child.StandardOutput.ReadToEndAsync()
    $errTask=$child.StandardError.ReadToEndAsync()
    [IO.File]::WriteAllText($receipt,($record|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    Write-Output "OWNED_PID=$($child.Id) CASE=$Case OUTPUT=$output RECEIPT=$receipt"
    while(!$child.WaitForExit(200)){
        $child.Refresh()
        if($child.HasExited){break}
        $memory=$child.PrivateMemorySize64
        $working=$child.WorkingSet64
        $record.peakPrivateBytes=[Math]::Max($record.peakPrivateBytes,$memory)
        $record.peakWorkingSet=[Math]::Max($record.peakWorkingSet,$working)
        $samples.Add([pscustomobject]@{elapsedMs=$clock.ElapsedMilliseconds;privateBytes=$memory;workingSet=$working;cpuMs=$child.TotalProcessorTime.TotalMilliseconds})
        if($memory -gt 2147483648){$record.stopReason='OWNED_MEMORY_LIMIT'}
        elseif($clock.Elapsed.TotalSeconds -gt $timeout){$record.stopReason='OWNED_TIMEOUT'}
        if($record.stopReason){
            $child.Kill()
            if(!$child.WaitForExit(5000)){throw 'Owned process exit was not confirmed within 5 seconds'}
            break
        }
    }
    $record.exitCode=$child.ExitCode
    $record.stdout=Read-OwnedStream $outTask
    $record.stderr=Read-OwnedStream $errTask
}catch{
    $record.stopReason='HARNESS_EXCEPTION'
    $record.harnessError=$_.Exception.ToString()
    throw
}finally{
    # Stop only the exact Process created above, even if receipt IO or observation failed.
    try{
        if($started){
            if(!$child.HasExited){
                $child.Kill()
                if(!$child.WaitForExit(5000)){throw 'Owned process finalization was not confirmed within 5 seconds'}
            }
            $record.exitCode=$child.ExitCode
            $record.stdout=Read-OwnedStream $outTask
            $record.stderr=Read-OwnedStream $errTask
        }
    }catch{
        $record.finalizationError=$_.Exception.ToString()
        if(!$record.stopReason){$record.stopReason='OWNED_EXIT_UNCONFIRMED'}
        Write-Warning "Owned process finalization failed: $($_.Exception.Message)"
    }
    $record.elapsedMs=$clock.Elapsed.TotalMilliseconds
    $record.finishedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    $record.samples=$samples.ToArray()
    if(Test-Path -LiteralPath $output -PathType Leaf){
        try{
            $result=Get-Content -LiteralPath $output -Encoding UTF8 -Raw|ConvertFrom-Json
            $record.resultStatus=$result.Status
            $record.resultSha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
        }catch{
            $record.resultReadError=$_.Exception.ToString()
            if(!$record.stopReason){$record.stopReason='RESULT_UNCONFIRMED'}
        }
    }
    try{[IO.File]::WriteAllText($receipt,($record|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))}
    finally{$child.Dispose()}
}
Write-Output "EXIT=$($record.exitCode) STOP=$($record.stopReason) STATUS=$($record.resultStatus) OUTPUT=$output"
if($record.stopReason -or $record.exitCode -eq 124){exit 124}
if($record.exitCode -ne 0){exit $record.exitCode}
$expected=if($hold){'HOLD_CLOSED'}else{'PASS'}
if($record.resultStatus -ne $expected){throw 'Exit zero without the expected final result status'}
exit 0
