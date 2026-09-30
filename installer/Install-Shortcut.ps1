<#
Installs the published Background Optimizer build to Program Files and creates
Start Menu + Desktop shortcuts for all users. Must be run as Administrator.

Why Program Files: the app runs elevated (app.manifest requires admin). If it lived
in a user-writable folder such as %LocalAppData%, any non-admin process running as
you could replace BgOptimizer.dll or a bundled runtime DLL and have it run as
administrator the next time you accept the UAC prompt. Program Files is writable
only by administrators, which closes that path.

Usage (from the repo root, in an elevated PowerShell):
  dotnet publish BgOptimizer.csproj -c Release -r win-x64 --self-contained true -o publish
  powershell -ExecutionPolicy Bypass -File installer\Install-Shortcut.ps1

Why a copy+shortcut instead of a packaged installer .exe: Windows Smart App
Control (if enabled) hard-blocks brand-new unsigned installer executables,
with no per-app override available. The app's own already-built exe isn't
affected by that block - Smart App Control evaluates a binary by its own
hash/signature, and this script reuses that exact exe rather than wrapping
it in a new one, so nothing new gets flagged.

The install is a clean replace (old files removed first) so no stale DLLs survive an
upgrade. A previous per-user install under %LocalAppData%\Programs\BgOptimizer, and
the per-user shortcuts that pointed at it, are removed.
#>

param(
    [string]$SourceDir = (Join-Path $PSScriptRoot "..\publish"),
    [string]$InstallDir = (Join-Path $env:ProgramFiles "BgOptimizer")
)

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Run this script from an elevated (Administrator) PowerShell. Installing to Program Files is what keeps the elevated app's files from being tampered with by non-admin processes."
    exit 1
}

$sourceExe = Join-Path $SourceDir "BgOptimizer.exe"
if (-not (Test-Path $sourceExe)) {
    Write-Error "BgOptimizer.exe not found in '$SourceDir'. Publish first: dotnet publish BgOptimizer.csproj -c Release -r win-x64 --self-contained true -o publish"
    exit 1
}

if (Get-Process -Name BgOptimizer -ErrorAction SilentlyContinue) {
    Write-Error "Background Optimizer is running. Exit it (tray icon -> Exit) and run this script again."
    exit 1
}

$programFiles = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
if (-not ([IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\').StartsWith($programFiles, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Warning "'$InstallDir' is outside Program Files. Make sure only Administrators can write there, or the elevated app can be tampered with."
}

# Clean replace so stale files from an older build never linger.
if (Test-Path $InstallDir) { Remove-Item -Path $InstallDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Write-Host "Copying build to $InstallDir ..."
Copy-Item -Path (Join-Path $SourceDir "*") -Destination $InstallDir -Recurse -Force

$exePath = Join-Path $InstallDir "BgOptimizer.exe"
$shell = New-Object -ComObject WScript.Shell

function New-AppShortcut([string]$Path) {
    $lnk = $shell.CreateShortcut($Path)
    $lnk.TargetPath = $exePath
    $lnk.WorkingDirectory = $InstallDir
    $lnk.IconLocation = $exePath
    $lnk.Save()
    Write-Host "Shortcut created: $Path"
}

$commonStartMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs"
New-AppShortcut (Join-Path $commonStartMenu "Background Optimizer.lnk")
New-AppShortcut (Join-Path ([Environment]::GetFolderPath("CommonDesktopDirectory")) "Background Optimizer.lnk")

# Remove the old per-user (user-writable) install and any per-user shortcuts pointing at it.
$oldDir = Join-Path $env:LOCALAPPDATA "Programs\BgOptimizer"
$oldShortcuts = @(
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Background Optimizer.lnk"),
    (Join-Path ([Environment]::GetFolderPath("Desktop")) "Background Optimizer.lnk")
)
foreach ($path in $oldShortcuts) {
    if ((Test-Path $path) -and ($shell.CreateShortcut($path).TargetPath -like (Join-Path $oldDir "*"))) {
        Remove-Item $path -Force
        Write-Host "Removed old shortcut: $path"
    }
}
if (Test-Path $oldDir) {
    Remove-Item -Path $oldDir -Recurse -Force
    Write-Host "Removed old user-writable install: $oldDir"
}

Write-Host ""
Write-Host "Done. Launch 'Background Optimizer' from the Start Menu or Desktop."
Write-Host "It will still prompt for admin every time - the app requires elevation to manage services and scheduled tasks."
