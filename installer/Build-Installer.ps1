<#
.SYNOPSIS
Publishes Deep Groove and builds its Windows installer with Inno Setup 7.

.DESCRIPTION
Run from any directory. The script verifies that WaveLab.csproj and WaveLab.iss carry the same
version, runs the installer-version test, rebuilds the normal Visual Studio Release program,
creates a clean self-contained win-x64 publish, locates ISCC.exe under Program Files, and verifies
both release outputs and the resulting installer.

.PARAMETER IsccPath
Optional explicit path to the Inno Setup 7 command-line compiler.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File installer\Build-Installer.ps1

.EXAMPLE
pwsh -File installer/Build-Installer.ps1 -IsccPath 'C:\Program Files\Inno Setup 7\ISCC.exe'
#>
[CmdletBinding()]
param(
    [string] $IsccPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installerDirectory = $PSScriptRoot
$repositoryRoot = Split-Path -Parent $installerDirectory
$projectPath = Join-Path $repositoryRoot 'src\WaveLab\WaveLab.csproj'
$innoScriptPath = Join-Path $installerDirectory 'WaveLab.iss'
$publishDirectory = Join-Path $repositoryRoot 'artifacts\publish'
$buildDirectory = Join-Path $repositoryRoot 'artifacts\build'
$releaseExecutable = Join-Path $repositoryRoot 'src\WaveLab\bin\Release\net10.0-windows\WaveLab.exe'

$projectText = [System.IO.File]::ReadAllText($projectPath)
$innoText = [System.IO.File]::ReadAllText($innoScriptPath)
$projectMatch = [regex]::Match($projectText, '<Version>\s*([^<]+?)\s*</Version>')
$innoMatch = [regex]::Match($innoText, '#define\s+MyAppVersion\s+"([^"]+)"')
if (-not $projectMatch.Success) { throw "No <Version> was found in $projectPath." }
if (-not $innoMatch.Success) { throw "No MyAppVersion was found in $innoScriptPath." }

$projectVersion = $projectMatch.Groups[1].Value.Trim()
$installerVersion = $innoMatch.Groups[1].Value.Trim()
if ($projectVersion -ne $installerVersion) {
    throw "Version mismatch: the project is $projectVersion but the installer is $installerVersion."
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $isccCandidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($env:ProgramFiles)) {
        $isccCandidates.Add((Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'))
    }
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $isccCandidates.Add((Join-Path $programFilesX86 'Inno Setup 7\ISCC.exe'))
    }
    $IsccPath = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Inno Setup 7 ISCC.exe was not found. Install it under Program Files or pass -IsccPath.'
}

try {
    $isccVersionOutput = @(& $IsccPath --version 2>&1)
    $isccVersionExitCode = $LASTEXITCODE
}
catch {
    throw "Building Deep Groove requires Inno Setup 7; $IsccPath could not report its compiler version."
}
$reportedIsccVersion = ($isccVersionOutput | Out-String).Trim()
if ($isccVersionExitCode -ne 0 -or $reportedIsccVersion -notmatch '^7(?:\.|$)') {
    if ([string]::IsNullOrWhiteSpace($reportedIsccVersion)) {
        $reportedIsccVersion = 'an unknown version'
    }
    throw "Building Deep Groove requires Inno Setup 7; $IsccPath reports $reportedIsccVersion."
}

$expectedInstaller = Join-Path $installerDirectory "Output\DeepGroove-Setup-$installerVersion.exe"

Push-Location $repositoryRoot
try {
    & dotnet test 'tests\WaveLab.Tests\WaveLab.Tests.csproj' --no-restore `
        --filter 'FullyQualifiedName~InstallerVersionTests'
    if ($LASTEXITCODE -ne 0) { throw "Installer version validation failed with exit code $LASTEXITCODE." }

    # Keep the ordinary Visual Studio Release output current as well as the private payload used by
    # Inno Setup. Rebuild (rather than an incremental build) also refreshes the app host timestamp,
    # making it unambiguous which program belongs to this installer run.
    & dotnet build $projectPath -c Release --no-restore --target Rebuild
    if ($LASTEXITCODE -ne 0) { throw "Visual Studio Release build failed with exit code $LASTEXITCODE." }

    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }

    & dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
        -o $publishDirectory --artifacts-path $buildDirectory
    if ($LASTEXITCODE -ne 0) { throw "Release publish failed with exit code $LASTEXITCODE." }

    if (Test-Path -LiteralPath $expectedInstaller) {
        Remove-Item -LiteralPath $expectedInstaller -Force
    }

    & $IsccPath $innoScriptPath
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

$payloadPath = Join-Path $publishDirectory 'WaveLab.exe'
if (-not (Test-Path -LiteralPath $releaseExecutable -PathType Leaf)) {
    throw "The Visual Studio Release program was not produced at $releaseExecutable."
}
if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
    throw "The published application was not produced at $payloadPath."
}
if (-not (Test-Path -LiteralPath $expectedInstaller -PathType Leaf)) {
    throw "The installer was not produced at $expectedInstaller."
}

$releaseProgram = Get-Item -LiteralPath $releaseExecutable
$releaseVersion = $releaseProgram.VersionInfo.ProductVersion
$payloadVersion = (Get-Item -LiteralPath $payloadPath).VersionInfo.ProductVersion
$builtInstaller = Get-Item -LiteralPath $expectedInstaller
$builtInstallerVersion = $builtInstaller.VersionInfo.ProductVersion.Trim()
if (-not $releaseVersion.StartsWith($projectVersion, [StringComparison]::Ordinal)) {
    throw "The Visual Studio Release program reports $releaseVersion instead of $projectVersion."
}
if (-not $payloadVersion.StartsWith($projectVersion, [StringComparison]::Ordinal)) {
    throw "The payload reports $payloadVersion instead of $projectVersion."
}
if ($builtInstallerVersion -ne $installerVersion) {
    throw "The installer reports $builtInstallerVersion instead of $installerVersion."
}

$sha256 = (Get-FileHash -LiteralPath $expectedInstaller -Algorithm SHA256).Hash
Write-Host "Built Deep Groove $installerVersion"
Write-Host "Release program: $releaseExecutable"
Write-Host "Installer: $expectedInstaller"
Write-Host "Bytes: $($builtInstaller.Length)"
Write-Host "SHA-256: $sha256"
