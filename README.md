# Background Optimizer

A Windows 11 desktop app (WPF/.NET) for seeing and controlling what's running in the background: processes, startup apps, and Windows services — with built-in safeguards so it can't be used to disable something critical by accident.

## Features

### Processes tab
- Live list of all running processes with CPU % and memory usage, auto-refreshing every 2 seconds.
- **Kill Selected** — force-terminates the selected process (with a confirmation prompt).
- **Apply Priority** — sets a process's CPU scheduling priority (Idle, BelowNormal, Normal, AboveNormal, High) without killing it. `Idle` is the safest way to throttle something you don't want competing for CPU while keeping it running.
- **Persist across restarts** — a checkbox next to Apply Priority. Checked, it writes the chosen priority to the registry under Image File Execution Options (the same mechanism Task Manager's own "Efficiency Mode" uses), so Windows re-applies it every time that exe launches, not just until the next reboot. Unchecked, applying just changes the live process as before, and clears any persistent rule that exe previously had. Two things worth knowing: it matches by the exe's bare filename, so it affects every instance of that exe system-wide, not just the one selected in the grid; and this registry area is also how debuggers attach to a process at launch, so some security software watches writes to it.
- Protected processes are flagged and cannot be killed or reprioritized (see **Safety** below).

### Startup Apps tab
- Lists every startup entry from the registry Run keys (current user and all users), both Startup folders (yours and all-users), and Scheduled Tasks — the mechanism most OEM bundleware (ASUS, Dell, HP, ...) actually uses to relaunch its background agents, which Run keys and the Startup folder never see. Tasks under `\Microsoft\...` are excluded on purpose (that's ~300+ Windows-internal plumbing tasks on a typical machine; surfacing those would bury the handful of OEM/third-party ones you actually came here to manage).
- **Enable Selected** / **Disable Selected** — toggles whether an item launches at login. Uses the same mechanism Task Manager uses for Run-key/Startup-folder entries (a `StartupApproved` registry flag, or renaming shortcuts with a `.disabled` suffix) and `schtasks.exe` for Scheduled Tasks, so it's fully reversible and doesn't delete anything.
- **Category** column — a best-effort hint (Telemetry / Core / Developer Tool / Vendor-Diagnostics / Uncategorized) based on name patterns, to help you decide what's safe to touch. It's a suggestion, not a verdict — the app never selects or acts on items based on it, and the same task can be safe for one person and load-bearing for another.

### Services tab
- Lists all Windows services with their current status, startup type, and the same best-effort **Category** hint as Startup Apps.
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

This app runs elevated (admin) because managing services and killing other processes requires it — which also means it *could* be used to break things if you're not careful. Several layers of protection:

1. **Protected list** ([Services/ProtectedItems.cs](Services/ProtectedItems.cs)) — hard-blocks changes to ~40 core Windows processes and services (`csrss`, `lsass`, `svchost`, `explorer`, `RpcSs`, `WinDefend`, `TrustedInstaller`, Windows Defender's engine and related services, etc.). These show as "Protected" in the grids and every action against them is refused, not just warned.
2. **Confirmation dialogs** — killing a process, disabling a startup item, disabling a service, or stopping a service all prompt for confirmation before acting.
3. **Restore Point safety net** — the "Auto restore point before risky changes" checkbox (Startup Apps tab, on by default) creates a System Restore point via the `SystemRestore` WMI class before Disable Startup, Set: Disabled, or Stop Now runs. There's also a manual **Create Restore Point** button for creating one on demand. This is best-effort: if System Restore is off for the system drive, or Windows' own once-per-24-hours throttle kicks in, the app logs it and proceeds anyway rather than blocking your actual request.
4. **Undo Last Change** — reverts the most recent reversible action (startup enable/disable, service start-type change, or service stop) from a session-only LIFO stack. It isn't persisted across app restarts — closing the app and reopening Startup Apps/Services to set something back by hand is equivalent.

The protected list can't know about every third-party or OEM (e.g. laptop vendor) service, so exercise the same judgment yourself there — prefer **Manual** over **Disabled** for anything you're not 100% sure about, change one thing at a time, and lean on the Category hint and Restore Point button above for anything you're unsure about.

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

**Windows Smart App Control**, if enabled (Settings → Privacy & security → Windows Security → App & browser control), hard-blocks brand-new unsigned executables with no override — this specifically affects a packaged installer `.exe`, since it's new and has no run history. It does *not* affect `BgOptimizer.exe` once it's run successfully once, since Smart App Control evaluates a binary by its own content, not by whether it's "an installer." This is why this repo ships a shortcut-creating PowerShell script instead of a traditional installer — see below.

For wider distribution than "hand someone a folder," a tagged GitHub Release with the zip attached as a release asset would let anyone download it without needing repo access or a GitHub login — ask if you'd like that set up.

### Shortcut installer

[installer/Install-Shortcut.ps1](installer/Install-Shortcut.ps1) copies a published build to `%LocalAppData%\Programs\BgOptimizer` and creates Start Menu + Desktop shortcuts, so you don't have to run the exe from the build/publish folder every time:

```powershell
dotnet publish BgOptimizer.csproj -c Release -r win-x64 --self-contained true -o publish
powershell -ExecutionPolicy Bypass -File installer\Install-Shortcut.ps1
```

The app still prompts for admin on every launch either way — that's the manifest (`app.manifest`), not the shortcut.

## Build & run from source

```bash
dotnet build
dotnet run
```

Or run the compiled executable directly: `bin/Debug/net10.0-windows/BgOptimizer.exe`.

## Project structure

```
MainWindow.xaml(.cs)        Main UI and event handlers
Models/                     Data models (ProcessEntry, StartupEntry, ServiceEntry, ItemCategory)
Services/
  ProcessMonitor.cs         Process enumeration, CPU% sampling, kill/priority
  StartupManager.cs         Startup item enumeration and enable/disable (Run keys, Startup folders, Scheduled Tasks)
  SvcManager.cs              Windows service enumeration and start-type/stop/start control (via WMI)
  ProtectedItems.cs         The safety deny-lists
  ItemClassifier.cs         Best-effort Category labeling for Startup Apps/Services
  PriorityPersistence.cs    Persistent process priority via Image File Execution Options
  SystemRestoreService.cs   System Restore point creation (via the SystemRestore WMI class)
Localization/
  Loc.cs                    Translation tables + live language-switching
  ActionResult.cs           Localized result type returned by service actions
installer/
  Install-Shortcut.ps1      Copies a published build + creates Start Menu/Desktop shortcuts
icon.ico                    App icon (exe, window title bar, and tray all share this)
.github/workflows/build.yml CI: builds and publishes on every push/PR
```

## CI/CD

Every push and pull request triggers a GitHub Actions build on `windows-latest` (required since this is a WPF app), which restores, builds, and publishes a self-contained `win-x64` build, uploading it as a downloadable artifact from the run. See **Installing a pre-built copy** above for how to use it.
