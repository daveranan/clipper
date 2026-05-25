param(
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppRoot = Split-Path -Parent $ScriptRoot
$PrivateKeyPath = Join-Path $AppRoot "src-tauri\tauri-updater.key"
$PrivateKeyPasswordPath = Join-Path $AppRoot "src-tauri\tauri-updater.password"
$TauriConfigPath = Join-Path $AppRoot "src-tauri\tauri.conf.json"
$Version = (Get-Content $TauriConfigPath -Raw | ConvertFrom-Json).version
$BundleRoot = Join-Path $AppRoot "src-tauri\target\release\bundle"
$Installer = Join-Path $BundleRoot "nsis\QuickClipper_${Version}_x64-setup.exe"

Set-Location $AppRoot

if (Test-Path $PrivateKeyPath) {
    $env:TAURI_SIGNING_PRIVATE_KEY = Get-Content $PrivateKeyPath -Raw
}
if (Test-Path $PrivateKeyPasswordPath) {
    $env:TAURI_SIGNING_PRIVATE_KEY_PASSWORD = Get-Content $PrivateKeyPasswordPath -Raw
}

npm install
npm run tauri build
& (Join-Path $ScriptRoot "CreateLatestJson.ps1") -Version $Version -Repository "daveranan/clipper"

Start-Process explorer.exe $BundleRoot

if (-not $SkipInstall) {
    if (-not (Test-Path $Installer)) {
        throw "Installer not found: $Installer"
    }

    Start-Process -FilePath $Installer -Wait

    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "QuickClipper\QuickClipper.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\QuickClipper\QuickClipper.exe"),
        (Join-Path $env:ProgramFiles "QuickClipper\QuickClipper.exe")
    )

    $app = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($app) {
        Start-Process -FilePath $app
    } else {
        Start-Process -FilePath (Join-Path $AppRoot "src-tauri\target\release\quickclipper.exe")
    }
}
