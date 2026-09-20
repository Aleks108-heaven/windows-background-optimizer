# Background Optimizer

A Windows 11 desktop app (WPF/.NET) for seeing and controlling what's running in the background: processes, startup apps, and Windows services — with built-in safeguards so it can't be used to disable something critical by accident.

## Features

### Processes tab
- Live list of all running processes with CPU % and memory usage, auto-refreshing every 2 seconds.
- **Kill Selected** — force-terminates the selected process (with a confirmation prompt).
- **Apply Priority** — sets a process's CPU scheduling priority (Idle, BelowNormal, Normal, AboveNormal, High) without killing it. `Idle` is the safest way to throttle something you don't want competing for CPU while keeping it running.
- Protected processes are flagged and cannot be killed or reprioritized (see **Safety** below).

### Startup Apps tab
- Lists every startup entry from the registry Run keys (current user and all users) and both Startup folders (yours and all-users).
- **Enable Selected** / **Disable Selected** — toggles whether an item launches at login. Uses the same mechanism Task Manager uses (a `StartupApproved` registry flag, or renaming shortcuts with a `.disabled` suffix), so it's fully reversible and doesn't delete anything.

### Services tab
- Lists all Windows services with their current status and startup type.
- **Set: Automatic / Manual / Disabled** — changes a service's startup type.
  - *Automatic*: starts at every boot.
  - *Manual*: only starts when something actually needs it — the safest way to reduce background load without losing the feature.
  - *Disabled*: never starts, even if something depends on it. This is the one destructive option, which is why it prompts for confirmation.
- **Stop Now** — stops a running service immediately.

### Activity Log
A running log at the bottom of the window records every action taken (or blocked), with a timestamp.

### Multi-language UI
Switch languages live from the dropdown in the top-right — no restart needed. Supported: English, Ukrainian, Spanish, German, Italian, Polish, Russian. Every label, button, column header, dialog, and log message is translated.

### System tray
Minimizing the window sends it to the system tray (look for the blue "B" icon — Windows may tuck new tray icons behind the `^` "show hidden icons" arrow near the clock the first time). Double-click the tray icon, or right-click → **Open**, to restore it. Right-click → **Exit** fully quits the app.

## Safety

This app runs elevated (admin) because managing services and killing other processes requires it — which also means it *could* be used to break things if you're not careful. Two layers of protection:

1. **Protected list** ([Services/ProtectedItems.cs](Services/ProtectedItems.cs)) — hard-blocks changes to ~40 core Windows processes and services (`csrss`, `lsass`, `svchost`, `explorer`, `RpcSs`, `WinDefend`, `TrustedInstaller`, Windows Defender's engine and related services, etc.). These show as "Protected" in the grids and every action against them is refused, not just warned.
2. **Confirmation dialogs** — killing a process, disabling a startup item, disabling a service, or stopping a service all prompt for confirmation before acting.

The protected list can't know about every third-party or OEM (e.g. laptop vendor) service, so exercise the same judgment yourself there — prefer **Manual** over **Disabled** for anything you're not 100% sure about, and change one thing at a time.

## Requirements

**To just run a pre-built copy** (no dev tools needed):
- Windows 10 (64-bit, version 1809 or later) or Windows 11
- Administrator rights — the app self-elevates via UAC on every launch, so you'll get a UAC prompt each time
- No .NET runtime needs to be installed separately — see **Installing a pre-built copy** below
- Hardware requirements are trivial: the app itself typically uses well under 1% CPU at idle and roughly 100–300 MB RAM. Any 64-bit Windows 10/11 laptop from the last decade or so is more than enough — an Intel Core i5 with 16 GB RAM is comfortably above what's needed.

**To build from source:**
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows (WPF only builds/runs on Windows)

## Installing a pre-built copy

Every push to `master`/`main` runs the CI workflow ([.github/workflows/build.yml](.github/workflows/build.yml)), which publishes a **self-contained** build — it bundles its own copy of the .NET runtime, so the machine you install it on doesn't need .NET installed at all.

1. Go to the repo's **Actions** tab on GitHub and open the latest successful **Build** run (you'll need a GitHub account signed in to download artifacts — this is a GitHub requirement, not specific to this repo).
2. Download the `BgOptimizer` artifact (a `.zip`) and extract it. The extracted folder contains `BgOptimizer.exe` plus its bundled runtime files — keep them together, all in the same folder.
3. Copy that whole folder to the target laptop (USB drive, file share, etc.) — no installer needed.
4. Run `BgOptimizer.exe`. Expect two prompts on first launch, both expected and safe to accept:
   - **Windows SmartScreen** ("Windows protected your PC") — the exe isn't code-signed, so files downloaded from the internet trigger this the first time. Click **More info** → **Run anyway**.
   - **UAC** (User Account Control) — the app needs admin rights to manage services and kill other processes. Click **Yes**.

If a security tool (Defender or a laptop OEM's antivirus) flags it heuristically because it can kill processes and reconfigure services, that's expected for this class of tool — check **Safety** above for what's actually locked down.

For wider distribution than "hand someone a folder," a tagged GitHub Release with the zip attached as a release asset would let anyone download it without needing repo access or a GitHub login — ask if you'd like that set up.

## Build & run from source

```bash
dotnet build
dotnet run
```

Or run the compiled executable directly: `bin/Debug/net10.0-windows/BgOptimizer.exe`.

## Project structure

```
MainWindow.xaml(.cs)        Main UI and event handlers
Models/                     Data models (ProcessEntry, StartupEntry, ServiceEntry)
Services/
  ProcessMonitor.cs         Process enumeration, CPU% sampling, kill/priority
  StartupManager.cs         Startup item enumeration and enable/disable
  SvcManager.cs              Windows service enumeration and start-type/stop control (via WMI)
  ProtectedItems.cs         The safety deny-lists
Localization/
  Loc.cs                    Translation tables + live language-switching
  ActionResult.cs           Localized result type returned by service actions
icon.ico                    App icon (exe, window title bar, and tray all share this)
.github/workflows/build.yml CI: builds and publishes on every push/PR
```

## CI/CD

Every push and pull request triggers a GitHub Actions build on `windows-latest` (required since this is a WPF app), which restores, builds, and publishes a self-contained `win-x64` build, uploading it as a downloadable artifact from the run. See **Installing a pre-built copy** above for how to use it.
