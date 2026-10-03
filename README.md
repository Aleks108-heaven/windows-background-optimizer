# Background Optimizer

A Windows 11 desktop app (WPF/.NET) for seeing and controlling what's running in the background: processes, startup apps, and Windows services — with built-in safeguards so it can't be used to disable something critical by accident.

## Features

### Processes tab

- Live list of all running processes with CPU % and memory usage, auto-refreshing every 2 seconds.
- CPU, memory and priority values are flagged by colour *and* a glyph/weight (▲ warn, ◆ danger and bold, ▼ low priority), so severity is readable without relying on colour.
- **Kill Selected** — force-terminates the selected process (with a confirmation prompt).
- **Apply Priority** — sets a process's CPU scheduling priority (Idle, BelowNormal, Normal, AboveNormal, High) without killing it. `Idle` is the safest way to throttle something you don't want competing for CPU while keeping it running.
- **Persist across restarts** — a checkbox next to Apply Priority. Checked, it writes the chosen priority to the registry under Image File Execution Options (the same mechanism Task Manager's own "Efficiency Mode" uses), so Windows re-applies it every time that exe launches, not just until the next reboot. Unchecked, applying just changes the live process as before, and clears any persistent rule that exe previously had. A persistent rule is never accepted for Idle/BelowNormal on an interactive app (browser, shell, ...), even with the override box ticked, and a refused rule is not re-applied in the background either. A saved rule is re-applied to new instances of that exe while the app runs; if one instance can't be changed (e.g. access denied) it is logged once and left alone rather than retried every 2 seconds. Two things worth knowing: it matches by the exe's bare filename, so it affects every instance of that exe system-wide, not just the one selected in the grid; and this registry area is also how debuggers attach to a process at launch, so some security software watches writes to it.
- Protected processes are flagged and cannot be killed or reprioritized (see **Safety** below).
- **Interactive apps** (browsers, Start/Search/shell hosts, Task Manager, VS Code, ...) can't be set to Idle or BelowNormal by default, because that makes them lag or freeze; raising them to Normal or higher is always allowed. Check **Allow throttling interactive apps** next to Apply Priority to override this for that action — genuinely protected/security-critical processes (see **Safety** below) stay off-limits regardless of this checkbox.
- **Free RAM (selected / all)** — trims the working set of processes (`EmptyWorkingSet`). Nothing is killed and priorities don't change; Windows reclaims the pages only under memory pressure and the app faults back in whatever it uses, so it is safe for browsers too. "All" skips protected processes and anything under 20 MB. The freed memory tends to creep back, so treat it as a one-time cleanup.
- **Laptop saver…** — for running processes classified as Telemetry or Vendor/Diagnostics (never Core, dev tools, protected or interactive apps), shows a confirmation list, then sets them to BelowNormal and trims their RAM. Nothing is killed or disabled, and **Undo** restores the original priorities.
- **Auto-Optimize (RAM-aware)** — watches free RAM every 2 seconds. When it drops below the threshold you set (default 2 GB), background processes that have been genuinely idle (a measured near-0% CPU for ~6+ seconds; a process whose CPU time Windows won't let the app read counts as unknown, never as idle) get their RAM trimmed and priority lowered to BelowNormal — never Idle, and never the browser, shell, or whatever window you're actively using, since idleness (not app name) is what's being detected. "Actively using" covers the whole app, not just the window's owner: every process that owns part of the foreground window, plus every other process with the same exe name (Electron renderer/GPU helpers, the process behind a Store app), is exempt. If you switch to an app Auto-Optimize had slowed or frozen, it is released immediately rather than waiting for RAM to recover. The threshold accepts `2.5` or `2,5` regardless of your Windows language. Once free RAM recovers, everything it touched is automatically restored. This is the safe way to free resources for a heavy foreground workload (e.g. a local LLM in LM Studio) without hand-picking processes yourself, and without the mistake of pinning a browser to Idle priority (which can break Cloudflare-style bot checks — see **Safety** below).
- **Allow suspending long-idle apps** — a stronger tier on top of Auto-Optimize: after ~5 minutes of continuous idle, fully freezes (0% CPU, via the same NT call Task Manager's "Freeze" uses) a background process instead of just deprioritizing it. It resumes instantly the moment you switch to it, or via **Resume All Suspended**. The first time this fires in a session it asks for confirmation, and a red "N suspended" indicator stays visible in the toolbar the whole time anything is frozen. Known antivirus/EDR/backup agents (see **Safety**) are never eligible, with no override.
- **Restart** — appears next to any process row already flagged red (≥400 MB, a memory "hog"). One click kills just that PID (with the same confirmation as Kill Selected); safe for browser helper processes since the browser respawns a fresh one.

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

### Security tab

- Read-only view of the Windows Firewall profiles, plus WinRM on/off and an inbound SMB (139/445) block rule. **Unblock** only removes this app's own rule ("BgOptimizer Block SMB Inbound"); if that rule doesn't exist it reports success instead of a netsh error. If file sharing still doesn't work after that, the cause is Windows' built-in "File and Printer Sharing (SMB-In)" rules being disabled, which this app doesn't manage.
- **Application firewall (outbound)** — **Block app…** picks an `.exe` (e.g. `msedge.exe`, `java.exe`, a developer tool) and adds a Windows Firewall rule blocking its outbound traffic; a dropdown lists only rules this app created, and **Remove rule** deletes the selected one. Core Windows processes on the protected list are refused, the path must be an existing `.exe`, and changes are undoable.
- **Battery health** — shows full-charge vs. design capacity (wear %) and a live charge %/rate, computed from `RemainingCapacity ÷ FullChargedCapacity` (the same math Windows itself uses) and refreshed every 2 seconds. If the battery's driver doesn't report a design capacity, it falls back to showing just the full-charge figure.
- **Hibernate on lid close (battery only)** — **Enable** turns on hibernation and switches the lid-close-on-battery action from Sleep to Hibernate, so a closed laptop on battery stops drawing the small trickle of power Sleep uses for many hours. Not all hardware allows changing this (Modern Standby-only laptops can silently ignore it), so the toggle re-reads the setting after writing it and reports failure instead of claiming success if it didn't stick. **Disable** reverts to Sleep, the Windows default.

### Tweaks tab

A curated set of reversible registry tweaks, each behind a confirmation: minimum diagnostic data (HKLM policy, needs admin), advertising ID and tailored experiences, suggested apps/Start ads/lock-screen tips, background Store apps, web results in Start search, Game DVR, and the startup delay. Unticking a tweak restores the Windows default (deleting the value where Windows has none); **Undo** restores your exact previous value. Nothing here touches Defender, SmartScreen, UAC, Windows Update or the firewall. Some changes need a sign-out/sign-in (web search may need Explorer restarted). Widgets is deliberately not included: Windows 11 blocks other programs from writing that setting.

### Activity Log

A running log at the bottom of the window records every action taken (or blocked), with a timestamp. The **Undo** and **Clear** buttons sit in its header bar.

### Multi-language UI

Switch languages live from the dropdown in the top-right — no restart needed. Supported: English, Ukrainian, Spanish, German, Italian, Polish, Russian. Every label, button, column header, dialog, and log message is translated.

### System tray

Minimizing the window sends it to the system tray (look for the blue "B" icon — Windows may tuck new tray icons behind the `^` "show hidden icons" arrow near the clock the first time). Double-click the tray icon, or right-click → **Open**, to restore it. Right-click → **Exit** fully quits the app; on the way out it restores any priorities it lowered and resumes any process it froze, so nothing is left suspended once the app is gone (a hard crash or kill of the app itself can't do this — use Task Manager's process list to resume anything left frozen).

## Safety

This app runs elevated (admin) because managing services and killing other processes requires it — which also means it *could* be used to break things if you're not careful. Several layers of protection:

1. **Protected list** ([Services/ProtectedItems.cs](Services/ProtectedItems.cs)) — hard-blocks changes to ~40 core Windows processes and services (`csrss`, `lsass`, `svchost`, `explorer`, `RpcSs`, `WinDefend`, `TrustedInstaller`, Windows Defender's engine and related services, etc.). These show as "Protected" in the grids and every action against them is refused, not just warned.
2. **Interactive-app list** (same file) — browsers, shell surfaces, Task Manager, and dev tools can't be set to Idle/BelowNormal without an explicit override, and are never eligible for automatic throttling or suspension at all (see below) — this is what stops Auto-Optimize from ever repeating the mistake of pinning a browser to Idle priority, which can break things like Cloudflare Turnstile bot verification.
3. **Security-tool list** (same file) — ~30 common third-party antivirus/EDR/backup/endpoint-management process names (CrowdStrike, SentinelOne, Sophos, ESET, Malwarebytes, Kaspersky, Norton, McAfee, Bitdefender, Veeam, Acronis, and others). These are never eligible for Auto-Optimize's throttling or the auto-suspend tier, or for Laptop saver, with no override — unlike priority changes, freezing a security tool stops it from protecting the machine at all, not just runs it slower.
4. **Confirmation dialogs** — killing a process, disabling a startup item, disabling a service, stopping a service, or restarting a memory hog all prompt for confirmation before acting. Destructive confirmations default to **Cancel** (and take focus), so a stray Enter can't run the action. Action buttons that need a selection log "Nothing selected" instead of silently doing nothing. The auto-suspend tier additionally asks once, the first time it's about to freeze a process in a session, before proceeding unattended after that.
5. **Live identity check** — every kill/priority/trim/suspend/resume re-reads the process's real name at the moment of the action and refuses if it no longer matches the grid row (or, for everything except resume, is now on the protected list), so a recycled PID can never redirect an action onto the wrong process. The process list itself also notices a recycled PID (different name or start time) and treats it as a new process, so its name and protected flag are never inherited from the old one.
6. **Restore Point safety net** — the "Auto restore point before risky changes" checkbox (Startup Apps tab, on by default) creates a System Restore point via the `SystemRestore` WMI class before Disable Startup, Set: Disabled, or Stop Now runs. There's also a manual **Create Restore Point** button for creating one on demand. This is best-effort: if System Restore is off for the system drive, or Windows' own once-per-24-hours throttle kicks in, the app logs it and proceeds anyway rather than blocking your actual request.
7. **Undo Last Change** — the button in the Activity Log bar (it is global, so it lives with the log rather than on any one tab); reverts the most recent reversible action (startup enable/disable, service start-type change, service stop, firewall rule, registry tweak, or Laptop saver) from a session-only LIFO stack. It isn't persisted across app restarts — closing the app and reopening Startup Apps/Services to set something back by hand is equivalent. Auto-Optimize's own changes revert themselves automatically once RAM pressure clears, and suspended processes can always be resumed via **Resume All Suspended**.

8. **Hardened tool launching** — the Windows tools the app runs while elevated (`netsh`, `powercfg`, `schtasks`) are started from the System32 folder by full path, so a look-alike `.exe` placed next to the app can't run as administrator, and their arguments are passed as a list rather than built into one string, so a quote or space in a firewall rule name or program path can't inject extra parameters.

The protected/interactive/security-tool lists can't know about every third-party or OEM (e.g. laptop vendor) process or service, so exercise the same judgment yourself there — prefer **Manual** over **Disabled** for anything you're not 100% sure about, change one thing at a time, and lean on the Category hint and Restore Point button above for anything you're unsure about.

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

[installer/Install-Shortcut.ps1](installer/Install-Shortcut.ps1) copies a published build to `%ProgramFiles%\BgOptimizer` and creates Start Menu + Desktop shortcuts for all users. **Run it from an elevated (Administrator) PowerShell.** It installs to Program Files on purpose: the app runs elevated, so if it lived in a user-writable folder (like `%LocalAppData%`), any non-admin process running as you could swap one of its DLLs and get code running as administrator the next time you accept the UAC prompt. Program Files is writable only by administrators. The script does a clean replace (no stale files from older builds), refuses to run while the app is open, and removes any earlier per-user install under `%LocalAppData%\Programs\BgOptimizer` and its shortcuts.

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

## Tests

```bash
dotnet test tests/BgOptimizer.Tests
```

xUnit tests cover the safety rules (protected / interactive / security-tool lists, the live-identity check on kill, priority, suspend and resume, firewall-block refusals), the idle-detection logic, the Category classifier, and the parsers for `powercfg` output and firewall rules (checked with non-English labels). Tests that act on a process only ever touch a `ping.exe` child they start themselves; nothing changes system settings. CI runs them on every push.

## Project structure

```text
MainWindow.xaml(.cs)        Main UI and event handlers
ConfirmDialog.xaml(.cs)    Themed confirmation dialog (lists affected items; Cancel is default for destructive actions)
Theme.xaml                  Colour tokens and control styles (buttons, tabs, grids, ComboBox)
Converters/                 Severity rules + brush/glyph/weight converters for the grids
Models/                     Data models (ProcessEntry, StartupEntry, ServiceEntry, ItemCategory)
Services/
  ProcessMonitor.cs         Process enumeration, CPU% sampling, kill/priority/suspend-resume
  StartupManager.cs         Startup item enumeration and enable/disable (Run keys, Startup folders, Scheduled Tasks)
  SvcManager.cs              Windows service enumeration and start-type/stop/start control (via WMI)
  ProtectedItems.cs         The safety deny-lists (protected, interactive, security-tool)
  ItemClassifier.cs         Best-effort Category labeling for Startup Apps/Services
  PriorityPersistence.cs    Persistent process priority via Image File Execution Options
  SecurityManager.cs        WinRM, SMB block, per-app outbound firewall rules (netsh)
  TweakManager.cs           Curated reversible registry tweaks with exact-value undo
  SystemRestoreService.cs   System Restore point creation (via the SystemRestore WMI class)
  WorkloadOptimizer.cs      Idle-CPU-streak tracking and candidate selection for Auto-Optimize
  ForegroundWindow.cs       Foreground-window PID detection, so Auto-Optimize never touches the app you're using
Localization/
  Loc.cs                    Translation tables + live language-switching
  ActionResult.cs           Localized result type returned by service actions
tests/BgOptimizer.Tests/    xUnit tests (run with `dotnet test tests/BgOptimizer.Tests`)
scripts/                    Optional standalone hardening scripts (WinRM, inbound SMB); see scripts/README.md
installer/
  Install-Shortcut.ps1      Copies a published build + creates Start Menu/Desktop shortcuts
icon.ico                    App icon (exe, window title bar, and tray all share this)
.github/workflows/build.yml CI: builds and publishes on every push/PR
```

## CI/CD

Every push and pull request triggers a GitHub Actions build on `windows-latest` (required since this is a WPF app), which restores, builds, and publishes a self-contained `win-x64` build, uploading it as a downloadable artifact from the run. See **Installing a pre-built copy** above for how to use it.
