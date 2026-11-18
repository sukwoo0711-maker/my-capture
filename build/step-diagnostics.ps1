# What build.ps1 prints when a build or test step fails: the failure lines first, then context.
#
# The test log interleaves thousands of "Passed ..." lines, and test names or parameters can contain
# words such as "failed" (for example "Passed X(count: 1, failed: False)"). Matching every such word
# and printing only the first lines found let passing tests fill the output, so a failure late in the
# log printed neither its test name nor its error. Lines that name a failure are therefore collected
# on their own and printed before any context, and a "Passed" line never counts as a failure hint.

# A line that is itself a failure: a failed test, its error, a compiler or MSBuild error, the verdict.
$script:StepFailurePattern =
    '(\[FAIL\]|^\s*Failed\s+\S|^\s*Failed!|Error Message:|\berror\s+[A-Za-z]+\d+\s*:|MSB\d{4}|Build FAILED|Test Run Failed|Test Run Aborted|The active test run was aborted)'

# A wider net, used only to choose context once the failures are in.
$script:StepFailureHintPattern =
    '(?i)(\[FAIL\]|\bFailed\b|\bFailure\b|Error Message:|MSB\d{4}|Test Run Failed)'

$script:StepPassedPattern = '^\s*Passed\s'

function Get-StepFailureDiagnostics {
    param(
        [AllowEmptyCollection()]
        [string[]]$Lines,
        [int]$MaxFailureLines = 80,
        [int]$MaxContextLines = 240
    )

    if ($null -eq $Lines) { $Lines = @() }
    $failureIndices = New-Object System.Collections.Generic.List[int]
    $hintIndices = New-Object System.Collections.Generic.List[int]
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $line = $Lines[$index]
        if ($line -match $script:StepPassedPattern) { continue }
        if ($line -cmatch $script:StepFailurePattern) { $failureIndices.Add($index) }
        elseif ($line -match $script:StepFailureHintPattern) { $hintIndices.Add($index) }
    }

    $output = New-Object System.Collections.Generic.List[string]
    if ($failureIndices.Count -eq 0 -and $hintIndices.Count -eq 0) {
        $start = [Math]::Max(0, $Lines.Count - 120)
        for ($index = $start; $index -lt $Lines.Count; $index++) { $output.Add($Lines[$index]) }
        return $output.ToArray()
    }

    if ($failureIndices.Count -gt 0) {
        $output.Add("Failures ($($failureIndices.Count) line(s)):")
        foreach ($index in ($failureIndices | Select-Object -First $MaxFailureLines)) { $output.Add($Lines[$index]) }
        if ($failureIndices.Count -gt $MaxFailureLines) {
            $output.Add("... $($failureIndices.Count - $MaxFailureLines) more failure line(s) in the full log")
        }
    }

    # Context windows: around the failures first, then around the other hints, within one budget.
    $chosen = New-Object 'System.Collections.Generic.SortedSet[int]'
    foreach ($hit in @($failureIndices) + @($hintIndices)) {
        $start = [Math]::Max(0, $hit - 3)
        $end = [Math]::Min($Lines.Count - 1, $hit + 12)
        for ($index = $start; $index -le $end -and $chosen.Count -lt $MaxContextLines; $index++) {
            [void]$chosen.Add($index)
        }
        if ($chosen.Count -ge $MaxContextLines) { break }
    }

    $output.Add('Context:')
    foreach ($index in $chosen) { $output.Add($Lines[$index]) }
    return $output.ToArray()
}
