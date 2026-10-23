# Development-only helpers. These files are never included in the setup package.
$setupTestRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$setupTestOutput = Join-Path $setupTestRepo 'src\MyCapture.Setup\bin\Release\net48'
$setupTestExe = Join-Path $setupTestOutput 'MyCaptureSetup.exe'
if (-not [IO.File]::Exists($setupTestExe)) { throw 'Build MyCapture.Setup Release before running setup tests.' }
[Reflection.Assembly]::LoadFrom($setupTestExe) | Out-Null

function Remove-TestItem([string]$Path) {
    # Permanently deletes only this test's own fixture under TEMP.
    $full = [IO.Path]::GetFullPath($Path)
    $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if (-not $full.StartsWith($temp + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Test cleanup must remain under TEMP: $full" }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
}
function Get-SetupBootstrapSource([string]$Name) {
    if ($Name -eq 'SETUP-HELP.txt') { return Join-Path $setupTestRepo 'build\installer\SETUP-HELP.txt' }
    if ($Name -notin @('MyCaptureSetup.exe', 'MyCaptureSetup.exe.config')) { throw "Unexpected bootstrap name: $Name" }
    return Join-Path $setupTestOutput $Name
}
function Get-SourceReleaseVersion {
    # The release version declared in source. The built setup must carry it, so a stale build still fails below.
    [xml]$props = Get-Content -LiteralPath (Join-Path $setupTestRepo 'Directory.Build.props') -Raw -Encoding UTF8
    $nodes = @($props.SelectNodes('/Project/PropertyGroup/Version'))
    if ($nodes.Count -ne 1) { throw 'Directory.Build.props must declare exactly one Version.' }
    return [string]$nodes[0].InnerText
}
function New-SyntheticSetupPackage([string]$Root, [string]$Version = (Get-SourceReleaseVersion)) {
    $Root = [IO.Path]::GetFullPath($Root)
    $publish = Join-Path $Root 'publish-win-x64'
    $stage = Join-Path $Root 'installer-stage'
    [IO.Directory]::CreateDirectory((Join-Path $publish 'Assets')) | Out-Null
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    foreach ($name in @('MyCapture.exe', 'MyCapture.dll')) { Copy-Item -LiteralPath $setupTestExe -Destination (Join-Path $publish $name) }
    foreach ($name in @('tray-idle.ico', 'tray-capturing.ico', 'tray-busy.ico', 'tray-error.ico')) {
        [IO.File]::WriteAllBytes((Join-Path $publish "Assets\$name"), [byte[]](0, 1, 2))
    }
    $files = @(Get-ChildItem -LiteralPath $publish -File -Recurse | ForEach-Object {
        [MyCapture.Setup.FileRecord]::Create($_.FullName, $_.FullName.Substring($publish.Length + 1).Replace('\', '/'))
    })
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $payload = Join-Path $Root "MyCapture-$Version-win-x64-portable.zip"
    $archive = [IO.Compression.ZipFile]::Open($payload, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($record in $files) {
            $entry = $archive.CreateEntry([string]$record.Path)
            $input = [IO.File]::OpenRead((Join-Path $publish ([string]$record.Path).Replace('/', '\')))
            $output = $entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    }
    finally { $archive.Dispose() }
    Copy-Item -LiteralPath $payload -Destination (Join-Path $stage 'payload.zip')
    $bootstrap = @('MyCaptureSetup.exe', 'MyCaptureSetup.exe.config', 'SETUP-HELP.txt') | ForEach-Object {
        $source = Get-SetupBootstrapSource $_
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage $_)
        [MyCapture.Setup.FileRecord]::Create($source, $_)
    }
    $manifest = [ordered]@{ SchemaVersion = 1; Product = 'MyCapture'; Version = $Version; Runtime = 'win-x64'; Architecture = 'x64'
        MinimumWindowsBuild = 19045; SelfContained = $true; Offline = $true
        Payload = [MyCapture.Setup.FileRecord]::Create($payload, 'payload.zip'); Files = $files; FileCount = $files.Count
        TotalExpandedBytes = [long](($files | Measure-Object -Property Bytes -Sum).Sum); BootstrapFiles = @($bootstrap) }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Root 'installer-manifest.json') -Encoding UTF8
    Copy-Item -LiteralPath (Join-Path $Root 'installer-manifest.json') -Destination (Join-Path $stage 'installer-manifest.json')
    $informational = [Diagnostics.FileVersionInfo]::GetVersionInfo($setupTestExe).ProductVersion
    if (($informational -split '\+')[0] -ne $Version) { throw 'Synthetic version must match the built setup product version.' }
    # Synthetic provenance is a test fixture, never a release artifact or publishing input.
    @{ SchemaVersion = 3; Product = 'MyCapture'; Version = $Version; SourceCommit = ($informational -split '\+')[1]; SourceTreeClean = $true; Synthetic = $true } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Root 'release-manifest.json') -Encoding UTF8
}
