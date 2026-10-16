# Records the runner state that could slow the first WPF media open (diagnostic only):
# audio and media services, Defender, display and sound devices, the Windows Media Player
# feature and per-user state, and what else is busy. Writes JSON to -Path.

param(
    [Parameter(Mandatory = $true)][string]$Path
)

$ErrorActionPreference = 'Continue'

function Try-Get([scriptblock]$Block) {
    try { & $Block } catch { "unavailable: $($_.Exception.Message)" }
}

$state = [ordered]@{}
$state.capturedUtc = (Get-Date).ToUniversalTime().ToString('o')
$os = Get-CimInstance Win32_OperatingSystem
$state.os = "$($os.Caption) $($os.Version) build $($os.BuildNumber)"
$state.uptimeS = [math]::Round(((Get-Date) - $os.LastBootUpTime).TotalSeconds)
$state.freeMemoryMb = [math]::Round($os.FreePhysicalMemory / 1024)
$state.image = "$env:ImageOS $env:ImageVersion"
$state.user = "$env:USERDOMAIN\$env:USERNAME"
$state.processors = (Get-CimInstance Win32_Processor | ForEach-Object { "$($_.Name), $($_.NumberOfLogicalProcessors) logical" }) -join '; '
$state.services = @(
    Get-Service Audiosrv, AudioEndpointBuilder, WMPNetworkSvc, WinDefend, MDCoreSvc, WdNisSvc, wuauserv, TrustedInstaller, FrameServer, FontCache, SysMain -ErrorAction SilentlyContinue |
        ForEach-Object { "$($_.Name): $($_.Status), $($_.StartType)" }
)
$state.defender = Try-Get {
    $mp = Get-MpComputerStatus
    [ordered]@{
        AMRunningMode = $mp.AMRunningMode
        AMServiceEnabled = $mp.AMServiceEnabled
        AntivirusEnabled = $mp.AntivirusEnabled
        RealTimeProtectionEnabled = $mp.RealTimeProtectionEnabled
        OnAccessProtectionEnabled = $mp.OnAccessProtectionEnabled
        IoavProtectionEnabled = $mp.IoavProtectionEnabled
        BehaviorMonitorEnabled = $mp.BehaviorMonitorEnabled
        IsTamperProtected = $mp.IsTamperProtected
        AMProductVersion = $mp.AMProductVersion
        AntivirusSignatureAge = $mp.AntivirusSignatureAge
    }
}
$state.defenderPreference = Try-Get {
    $pref = Get-MpPreference
    [ordered]@{
        DisableRealtimeMonitoring = $pref.DisableRealtimeMonitoring
        ExclusionPath = @($pref.ExclusionPath)
        ExclusionProcess = @($pref.ExclusionProcess)
    }
}
$state.videoControllers = @(Get-CimInstance Win32_VideoController | ForEach-Object { "$($_.Name), driver $($_.DriverVersion), $($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)" })
$state.soundDevices = @(Get-CimInstance Win32_SoundDevice | ForEach-Object { "$($_.Name) ($($_.Status))" })
$state.audioEndpoints = @(Try-Get { Get-PnpDevice -Class AudioEndpoint -ErrorAction Stop | ForEach-Object { "$($_.FriendlyName) ($($_.Status))" } })
$state.windowsMediaPlayerFeature = Try-Get { (Get-WindowsOptionalFeature -Online -FeatureName WindowsMediaPlayer -ErrorAction Stop).State.ToString() }
$state.mediaFoundationFeature = Try-Get { (Get-WindowsFeature Server-Media-Foundation -ErrorAction Stop).InstallState.ToString() }
$state.wmpDll = Try-Get { (Get-Item "$env:WINDIR\System32\wmp.dll" -ErrorAction Stop).VersionInfo.FileVersion }
$state.mediaPlayerUserKey = if (Test-Path 'HKCU:\Software\Microsoft\MediaPlayer') {
    @(Get-ChildItem 'HKCU:\Software\Microsoft\MediaPlayer' -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })
} else { 'absent' }
$state.sessions = Try-Get { (query session 2>&1) -join "`n" }
$state.busiestProcesses = @(
    Get-Process | Where-Object { $_.CPU } | Sort-Object CPU -Descending | Select-Object -First 12 |
        ForEach-Object { "$($_.ProcessName) ($($_.Id)): cpu $([math]::Round($_.CPU, 1)) s" }
)
$state.machineBusyPct = Try-Get { [math]::Round((Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average) }
$state.systemDrive = Try-Get {
    $volume = Get-Volume -DriveLetter C
    $disk = Get-PhysicalDisk | Select-Object -First 1
    "free $([math]::Round($volume.SizeRemaining / 1GB)) GB of $([math]::Round($volume.Size / 1GB)) GB; $($disk.FriendlyName) $($disk.MediaType) $($disk.BusType)"
}

$state | ConvertTo-Json -Depth 6 | Set-Content -Path $Path -Encoding utf8
Get-Content -Path $Path
