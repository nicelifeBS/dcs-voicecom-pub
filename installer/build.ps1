# Builds the DCS VoiceCom installer: publishes the app (self-contained, so no .NET runtime is needed on the
# target machine), then compiles installer\DcsVoiceCom.iss with Inno Setup 6.
# Output: dist\DcsVoiceCom-Setup-<version>.exe
#
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-Version 0.1.0-beta.5]
#
# -Version (the Installer workflow passes the v* tag without the "v") sets the app and installer version.
# Without it, <Version> from Directory.Build.props is used.
param(
    [string]$Version = "",
    [string]$Iscc = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$env:DOTNET_NOLOGO = "1"
# Last line only: a first run of dotnet can print a welcome banner before the value.
$version = $Version.Trim().TrimStart('v')
if (-not $version) { $version = (dotnet msbuild src\DcsVoiceCom.App -getProperty:Version | Select-Object -Last 1).Trim() }
$publishDir = Join-Path $root "dist\DcsVoiceCom"
Write-Host "DCS VoiceCom $version"

if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish src\DcsVoiceCom.App -c Release -r win-x64 --self-contained true -o $publishDir "-p:Version=$version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

if (-not $Iscc) {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { $Iscc = $cmd.Source }
    else {
        $Iscc = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
            "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}
if (-not $Iscc) { throw "Inno Setup 6 not found. Install it (winget install JRSoftware.InnoSetup) or pass -Iscc <path to ISCC.exe>." }

& $Iscc "/DAppVersion=$version" "/DPublishDir=$publishDir" installer\DcsVoiceCom.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Write-Host "Installer: dist\DcsVoiceCom-Setup-$version.exe"
