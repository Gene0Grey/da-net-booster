# Fills desktop/DaNetBooster/tools with adb, hev-socks5-tunnel (+wintun.dll, msys-2.0.dll) and the phone APK.
# Run after building the phone app. CI passes the signed release APK; locally it defaults to the debug build.
param([string]$Apk)
$ErrorActionPreference = 'Stop'
$root  = Split-Path $PSScriptRoot
$tools = Join-Path $root 'desktop\DaNetBooster\tools'
$tmp   = Join-Path $env:TEMP 'danet-fetch'
New-Item -ItemType Directory -Force $tools, $tmp | Out-Null

function Fetch($url, $zip) {
    $out = Join-Path $tmp $zip
    curl.exe -fsSL -o $out $url
    if ($LASTEXITCODE) { throw "download failed: $url" }
    Expand-Archive $out (Join-Path $tmp ($zip -replace '\.zip$')) -Force
    Join-Path $tmp ($zip -replace '\.zip$')
}

$pt = Fetch 'https://dl.google.com/android/repository/platform-tools-latest-windows.zip' 'platform-tools.zip'
Copy-Item "$pt\platform-tools\adb.exe", "$pt\platform-tools\AdbWinApi.dll", "$pt\platform-tools\AdbWinUsbApi.dll" $tools

$hev = Fetch 'https://github.com/heiher/hev-socks5-tunnel/releases/download/2.18.0/hev-socks5-tunnel-win64.zip' 'hev.zip'
Copy-Item "$hev\hev-socks5-tunnel\*" $tools

$apk = if ($Apk) { $Apk } else { Join-Path $root 'android\app\build\outputs\apk\debug\app-debug.apk' }
if (Test-Path $apk) { Copy-Item $apk (Join-Path $tools 'DaNetBooster.apk') }
else { Write-Warning "APK not built yet: run android\gradlew assembleDebug, then re-run this script." }

Get-ChildItem $tools | Format-Table Name, Length
