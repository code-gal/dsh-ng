[CmdletBinding()]
param(
    [ValidateSet('Aot', 'Compatibility')]
    [string]$Mode = 'Aot',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$CertificateThumbprint,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\..\artifacts\installer')
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$project = Join-Path $projectRoot 'DshNgDesktop.csproj'
$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-desktop-installer-" + [Guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $workRoot 'publish'
$iexpressLayout = Join-Path $workRoot 'iexpress'

function Invoke-Checked([scriptblock]$Command, [string]$FailureMessage) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

try {
    New-Item -ItemType Directory -Path $publishRoot, $iexpressLayout, $OutputDirectory -Force | Out-Null
    Invoke-Checked {
        dotnet publish $project -c Release -r $RuntimeIdentifier `
            -p:DshPublishMode=$Mode -p:PublishSingleFile=true -p:SelfContained=true `
            -o $publishRoot
    } "dotnet publish failed for $RuntimeIdentifier ($Mode)."

    $bootstrap = Get-ChildItem -LiteralPath $publishRoot -Filter 'DshNgDesktop.exe' -File | Select-Object -First 1
    if ($null -eq $bootstrap) {
        throw 'The self-contained publish did not contain DshNgDesktop.exe.'
    }

    Copy-Item -LiteralPath $bootstrap.FullName -Destination (Join-Path $iexpressLayout 'DshNgDesktop.exe')
    $payloadDirectory = Join-Path $workRoot 'payload'
    Copy-Item -LiteralPath $publishRoot -Destination $payloadDirectory -Recurse
    Compress-Archive -LiteralPath (Join-Path $payloadDirectory '*') -DestinationPath (Join-Path $iexpressLayout 'payload.zip') -CompressionLevel Optimal

    $commandFile = Join-Path $iexpressLayout 'setup-run.cmd'
    @'
@echo off
setlocal
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%~dp0payload.zip' -DestinationPath '%~dp0payload' -Force"
if errorlevel 1 exit /b 1
start "DSH Desktop Setup" "%~dp0DshNgDesktop.exe" --install --payload "%~dp0payload"
exit /b 0
'@ | Set-Content -LiteralPath $commandFile -Encoding Ascii

    $installerName = "DSH-Desktop-Setup-$Version-$RuntimeIdentifier.exe"
    $installerPath = Join-Path (Resolve-Path $OutputDirectory) $installerName
    $sedPath = Join-Path $workRoot 'installer.sed'
    @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=1
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$installerPath
FriendlyName=DSH Desktop Setup
AppLaunched=setup-run.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=0
[Strings]
FILE0="DshNgDesktop.exe"
FILE1="payload.zip"
FILE2="setup-run.cmd"
[SourceFiles]
SourceFiles0=$iexpressLayout\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
"@ | Set-Content -LiteralPath $sedPath -Encoding Ascii

    Invoke-Checked { & "$env:WINDIR\System32\iexpress.exe" /N $sedPath } 'IExpress could not create the Windows installer.'
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw 'IExpress completed without producing the expected installer file.'
    }

    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        Invoke-Checked {
            signtool.exe sign /sha1 $CertificateThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $installerPath
        } 'Authenticode signing failed.'
    }

    Get-FileHash -LiteralPath $installerPath -Algorithm SHA256 |
        ForEach-Object { "$($_.Hash.ToLowerInvariant()) *$installerName" } |
        Set-Content -LiteralPath "$installerPath.sha256" -Encoding Ascii
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
