<#
Copies the published Background Optimizer build to a stable per-user folder
and creates Start Menu + Desktop shortcuts, so you can launch the app
without digging into the build output every time.

Usage (from the repo root):
  dotnet publish BgOptimizer.csproj -c Release -r win-x64 --self-contained true -o publish
  powershell -ExecutionPolicy Bypass -File installer\Install-Shortcut.ps1

Why a copy+shortcut instead of a packaged installer .exe: Windows Smart App
Control (if enabled) hard-blocks brand-new unsigned installer executables,
with no per-app override available. The app's own already-built exe isn't
affected by that block - Smart App Control evaluates a binary by its own
hash/signature, and this script reuses that exact exe rather than wrapping
it in a new one, so nothing new gets flagged.
#>

param(
    [string]$SourceDir = (Join-Path $PSScriptRoot "..\publish"),
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\BgOptimizer")
)

$sourceExe = Join-Path $SourceDir "BgOptimizer.exe"
if (-not (Test-Path $sourceExe)) {
    Write-Error "BgOptimizer.exe not found in '$SourceDir'. Publish first: dotnet publish BgOptimizer.csproj -c Release -r win-x64 --self-contained true -o publish"
    exit 1
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Write-Host "Copying build to $InstallDir ..."
Copy-Item -Path (Join-Path $SourceDir "*") -Destination $InstallDir -Recurse -Force

$exePath = Join-Path $InstallDir "BgOptimizer.exe"
$shell = New-Object -ComObject WScript.Shell

$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$startMenuShortcut = $shell.CreateShortcut((Join-Path $startMenuDir "Background Optimizer.lnk"))
$startMenuShortcut.TargetPath = $exePath
$startMenuShortcut.WorkingDirectory = $InstallDir
$startMenuShortcut.IconLocation = $exePath
$startMenuShortcut.Save()
Write-Host "Start Menu shortcut created: $($startMenuShortcut.FullName)"

$desktopShortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath("Desktop")) "Background Optimizer.lnk"))
$desktopShortcut.TargetPath = $exePath
$desktopShortcut.WorkingDirectory = $InstallDir
$desktopShortcut.IconLocation = $exePath
$desktopShortcut.Save()
Write-Host "Desktop shortcut created: $($desktopShortcut.FullName)"

Write-Host ""
Write-Host "Done. Launch 'Background Optimizer' from the Start Menu or Desktop."
Write-Host "It will still prompt for admin every time - the app requires elevation to manage services and scheduled tasks."
