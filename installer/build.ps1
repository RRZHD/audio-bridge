# Builds the self-contained server exe and compiles the Inno Setup installer in one go.
# Usage: powershell -ExecutionPolicy Bypass -File installer\build.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$serverProject = Join-Path $root "pc-server\AudioBridge.Server"
$publishDir = Join-Path $serverProject "publish"

Write-Host "==> Publishing self-contained server exe..." -ForegroundColor Cyan
dotnet publish $serverProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

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

Write-Host "==> Done: installer\output\AudioBridgeServer-Setup.exe" -ForegroundColor Green
