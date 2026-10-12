param(
    [Parameter(Mandatory=$true)][string]$Payload,
    [ValidateSet(3840,7680)][int]$Width=3840,
    [ValidateSet(1000,10000)][int]$Items=1000,
    [int]$StrokePoints=5000,
    [string]$Label='baseline',
    [ValidateSet('solid','entropy')][string]$PixelPattern='solid',
    [ValidateSet('product','independent')][string]$EncoderMode='product',
    [string]$EvidenceRoot=''
)
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$evidence=if([string]::IsNullOrWhiteSpace($EvidenceRoot)){Join-Path $repoRoot 'artifacts/stress-editor'}else{[IO.Path]::GetFullPath($EvidenceRoot)}
$payloadFull=[IO.Path]::GetFullPath($Payload)
$hostDll=Join-Path $evidence 'harness-build/EditorStress.dll'
$runtime=Join-Path $payloadFull 'MyCapture.runtimeconfig.json'
$deps=Join-Path $payloadFull 'MyCapture.deps.json'
foreach($file in @($hostDll,$runtime,$deps)){if(!(Test-Path -LiteralPath $file -PathType Leaf)){throw "Missing $file"}}
if($Label -notmatch '^[a-zA-Z0-9_-]+$'){throw 'Label must be a simple evidence identifier'}
$tag=$Label+'-'+$PixelPattern+'-'+$EncoderMode+'-'+$Width+'-'+$Items+'-'+[DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$root=Join-Path $evidence $tag
$receipt=Join-Path $evidence ($tag+'-process.json')
$originalRuntime=Get-Content -LiteralPath $runtime -Raw | ConvertFrom-Json
$pinnedRuntime=Join-Path $evidence ($tag+'-runtimeconfig.json')
$runtimeOptions=[ordered]@{tfm='net10.0';frameworks=@(@{name='Microsoft.NETCore.App';version='10.0.11'},@{name='Microsoft.WindowsDesktop.App';version='10.0.11'});rollForward='Disable'}
if($null -ne $originalRuntime.runtimeOptions.configProperties){$runtimeOptions.configProperties=$originalRuntime.runtimeOptions.configProperties}
[IO.File]::WriteAllText($pinnedRuntime,(@{runtimeOptions=$runtimeOptions} | ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
$arguments=@('exec','--runtimeconfig',$pinnedRuntime,'--depsfile',$deps,$hostDll,$payloadFull,$root,[string]$Width,[string]$Items,[string]$StrokePoints,$PixelPattern,$EncoderMode)
foreach($argument in $arguments){if($argument.Contains('"')){throw 'Unsupported quote'}}
$start=[Diagnostics.ProcessStartInfo]::new()
$start.FileName=(Get-Command dotnet.exe -ErrorAction Stop).Source
$start.Arguments=($arguments | ForEach-Object {'"'+[regex]::Replace($_,'\\+$','$0$0')+'"'}) -join ' '
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
$start.WorkingDirectory=$evidence
$metadata=[ordered]@{pid=$null;cwd=$evidence;command=$start.FileName;arguments=$arguments;commandLineArguments=$start.Arguments;startedUtc=[DateTimeOffset]::UtcNow.ToString('o');exitCode=$null;stopReason=$null;payloadSha256=(Get-FileHash -LiteralPath (Join-Path $payloadFull 'MyCapture.dll') -Algorithm SHA256).Hash;hostSha256=(Get-FileHash -LiteralPath $hostDll -Algorithm SHA256).Hash;runtimeConfig=$pinnedRuntime;runtimeConfigSha256=(Get-FileHash -LiteralPath $pinnedRuntime -Algorithm SHA256).Hash;output=$root}
$process=[Diagnostics.Process]::new()
$process.StartInfo=$start
$started=$false
$stdout=$null
$stderr=$null
try{
    if(!$process.Start()){throw 'Owned harness did not start'}
    $started=$true
    $metadata.pid=$process.Id
    $stdout=$process.StandardOutput.ReadToEndAsync()
    $stderr=$process.StandardError.ReadToEndAsync()
    [IO.File]::WriteAllText($receipt,($metadata | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    Write-Output "OWNED_PID=$($process.Id) RECEIPT=$receipt"
    if(!$process.WaitForExit(60000)){
        $metadata.stopReason='OWNED_TIMEOUT'
        throw 'Owned harness exceeded 60 seconds'
    }
}catch{
    if(!$metadata.stopReason){$metadata.stopReason='HARNESS_EXCEPTION'}
    $metadata.harnessError=$_.Exception.ToString()
    throw
}finally{
    # Finalize only the exact process this runner started, even if receipt IO failed.
    try{
        if($started){
            if(!$process.HasExited){
                $process.Kill()
                if(!$process.WaitForExit(5000)){
                    $metadata.stopReason='OWNED_EXIT_UNCONFIRMED'
                    throw 'Owned process exit was not confirmed within 5 seconds'
                }
            }
            $metadata.exitCode=$process.ExitCode
            foreach($entry in @(@('stdout',$stdout),@('stderr',$stderr))){
                if($null -ne $entry[1]){
                    if(!$entry[1].Wait(5000)){
                        $metadata.stopReason='OUTPUT_UNCONFIRMED'
                        throw 'Owned output collection was not confirmed within 5 seconds'
                    }
                    [IO.File]::WriteAllText((Join-Path $evidence ($tag+'-'+$entry[0]+'.log')),$entry[1].GetAwaiter().GetResult(),[Text.UTF8Encoding]::new($false))
                }
            }
        }
    }catch{
        $metadata.finalizationError=$_.Exception.ToString()
        if(!$metadata.stopReason){$metadata.stopReason='HARNESS_FINALIZATION_ERROR'}
        Write-Warning "Owned process finalization failed: $($_.Exception.Message)"
    }
    $metadata.finishedUtc=[DateTimeOffset]::UtcNow.ToString('o')
    try{
        [IO.File]::WriteAllText($receipt,($metadata | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    }finally{
        $process.Dispose()
    }
}
Write-Output "EXIT=$($metadata.exitCode) STOP=$($metadata.stopReason) OUTPUT=$root"
if($metadata.stopReason){exit 124}
exit $metadata.exitCode
