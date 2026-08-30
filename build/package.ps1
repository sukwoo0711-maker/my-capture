param([string]$Version = '0.2.0')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must use numeric SemVer form (for example 0.2.0): $Version" }
$binaryVersion = "$Version.0"
$repo = Split-Path -Parent $PSScriptRoot
$dotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $dotnetDir 'dotnet.exe')) { $env:Path += ";$dotnetDir" }
$env:DOTNET_CLI_UI_LANGUAGE='en';$env:DOTNET_NOLOGO='1';$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$project = Join-Path $repo 'src\MyCapture.App\MyCapture.App.csproj'
$artifactRoot = Join-Path $repo "artifacts\release\$Version"
$publish = Join-Path $artifactRoot 'publish-win-x64'
$stage = Join-Path $artifactRoot 'installer-stage'
Remove-Item $artifactRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publish,$stage | Out-Null
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish `
    -p:Version=$Version -p:FileVersion=$binaryVersion -p:AssemblyVersion=$binaryVersion `
    -p:DebugType=None -p:DebugSymbols=false -p:PublishReadyToRun=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
$exe = Join-Path $publish 'MyCapture.exe'
if (-not (Test-Path $exe)) { throw 'Published executable is missing.' }
foreach($asset in 'tray-idle.ico','tray-capturing.ico','tray-busy.ico') {
    if (-not (Test-Path (Join-Path $publish "Assets\$asset"))) { throw "Published asset missing: Assets\$asset" }
}
$portable = Join-Path $artifactRoot "MyCapture-$Version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $portable -CompressionLevel Optimal
$payload = Join-Path $stage 'payload.zip'
Copy-Item $portable $payload
Copy-Item (Join-Path $PSScriptRoot 'installer\install.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'installer\install.cmd') $stage
Copy-Item (Join-Path $PSScriptRoot 'installer\uninstall.ps1') $stage
Copy-Item (Join-Path $PSScriptRoot 'installer\uninstall.cmd') $stage
$setup = Join-Path $artifactRoot "MyCapture-$Version-win-x64-setup.exe"
$sed = Join-Path $stage 'package.sed'
$source = $stage.TrimEnd('\') + '\'
@"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=MyCapture installation completed.
TargetName=$setup
FriendlyName=MyCapture $Version Setup
AppLaunched=install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=install.cmd
UserQuietInstCmd=install.cmd
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=$source
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
%FILE3%=
%FILE4%=
[Strings]
FILE0=payload.zip
FILE1=install.ps1
FILE2=install.cmd
FILE3=uninstall.ps1
FILE4=uninstall.cmd
"@ | Set-Content $sed -Encoding ASCII
$iexpress = Start-Process "$env:SystemRoot\System32\iexpress.exe" -ArgumentList '/N','/Q',$sed -Wait -PassThru
if ($iexpress.ExitCode -ne 0 -or -not (Test-Path $setup)) { throw "IExpress packaging failed: $($iexpress.ExitCode)" }
$inventory = Get-ChildItem $publish -File -Recurse | ForEach-Object {
    [pscustomobject]@{ Path=$_.FullName.Substring($publish.Length+1); Bytes=$_.Length; Sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash }
}
$deliverables = @($portable,$setup) | ForEach-Object {
    $f=Get-Item $_;[pscustomobject]@{ Path=$f.Name; Bytes=$f.Length; Sha256=(Get-FileHash $f.FullName -Algorithm SHA256).Hash }
}
[pscustomobject]@{ Product='MyCapture';Version=$Version;Runtime='win-x64';SelfContained=$true;Unsigned=$true;GeneratedUtc=[DateTime]::UtcNow.ToString('O');Deliverables=$deliverables;PublishInventory=$inventory } |
    ConvertTo-Json -Depth 5 | Set-Content (Join-Path $artifactRoot 'release-manifest.json') -Encoding UTF8
Remove-Item $stage -Recurse -Force
"SETUP=$setup";"PORTABLE=$portable";"FILES=$($inventory.Count)";"MANIFEST=$(Join-Path $artifactRoot 'release-manifest.json')"
