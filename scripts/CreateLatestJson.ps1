param(
    [string]$Version = "0.1.0",
    [string]$Repository = "daveranan/clipper"
)

$ErrorActionPreference = "Stop"

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$AppRoot = Split-Path -Parent $ScriptRoot
$BundleRoot = Join-Path $AppRoot "src-tauri\target\release\bundle"
$InstallerName = "QuickClipper_${Version}_x64-setup.exe"
$SignaturePath = Join-Path $BundleRoot "nsis\$InstallerName.sig"
$LatestPath = Join-Path $BundleRoot "latest.json"

if (-not (Test-Path $SignaturePath)) {
    throw "Signature not found: $SignaturePath"
}

$latest = [ordered]@{
    version = $Version
    notes = "QuickClipper $Version"
    pub_date = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    platforms = @{
        "windows-x86_64" = @{
            signature = (Get-Content $SignaturePath -Raw).Trim()
            url = "https://github.com/$Repository/releases/download/v$Version/$InstallerName"
        }
    }
}

$latest | ConvertTo-Json -Depth 8 | Set-Content -Path $LatestPath -Encoding UTF8
Write-Host "Wrote $LatestPath"
