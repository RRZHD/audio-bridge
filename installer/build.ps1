# Builds the self-contained server exe and compiles the Inno Setup installer in one go.
# Usage: powershell -ExecutionPolicy Bypass -File installer\build.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$serverProject = Join-Path $root "pc-server\AudioBridge.Server"
$publishDir = Join-Path $serverProject "publish"


# UsbDk: the one-time USB driver the server needs to talk to the phone (Apache-2.0, signed by Red Hat).
# The installer bundles its MSI; fetch it once and verify the checksum.
$usbdkMsi = Join-Path $PSScriptRoot "third-party\UsbDk_1.0.22_x64.msi"
$usbdkSha256 = "91f6f695e1e13c656024e6d3b55620bf08d8835ef05ee0496935ba6bb62466a5"
if (-not (Test-Path $usbdkMsi) -or (Get-FileHash $usbdkMsi -Algorithm SHA256).Hash.ToLower() -ne $usbdkSha256) {
    Write-Host "==> Downloading UsbDk 1.0.22..." -ForegroundColor Cyan
    Invoke-WebRequest "https://github.com/daynix/UsbDk/releases/download/v1.00-22/UsbDk_1.0.22_x64.msi" -OutFile $usbdkMsi
    if ((Get-FileHash $usbdkMsi -Algorithm SHA256).Hash.ToLower() -ne $usbdkSha256) { throw "UsbDk MSI checksum mismatch" }
}


# Code signing (optional): installer\signing\codesign.pfx + codesign.pass (both gitignored; issued by the internal CA).
$signPfx = Join-Path $PSScriptRoot "signing\codesign.pfx"
$signPass = Join-Path $PSScriptRoot "signing\codesign.pass"
function Sign-File([string]$path) {
    if (-not (Test-Path $signPfx)) { Write-Host "(no signing certificate: $path left unsigned)" -ForegroundColor Yellow; return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1
    if (-not $signtool) { throw "signtool.exe not found (Windows SDK)" }
    $pass = (Get-Content $signPass -Raw).Trim()
    & $signtool.FullName sign /fd SHA256 /f $signPfx /p $pass /tr http://timestamp.digicert.com /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $path" }
}

Write-Host "==> Publishing self-contained server exe..." -ForegroundColor Cyan
dotnet publish $serverProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Sign-File (Join-Path $publishDir "AudioBridgeServer.exe")

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidate = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    if (Test-Path $candidate) { $iscc = $candidate } else {
        throw "ISCC.exe not found. Install Inno Setup 6: winget install JRSoftware.InnoSetup"
    }
} else {
    $iscc = $iscc.Source
}

Write-Host "==> Compiling installer..." -ForegroundColor Cyan
& $iscc "$PSScriptRoot\setup.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed" }
Sign-File (Join-Path $PSScriptRoot "output\AudioBridgeServer-Setup.exe")

Write-Host "==> Done: installer\output\AudioBridgeServer-Setup.exe" -ForegroundColor Green
