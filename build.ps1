# SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
# Copyright (c) 2026 hmodymac8-droid
<#
Build PCButlerSetup.exe, a standard Windows setup wizard.

Needs:  the .NET 10 SDK
        Inno Setup 6 (https://jrsoftware.org/isinfo.php, or: winget install JRSoftware.InnoSetup)

    ./build.ps1 [-Work <folder>] [-Dotnet <path to dotnet.exe>] [-Test]

The program is published self-contained (no .NET needed on the user's PC) into
Work\dist (default: artifacts\dist), and the finished installer is written next to
this script. -Test builds the TestBuild variant of the installer (see installer.iss).
#>
param(
    [string]$Work = (Join-Path $PSScriptRoot "artifacts"),
    [string]$Dotnet = "",
    [switch]$Test
)
$ErrorActionPreference = "Stop"

# The version lives in AppInfo.Version and the app's project file; they must agree.
$version = [regex]::Match((Get-Content (Join-Path $PSScriptRoot "src\PCButler.Core\AppInfo.cs") -Raw),
                          'Version = "([^"]+)"').Groups[1].Value
$project = [regex]::Match((Get-Content (Join-Path $PSScriptRoot "src\PCButler\PCButler.csproj") -Raw),
                          '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $version -or $version -ne $project) {
    throw "AppInfo.Version ($version) and the <Version> in PCButler.csproj ($project) must be the same."
}

function Find-Dotnet {
    if ($Dotnet) { return $Dotnet }
    foreach ($candidate in @($env:DOTNET_ROOT, "$env:ProgramFiles\dotnet")) {
        if ($candidate -and (Test-Path (Join-Path $candidate "dotnet.exe"))) {
            $exe = Join-Path $candidate "dotnet.exe"
            if ((& $exe --list-sdks) -match "^10\.") { return $exe }
        }
    }
    return "dotnet"
}

function Find-Iscc {
    $candidates = @((Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
                    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
                    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
                    "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    throw "Inno Setup 6 was not found. Install it with:  winget install JRSoftware.InnoSetup"
}

# 1. The program itself, with the .NET runtime included.
$dist = Join-Path $Work "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
& (Find-Dotnet) publish (Join-Path $PSScriptRoot "src\PCButler") -c Release -r win-x64 --self-contained `
    -p:DebugType=none -o $dist
if ($LASTEXITCODE) { throw "dotnet publish failed" }

# 2. The setup wizard, compiled from installer.iss with Inno Setup.
$arguments = @("/DAppVersion=$version", "/DDistDir=$dist", "/O$PSScriptRoot")
if ($Test) { $arguments += "/DTestBuild" }
& (Find-Iscc) @arguments (Join-Path $PSScriptRoot "installer\installer.iss")
if ($LASTEXITCODE) { throw "Inno Setup failed" }
Write-Host "Built $(Join-Path $PSScriptRoot 'PCButlerSetup.exe') version $version"
