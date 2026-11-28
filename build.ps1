# SPDX-License-Identifier: LicenseRef-PCButler-Proprietary
# Copyright (c) 2026 hmodymac8-droid
<#
Build PCButlerSetup.exe, a standard Windows setup wizard.

Needs:  the .NET 10 SDK
        Rust (https://rustup.rs, the stable x86_64-pc-windows-msvc toolchain) for the search service
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
$agent = [regex]::Match((Get-Content (Join-Path $PSScriptRoot "src\PCButler.Agent\PCButler.Agent.csproj") -Raw),
                        '<Version>([^<]+)</Version>').Groups[1].Value
if (-not $version -or $version -ne $project -or $version -ne $agent) {
    throw "AppInfo.Version ($version) and the <Version> in PCButler.csproj ($project) and PCButler.Agent.csproj ($agent) must be the same."
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

function Find-Cargo {
    $candidates = @((Get-Command cargo.exe -ErrorAction SilentlyContinue).Source,
                    $(if ($env:CARGO_HOME) { Join-Path $env:CARGO_HOME "bin\cargo.exe" }),
                    "$env:USERPROFILE\.cargo\bin\cargo.exe",
                    "$env:USERPROFILE\.rustup\toolchains\stable-x86_64-pc-windows-msvc\bin\cargo.exe")
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    throw "Rust (cargo) was not found. The search service, PCButlerSearch.exe, is written in Rust: install it from https://rustup.rs " +
          "(the stable x86_64-pc-windows-msvc toolchain, which needs the Visual Studio C++ build tools), then run build.ps1 again."
}

# 0. The search service (src/PCButler.SearchService, Rust). Its SHA-256 goes into PC Butler, whose
#    administrator helper installs only that exact file (SearchServiceSetup).
$service = Join-Path $PSScriptRoot "src\PCButler.SearchService"
$cargo = Find-Cargo
$env:PATH = (Split-Path $cargo) + ";" + $env:PATH  # rustc beside cargo, when cargo is not on PATH
Push-Location $service  # its .cargo\config.toml (static C runtime) is read from the current folder
try {
    & $cargo build --release --locked
    if ($LASTEXITCODE) { throw "cargo build of the search service failed" }
} finally {
    Pop-Location
}
$serviceExe = Join-Path $service "target\release\PCButlerSearch.exe"
$serviceHash = (Get-FileHash $serviceExe -Algorithm SHA256).Hash

# 1. The program itself, with the .NET runtime included.
$dist = Join-Path $Work "dist"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
# ReadyToRun: the code is compiled here, not on the user's PC at every start, so the program and its window open sooner.
& (Find-Dotnet) publish (Join-Path $PSScriptRoot "src\PCButler") -c Release -r win-x64 --self-contained `
    -p:DebugType=none -p:PublishReadyToRun=true -p:SearchServiceSha256=$serviceHash -o $dist
if ($LASTEXITCODE) { throw "dotnet publish failed" }
# The background agent, PCButler.Agent.exe (no WPF), beside it, sharing the same runtime.
& (Find-Dotnet) publish (Join-Path $PSScriptRoot "src\PCButler.Agent") -c Release -r win-x64 --self-contained `
    -p:DebugType=none -p:PublishReadyToRun=true -p:SearchServiceSha256=$serviceHash -o $dist
if ($LASTEXITCODE) { throw "dotnet publish of the agent failed" }
# The service's program travels with PC Butler; the helper copies it to Program Files when the user switches it on.
Copy-Item $serviceExe (Join-Path $dist "PCButlerSearch.exe")

# Each tool has a program file of its own: a copy of PCButler.exe under the tool's name, which runs
# the same PCButler.dll beside it (see Tools.ExeNames). Courier stays in PCButler.exe, which the firewall rule names.
foreach ($name in "Search", "Sorter", "Duplicates", "Storage", "Cleaner", "Programs", "Shredder", "Booster", "Benchmark",
                  "Internet", "ScanLAN", "Clipboard", "Vault", "History", "Settings", "Help", "QuickSearch") {
    Copy-Item (Join-Path $dist "PCButler.exe") (Join-Path $dist "PCButler.$name.exe")
}

# 2. The setup wizard, compiled from installer.iss with Inno Setup.
$arguments = @("/DAppVersion=$version", "/DDistDir=$dist", "/O$PSScriptRoot")
if ($Test) { $arguments += "/DTestBuild" }
& (Find-Iscc) @arguments (Join-Path $PSScriptRoot "installer\installer.iss")
if ($LASTEXITCODE) { throw "Inno Setup failed" }
Write-Host "Built $(Join-Path $PSScriptRoot 'PCButlerSetup.exe') version $version"
