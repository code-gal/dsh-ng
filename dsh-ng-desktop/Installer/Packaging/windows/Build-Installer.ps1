[CmdletBinding()]
param(
    [ValidateSet('Aot', 'DotNet')]
    [string]$Mode = 'Aot',
    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$CertificateThumbprint,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot '..\..\..\artifacts\installer'
}

function Invoke-Checked {
    param(
        [string[]]$Arguments,
        [string]$FailureMessage
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
}

function Get-RelativePayloadPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RootDirectory,
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    # Windows PowerShell 5.1 runs on .NET Framework and therefore does not
    # expose System.IO.Path.GetRelativePath. Keep the package script usable
    # from the built-in PowerShell shipped with supported Windows versions.
    $trimCharacters = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar
    )
    $root = [System.IO.Path]::GetFullPath($RootDirectory).TrimEnd($trimCharacters) + [System.IO.Path]::DirectorySeparatorChar
    $candidate = [System.IO.Path]::GetFullPath($FilePath)
    if (-not $candidate.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The published file is outside the expected payload directory: $FilePath"
    }

    return $candidate.Substring($root.Length).Replace('\', '/')
}

function Get-PayloadManifest {
    param(
        [string]$PublishDirectory,
        [bool]$RequiresDotNetDesktopRuntime
    )

    $payloadFiles = @(
        Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse |
            Where-Object { $_.Extension -ne '.pdb' } |
            Sort-Object FullName
    )
    if ($payloadFiles.Count -eq 0) {
        throw 'The client publish output is empty.'
    }

    $manifestFiles = @(
        foreach ($file in $payloadFiles) {
            $relativePath = Get-RelativePayloadPath -RootDirectory $PublishDirectory -FilePath $file.FullName
            [ordered]@{
                RelativePath = $relativePath
                Length = [Int64]$file.Length
                Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    )
    $mainExecutable = 'DshNgDesktop.exe'
    if (-not ($manifestFiles.RelativePath -contains $mainExecutable)) {
        throw 'The client publish output does not contain DshNgDesktop.exe.'
    }

    # The manifest declares the entire non-debug publish closure as required.
    # SetupHost validates length and SHA-256 for every entry before it can launch
    # the Avalonia installer, including the primary EXE and native libraries.
    return [ordered]@{
        SchemaVersion = 1
        RequiresDotNetDesktopRuntime = $RequiresDotNetDesktopRuntime
        RequiredDotNetDesktopMajorVersion = 10
        MainExecutableRelativePath = $mainExecutable
        RequiredLaunchFiles = @($manifestFiles.RelativePath)
        Files = $manifestFiles
    }
}

$normalizedVersion = $Version.Trim()
if ($normalizedVersion.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
    $normalizedVersion = $normalizedVersion.Substring(1)
}
if ($normalizedVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw 'Version must be SemVer, for example 0.9.1 or v0.9.1.'
}

$releaseVersion = "v$normalizedVersion"
$flavor = if ($Mode -eq 'Aot') { 'aot' } else { 'dotnet' }
$selfContained = if ($Mode -eq 'Aot') { 'true' } else { 'false' }
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$desktopProject = Join-Path $projectRoot 'DshNgDesktop.csproj'
$setupHostProject = Join-Path $projectRoot 'DshDesktop.SetupHost\DshDesktop.SetupHost.csproj'
$nuGetConfig = Join-Path $projectRoot 'NuGet.Config'
$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("dsh-desktop-setuphost-" + [Guid]::NewGuid().ToString('N'))
$clientPublishDirectory = Join-Path $workRoot 'client-publish'
$payloadArchive = Join-Path $workRoot 'client-payload.zip'
$payloadManifest = Join-Path $workRoot 'client-payload.manifest.json'
$setupHostPublishDirectory = Join-Path $workRoot 'setup-host-publish'

try {
    New-Item -ItemType Directory -Path $clientPublishDirectory, $setupHostPublishDirectory, $OutputDirectory -Force | Out-Null

    Invoke-Checked -Arguments @(
        'restore', $desktopProject,
        '-r', $RuntimeIdentifier,
        '--configfile', $nuGetConfig
    ) -FailureMessage "dotnet restore failed for the $Mode desktop payload."

    Invoke-Checked -Arguments @(
        'publish', $desktopProject,
        '-c', 'Release',
        '-r', $RuntimeIdentifier,
        '--no-restore',
        "-p:DshPublishMode=$Mode",
        "-p:SelfContained=$selfContained",
        '-p:PublishSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $clientPublishDirectory
    ) -FailureMessage "dotnet publish failed for the $Mode desktop payload."

    Get-ChildItem -LiteralPath $clientPublishDirectory -Filter '*.pdb' -File -Recurse |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

    $manifest = Get-PayloadManifest -PublishDirectory $clientPublishDirectory -RequiresDotNetDesktopRuntime ($Mode -eq 'DotNet')
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $payloadManifest -Encoding utf8
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $clientPublishDirectory,
        $payloadArchive,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    Invoke-Checked -Arguments @(
        'restore', $setupHostProject,
        '-r', $RuntimeIdentifier,
        '--configfile', $nuGetConfig
    ) -FailureMessage 'dotnet restore failed for the Native AOT SetupHost.'

    Invoke-Checked -Arguments @(
        'publish', $setupHostProject,
        '-c', 'Release',
        '-r', $RuntimeIdentifier,
        '--no-restore',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:DshSetupPayloadArchive=$payloadArchive",
        "-p:DshSetupPayloadManifest=$payloadManifest",
        '-o', $setupHostPublishDirectory
    ) -FailureMessage 'dotnet publish failed for the Native AOT SetupHost.'

    $setupHostExecutable = Join-Path $setupHostPublishDirectory 'DshDesktop.SetupHost.exe'
    if (-not (Test-Path -LiteralPath $setupHostExecutable -PathType Leaf)) {
        throw 'The Native AOT SetupHost publish did not contain DshDesktop.SetupHost.exe.'
    }

    $installerName = "DSH-Desktop-Setup-$releaseVersion-$RuntimeIdentifier-$flavor.exe"
    $installerPath = Join-Path (Resolve-Path -LiteralPath $OutputDirectory).Path $installerName
    Copy-Item -LiteralPath $setupHostExecutable -Destination $installerPath -Force

    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        & signtool.exe sign /sha1 $CertificateThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $installerPath
        if ($LASTEXITCODE -ne 0) {
            throw 'Authenticode signing failed for the final SetupHost single-file EXE.'
        }
    }
    else {
        Write-Warning 'No CertificateThumbprint was supplied. This artifact is unsigned. It may be published only as a clearly labeled Windows Community Preview with its SHA-256 and SmartScreen warning; never ask users to import a root certificate.'
    }

    $hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$installerPath.sha256" -Value "$hash *$installerName" -Encoding ascii
    Write-Host "Created installer: $installerPath"
    Write-Host "Created checksum: $installerPath.sha256"
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
