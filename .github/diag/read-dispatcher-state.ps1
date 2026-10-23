# Diagnostic branch only. Reads the WPF Dispatcher's internal state from a hang dump:
# processing request type, Win32 timer flags, the pending queue with each operation's
# priority, status and target method, and every live DispatcherTimer.
param(
    [Parameter(Mandatory = $true)] [string]$Dump,
    [Parameter(Mandatory = $true)] [string]$Publish,
    [Parameter(Mandatory = $true)] [string]$Out,
    [string]$NativeReport = ''
)
$ErrorActionPreference = 'Continue'
$tool = Join-Path $env:USERPROFILE '.dotnet\tools\dotnet-dump.exe'
$report = New-Object System.Text.StringBuilder
function Add([string]$Text) { [void]$report.AppendLine($Text) }
function Sos([string[]]$Commands) {
    $arguments = @('analyze', $Dump, '-c', "setclrpath $Publish")
    foreach ($command in $Commands) { $arguments += @('-c', $command) }
    $arguments += @('-c', 'exit')
    return (& $tool @arguments 2>&1 | Out-String)
}
# dumpobj field rows end with: <VT> <instance|static> <Value> <Name>.
function Fields([string]$Text) {
    $map = @{}
    foreach ($line in ($Text -split "`r?`n")) {
        $parts = @($line.Trim() -split '\s+')
        if ($parts.Count -ge 7 -and $parts[0] -match '^[0-9a-fA-F]{8,16}$' -and $parts[-3] -eq 'instance') {
            $map[$parts[-1]] = $parts[-2]
        }
    }
    return $map
}
function Addresses([string]$Text) {
    return @(($Text -split "`r?`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[0-9a-fA-F]{12,16}$' })
}
function ArrayItems([string]$Address) {
    $text = Sos @("dumparray $Address")
    return @([regex]::Matches($text, '(?m)^\s*\[\d+\]\s+([0-9a-fA-F]{12,16})\s*$') | ForEach-Object { $_.Groups[1].Value })
}
function Obj([string]$Address, [string]$Title) {
    $text = Sos @("dumpobj $Address")
    Add "### $Title ($Address)"
    Add $text
    return (Fields $text)
}
function NonZero([string]$Value) { return $Value -and $Value -notmatch '^0+$' }

if ($NativeReport -and (Test-Path -LiteralPath $NativeReport)) {
    $uptime = Select-String -LiteralPath $NativeReport -Pattern 'System Uptime|Process Uptime|Debug session time' | ForEach-Object { $_.Line.Trim() }
    Add '## Dump time (Environment.TickCount is the system uptime in milliseconds)'
    $uptime | ForEach-Object { Add $_ }
}

$names = Sos @('name2ee *!System.Windows.Threading.Dispatcher', 'name2ee *!System.Windows.Threading.DispatcherTimer')
$tables = @{}
foreach ($match in [regex]::Matches($names, 'MethodTable:\s+([0-9a-fA-F]+)[\s\S]*?Name:\s+(\S+)')) {
    $tables[$match.Groups[2].Value] = $match.Groups[1].Value
}
Add '## Method tables'
$tables.GetEnumerator() | ForEach-Object { Add "$($_.Key) = $($_.Value)" }

foreach ($dispatcher in (Addresses (Sos @("dumpheap -mt $($tables['System.Windows.Threading.Dispatcher']) -live -short")))) {
    Add '## Dispatcher'
    $fields = Obj $dispatcher 'Dispatcher'
    if (NonZero $fields['_queue']) {
        $queue = Obj $fields['_queue'] 'PriorityQueue'
        if (NonZero $queue['_priorityChains']) {
            $chains = Obj $queue['_priorityChains'] 'SortedList of priority chains'
            if (NonZero $chains['values']) {
                Add '### Priority chains'
                foreach ($chain in (ArrayItems $chains['values'])) { [void](Obj $chain 'PriorityChain') }
            }
        }
        # Walk the pending items in order and name each operation's target method.
        $item = $queue['_head']
        for ($index = 0; $index -lt 60 -and (NonZero $item); $index++) {
            $itemFields = Obj $item "Queue item $index"
            if (NonZero $itemFields['_data']) {
                $operation = Obj $itemFields['_data'] "Operation $index"
                if (NonZero $operation['_method']) {
                    Add "#### Operation $index method"
                    Add (Sos @("dumpdelegate $($operation['_method'])"))
                }
            }
            $item = $itemFields['_sequentialNext']
        }
    }
    if (NonZero $fields['_timers']) {
        $timers = Obj $fields['_timers'] 'Dispatcher timer list'
        if (NonZero $timers['_items']) {
            foreach ($timer in (ArrayItems $timers['_items'])) { [void](Obj $timer 'Listed DispatcherTimer') }
        }
    }
}

Add '## Every live DispatcherTimer'
foreach ($timer in (Addresses (Sos @("dumpheap -mt $($tables['System.Windows.Threading.DispatcherTimer']) -live -short")))) {
    $timerFields = Obj $timer 'DispatcherTimer'
    $tick = if (NonZero $timerFields['Tick']) { $timerFields['Tick'] } else { $timerFields['_tick'] }
    if (NonZero $tick) {
        Add '#### Tick handler'
        Add (Sos @("dumpdelegate $tick"))
    }
}

[IO.File]::WriteAllText($Out, $report.ToString(), (New-Object Text.UTF8Encoding($false)))
