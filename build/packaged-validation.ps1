# Shared release gate definition and evidence verification. Dot-source; no application launch.
function Get-PackagedSelfTests {
    @(
        [pscustomobject]@{ Name = 'capture'; Switch = '--selftest-capture'; Report = 'selftest-report.txt' }
        [pscustomobject]@{ Name = 'shell'; Switch = '--selftest-shell'; Report = 'shell-selftest-report.txt' }
        [pscustomobject]@{ Name = 'advanced'; Switch = '--selftest-advanced'; Report = 'advanced-selftest-report.txt' }
        [pscustomobject]@{ Name = 'settings'; Switch = '--selftest-settings'; Report = 'settings-selftest-report.txt' }
        [pscustomobject]@{ Name = 'ocr'; Switch = '--selftest-ocr'; Report = 'ocr-selftest-report.txt' }
        [pscustomobject]@{ Name = 'recording'; Switch = '--selftest-recording'; Report = 'recording-selftest-report.txt' }
        [pscustomobject]@{ Name = 'video-editor'; Switch = '--selftest-video-editor'; Report = 'video-editor-selftest-report.txt' }
        [pscustomobject]@{ Name = 'hotfix-ux'; Switch = '--selftest-hotfix-ux'; Report = 'hotfix-ux-report.txt' }
    )
}

function Assert-PackagedSelfTestResult([string]$RunRoot) {
    $result = Get-Content -LiteralPath (Join-Path $RunRoot 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $tests = @(Get-PackagedSelfTests)
    if ($result.SchemaVersion -ne 1 -or $result.Result -cne 'PASS' -or
        $result.PassCount -ne $tests.Count -or @($result.Tests).Count -ne $tests.Count) {
        throw 'Packaged self-test result is incomplete or failed.'
    }
    foreach ($test in $tests) {
        $records = @($result.Tests | Where-Object { $_.Name -ceq $test.Name })
        $relative = "$($test.Name)/$($test.Report)"
        if ($records.Count -ne 1 -or $records[0].Path -cne $relative -or
            $null -eq $records[0].ExitCode -or $records[0].ExitCode -ne 0 -or
            $records[0].Result -cne 'RESULT: PASS') { throw "Invalid self-test evidence: $($test.Name)" }
        $report = Join-Path $RunRoot $relative
        $lines = @(Get-Content -LiteralPath $report -Encoding UTF8 | Where-Object { $_.StartsWith('RESULT:', [StringComparison]::Ordinal) })
        if ($lines.Count -ne 1 -or $lines[0] -cne 'RESULT: PASS' -or
            (Get-FileHash -LiteralPath $report -Algorithm SHA256).Hash -ine $records[0].Sha256) {
            throw "Self-test report changed or failed: $relative"
        }
    }
    return $result
}

function Assert-StorePublishProvenance([string]$RepoRoot, [string]$PublishRoot, [string]$Version) {
    $manifestPath = Join-Path (Split-Path -Parent $PublishRoot) 'release-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $head = (& git -C $RepoRoot rev-parse --verify 'HEAD^{commit}' | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve Store source commit.' }
    $status = @(& git -C $RepoRoot status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw 'Store packaging requires a clean source worktree.' }
    if ($manifest.SchemaVersion -ne 3 -or $manifest.Product -cne 'MyCapture' -or
        $manifest.Version -cne $Version -or $manifest.SourceTreeClean -isnot [bool] -or
        -not $manifest.SourceTreeClean -or $manifest.SourceCommit -cne $head -or
        $manifest.Runtime -cne 'win-x64' -or $manifest.Architecture -cne 'x64' -or
        $manifest.SelfContained -isnot [bool] -or -not $manifest.SelfContained) {
        throw 'Store publish provenance does not match the exact clean source/version/runtime.'
    }
    if (@(Get-ChildItem -LiteralPath $PublishRoot -Recurse -Force | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0) {
        throw 'Store publish contains a reparse point.'
    }
    $files = @(Get-ChildItem -LiteralPath $PublishRoot -File -Recurse -Force)
    $inventory = @($manifest.PublishInventory)
    if ($inventory.Count -lt 1 -or $inventory.Count -gt 10000 -or $files.Count -ne $inventory.Count) {
        throw 'Store publish inventory file count mismatch.'
    }
    $expected = @{}
    [long]$total = 0
    foreach ($record in $inventory) {
        $path = [string]$record.Path
        if ($path -match '\\|:|^/|/$' -or @($path.Split('/') | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -gt 0 -or $expected.ContainsKey($path)) {
            throw "Unsafe or duplicate publish inventory path: $path"
        }
        $expected[$path] = $record
    }
    foreach ($file in $files) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Publish file is a reparse point: $($file.FullName)" }
        $path = $file.FullName.Substring($PublishRoot.TrimEnd('\').Length + 1).Replace('\', '/')
        if (-not $expected.ContainsKey($path)) { throw "Unmanifested publish file: $path" }
        $record = $expected[$path]
        if ($file.Length -ne [long]$record.Bytes -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ine $record.Sha256) {
            throw "Store publish inventory hash/size mismatch: $path"
        }
        $total += $file.Length
        if ($total -gt 2147483648) { throw 'Store payload exceeds the 2 GiB safety limit.' }
    }
    if ($null -ne $manifest.PSObject.Properties['StoreOnly'] -and $manifest.StoreOnly) {
        $runName = [string]$manifest.PackagedSelfTestRun
        if ($runName -cnotmatch '\A[0-9]{8}T[0-9]{6}Z-[0-9a-f]{32}\z') { throw 'Unsafe Store self-test run name.' }
        $runRoot = Join-Path (Join-Path (Split-Path -Parent $PublishRoot) 'packaged-self-tests') $runName
        $selfTests = Assert-PackagedSelfTestResult $runRoot
        if ($selfTests.PublishRoot -ine $PublishRoot -or
            (Get-FileHash -LiteralPath (Join-Path $runRoot 'result.json') -Algorithm SHA256).Hash -ine $manifest.PackagedSelfTestResultSha256) {
            throw 'Store self-test evidence does not match the publish provenance.'
        }
    }
    return $manifest
}
