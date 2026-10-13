<#
.SYNOPSIS
    Builds PC Butler for Android: one signed APK (phones and the emulator).

.DESCRIPTION
    ./build-android.ps1 [-Bundle] [-Dotnet <path to dotnet.exe>] [-Keystore <file>] [-Out <folder>]

    -Bundle builds the Android App Bundle (.aab) that Google Play takes, instead of the APK.

    Needs the .NET SDK with the maui-android workload, the Android SDK (ANDROID_HOME) and a
    JDK (JAVA_HOME). The APK is signed with the keystore; its password is read from
    "<keystore>.password.txt" beside it, or from PCBUTLER_KEYSTORE_PASSWORD. Keep the
    keystore: every update must be signed with the same key, or Android refuses to install it.
#>
param(
    [switch]$Bundle,
    [string]$Dotnet = "",
    [string]$Keystore = "D:\Android\keys\pcbutler.keystore",
    [string]$Out = (Join-Path $PSScriptRoot "artifacts\android")
)
$ErrorActionPreference = "Stop"

if (-not $Dotnet) {
    $Dotnet = @("D:\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $env:JAVA_HOME) {
    $env:JAVA_HOME = Get-ChildItem "$env:ProgramFiles\Microsoft" -Directory -Filter "jdk-17*" | Select-Object -First 1 -ExpandProperty FullName
}
$password = $env:PCBUTLER_KEYSTORE_PASSWORD
if (-not $password -and (Test-Path "$Keystore.password.txt")) {
    $password = (Get-Content "$Keystore.password.txt" -Raw).Trim()
}
if (-not (Test-Path $Keystore) -or -not $password) {
    throw "The signing keystore or its password is missing ($Keystore)."
}

$project = Join-Path $PSScriptRoot "src\PCButler.Android\PCButler.Android.csproj"
$format = if ($Bundle) { "aab" } else { "apk" }
& $Dotnet publish $project -c Release -f net10.0-android -o $Out -p:AndroidPackageFormat=$format `
    -p:AndroidKeyStore=true -p:AndroidSigningKeyStore=$Keystore -p:AndroidSigningKeyAlias=pcbutler `
    -p:AndroidSigningKeyPass=$password -p:AndroidSigningStorePass=$password
if ($LASTEXITCODE) { throw "dotnet publish failed" }

$built = Get-ChildItem $Out -Filter "*-Signed.$format" | Select-Object -First 1
$target = Join-Path $Out "PCButler.$format"
Copy-Item $built.FullName $target -Force
Write-Host "Built $target ($([int]((Get-Item $target).Length / 1MB)) MB)"
