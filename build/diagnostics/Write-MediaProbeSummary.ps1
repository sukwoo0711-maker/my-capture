# Summarises one job of the media cold-open probe as Markdown (diagnostic only).

param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Title
)

$ErrorActionPreference = 'Continue'

function Ms($value) { if ($null -eq $value) { '-' } else { '{0:N0}' -f $value } }

$lines = @("### $Title", '')
$start = Join-Path $Path 'runner-start.json'
if (Test-Path $start) {
    $state = Get-Content $start -Raw | ConvertFrom-Json
    $lines += "- runner: $($state.image), $($state.os), up $($state.uptimeS) s, $($state.processors)"
    $lines += "- Defender real-time: $($state.defender.RealTimeProtectionEnabled), mode $($state.defender.AMRunningMode)"
    $lines += "- services at job start: $($state.services -join '; ')"
    $lines += "- sound devices: $(@($state.soundDevices).Count), audio endpoints: $(@($state.audioEndpoints) -join '; ')"
    $lines += "- video: $($state.videoControllers -join '; ')"
    $lines += ''
}

foreach ($file in Get-ChildItem -Path $Path -Filter 'run*.json' | Sort-Object Name) {
    $run = Get-Content $file.FullName -Raw | ConvertFrom-Json
    $lines += "#### $($run.label): $($run.variant), outcome **$($run.outcome)**, render tier $($run.machine.renderTier)"
    $lines += ''
    $lines += '| open | ctor ms | Open() ms | MediaOpened ms | MediaFailed | first frame ms |'
    $lines += '|---|---:|---:|---:|---|---:|'
    foreach ($open in $run.opens) {
        $failed = if ($null -ne $open.failedMs) { "$(Ms $open.failedMs) ms $($open.failureHResult)" } else { '-' }
        $lines += "| $($open.name) | $(Ms $open.ctorMs) | $(Ms $open.openReturnMs) | $(Ms $open.openedMs) | $failed | $(Ms $open.firstFrameMs) |"
    }

    $lines += ''
    $lines += "- pre: encode $(Ms $run.pre.encodeMs) ms, container read $(Ms $run.pre.containerReadMs) ms"
    $lines += "- services: $(@($run.services | Where-Object { $_ } | ForEach-Object { $_.Trim() }) -join '; ')"
    $later = @($run.processesStarted | Where-Object { $_ -and [double]($_.Trim().Split(' ')[0]) -ge 0 } | ForEach-Object { $_.Trim() })
    $lines += "- processes started after t0: $(if ($later.Count) { $later -join '; ' } else { 'none' })"
    if ($run.cpuByProcessT0ToFirstSettled) {
        $lines += "- CPU t0 to first settled ($(Ms $run.cpuByProcessT0ToFirstSettled.windowMs) ms): $(@($run.cpuByProcessT0ToFirstSettled.topCpu | Select-Object -First 6) -join '; ')"
        $lines += "- reads t0 to first settled: $(@($run.cpuByProcessT0ToFirstSettled.topRead | Select-Object -First 4) -join '; ')"
    }

    if ($run.diskLoad) {
        $lines += "- disk load: $($run.diskLoad.writtenMb) MB written from $(Ms $run.diskLoad.started) to $(Ms $run.diskLoad.stopped) ms, flush avg $($run.diskLoad.flushAvgMs) ms, max $(Ms $run.diskLoad.flushMaxMs) ms"
    }

    $flushes = @($run.flushLatency | Where-Object { $_ } | ForEach-Object { $parts = $_.Trim() -split '\s+'; [pscustomobject]@{ At = [double]$parts[0]; Ms = [double]$parts[1] } })
    $during = @($flushes | Where-Object { $_.At -ge 0 -and $_.At -le 30000 })
    if ($during.Count) {
        $lines += "- 4 KB flush on the profile drive, first 30 s after t0: max $(Ms ($during | Measure-Object Ms -Maximum).Maximum) ms, median $(Ms (@($during | Sort-Object Ms)[[int]($during.Count / 2)]).Ms) ms"
    }

    if ($run.pre.prewarmMs) { $lines += "- prewarm: $(Ms $run.pre.prewarmMs) ms" }
    if ($run.cpuByProcessT0ToFirstSettled.topHardFaults) {
        $lines += "- hard faults t0 to first settled: $(@($run.cpuByProcessT0ToFirstSettled.topHardFaults | Select-Object -First 4) -join '; ')"
    }

    if ($run.editorSteps) {
        $lines += "- editor steps (first 12):"
        $lines += @($run.editorSteps | Select-Object -First 12 | ForEach-Object { "  - $($_.Trim())" })
    }

    $lines += "- Media Player files before: $(@($run.machine.mediaPlayerFilesBefore) -join '; ')"
    $lines += "- Media Player files after: $(@($run.machine.mediaPlayerFilesAfter) -join '; ')"

    $first = $run.opens | Select-Object -First 1
    if ($null -ne $first.openCall) {
        $end = if ($null -ne $first.openedMs) { $first.openCall + $first.openedMs } else { [double]::MaxValue }
        $during = @($run.modulesLoaded | Where-Object { $_.by -ge $first.ctorStart -and $_.after -le $end })
        $lines += "- modules loaded from P1 construction to MediaOpened: $($during.Count)"
        $lines += '<details><summary>module timeline (ms after t0)</summary>'
        $lines += ''
        $lines += '```'
        $lines += @($during | ForEach-Object { '{0,9:N0} {1,9:N0}  {2}' -f $_.after, $_.by, $_.module })
        $lines += '```'
        $lines += '</details>'
    }

    $dumps = @($run.dumps | ForEach-Object { "$($_.name) at $(Ms $_.requested) ms$(if ($_.error) { " failed: $($_.error)" })" })
    if ($dumps.Count) { $lines += "- dumps: $($dumps -join '; ')" }
    $lines += ''
}

$lines -join "`n"
