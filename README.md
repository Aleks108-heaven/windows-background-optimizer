# Background Optimizer

A Windows 11 desktop app (WPF/.NET) for seeing and controlling what's running in the background: processes, startup apps, and Windows services — with built-in safeguards so it can't be used to disable something critical by accident.

## Features

### Processes tab

- Live list of all running processes with CPU % and memory usage, refreshing every 5 seconds (`RefreshSeconds` in [MainWindow.xaml.cs](MainWindow.xaml.cs)). The scan runs on a background thread, so the window stays responsive while it works.
- CPU, memory and priority values are flagged by colour *and* a glyph/weight (▲ warn, ◆ danger and bold, ▼ low priority), so severity is readable without relying on colour.
- **Kill Selected** — force-terminates the selected process (with a confirmation prompt).
- **Apply Priority** — sets a process's CPU scheduling priority (Idle, BelowNormal, Normal, AboveNormal, High) without killing it. `Idle` is the safest way to throttle something you don't want competing for CPU while keeping it running.
- **Persist across restarts** — a switch next to Apply Priority. Checked, it writes the chosen priority to the registry under Image File Execution Options (the same mechanism Task Manager's own "Efficiency Mode" uses), so Windows re-applies it every time that exe launches, not just until the next reboot. Unchecked, applying just changes the live process as before, and clears any persistent rule that exe previously had. A persistent rule is never accepted for Idle/BelowNormal on an interactive app (browser, shell, ...), even with the override box ticked, and a refused rule is not re-applied in the background either. A saved rule is re-applied to new instances of that exe while the app runs; if one instance can't be changed (e.g. access denied) it is logged once and left alone rather than retried on every refresh. Two things worth knowing: it matches by the exe's bare filename, so it affects every instance of that exe system-wide, not just the one selected in the grid; and this registry area is also how debuggers attach to a process at launch, so some security software watches writes to it.
- Protected processes are flagged and cannot be killed or reprioritized (see **Safety** below).
- **Interactive apps** (browsers, Start/Search/shell hosts, Task Manager, VS Code, ...) can't be set to Idle or BelowNormal by default, because that makes them lag or freeze; raising them to Normal or higher is always allowed. Check **Allow throttling interactive apps** next to Apply Priority to override this for that action — genuinely protected/security-critical processes (see **Safety** below) stay off-limits regardless of this checkbox.
- **Free RAM (selected / all)** — trims the working set of processes (`EmptyWorkingSet`). Nothing is killed and priorities don't change; Windows reclaims the pages only under memory pressure and the app faults back in whatever it uses, so it is safe for browsers too. "All" skips protected processes, audio apps and anything under 20 MB. The freed memory tends to creep back, so treat it as a one-time cleanup.
- **Laptop saver…** — for running processes classified as Telemetry or Vendor/Diagnostics (never Core, dev tools, protected or interactive apps), shows a confirmation list, then sets them to BelowNormal and trims their RAM. Nothing is killed or disabled, and **Undo** restores the original priorities.
- **Auto-Optimize (RAM-aware)** — checks free RAM on every refresh (every 5 seconds). When it drops below the threshold you set (default 2 GB), background processes that have been genuinely idle (a measured near-0% CPU on 3 consecutive samples, about 15 seconds; a process whose CPU time Windows won't let the app read counts as unknown, never as idle) get their RAM trimmed and priority lowered to BelowNormal — never Idle, and never the browser, shell, or whatever window you're actively using, since idleness (not app name) is what's being detected. "Actively using" covers the whole app, not just the window's owner: every process that owns part of the foreground window, plus every other process with the same exe name (Electron renderer/GPU helpers, the process behind a Store app), is exempt. If you switch to an app Auto-Optimize had slowed or frozen, it is released immediately rather than waiting for RAM to recover. The threshold accepts `2.5` or `2,5` regardless of your Windows language. Once free RAM recovers, everything it touched is automatically restored. This is the safe way to free resources for a heavy foreground workload (e.g. a local LLM in LM Studio) without hand-picking processes yourself, and without the mistake of pinning a browser to Idle priority (which can break Cloudflare-style bot checks — see **Safety** below).
- **Allow suspending long-idle apps** — a stronger tier on top of Auto-Optimize: after ~5 minutes of continuous idle, fully freezes (0% CPU, via the same NT call Task Manager's "Freeze" uses) a background process instead of just deprioritizing it. It resumes instantly the moment you switch to it, or via **Resume All Suspended**. The first time this fires in a session it asks for confirmation, and a red "N suspended" indicator stays visible in the toolbar the whole time anything is frozen. Known antivirus/EDR/backup agents and audio apps (see **Safety**) are never eligible, with no override.
- **Restart** — appears next to any process row already flagged red (≥400 MB, a memory "hog"). One click kills just that PID (with the same confirmation as Kill Selected); safe for browser helper processes since the browser respawns a fresh one.

### Startup Apps tab

- Lists every startup entry from the registry Run keys (current user, all users, and the 32-bit all-users `WOW6432Node` key), both Startup folders (yours and all-users), and Scheduled Tasks — the mechanism most OEM bundleware (ASUS, Dell, HP, ...) actually uses to relaunch its background agents, which Run keys and the Startup folder never see. Tasks under `\Microsoft\...` are excluded on purpose (that's ~300+ Windows-internal plumbing tasks on a typical machine; surfacing those would bury the handful of OEM/third-party ones you actually came here to manage).
- **Enable Selected** / **Disable Selected** — toggles whether an item launches at login. Uses the same mechanism Task Manager uses for Run-key and Startup-folder entries (the `StartupApproved\Run`, `Run32` and `StartupFolder` flags, so the two always agree and nothing is renamed or deleted) and `schtasks.exe` for Scheduled Tasks, so it's fully reversible. Older versions of this app switched shortcuts off by renaming them to `*.disabled`; those are still listed (as off) and are moved back to their real name when switched on. Every switch is written to the action log as `STARTUP_ENABLE` / `STARTUP_DISABLE`, whatever the outcome.
- **Locked and read-only items.** Disabling is refused for security software (Windows Security, Defender, common third-party AV/EDR), for components that live inside Windows itself (the driver store is excluded, so vendor helpers such as a Realtek audio service stay switchable) and for this app; turning one of them back **on** is always allowed. RunOnce entries, Group-Policy Run entries and Store-app startup tasks are listed but read-only (Windows consumes or manages them), with an **Open Windows startup settings** button (`ms-settings:startupapps`) for those. The values Windows uses for Store-app tasks are read as the documented `StartupTaskState` numbers; that registry layout is not documented, so check a row against Settings > Apps > Startup if it matters.
- **Publisher, Note and the details line.** *Publisher* is the signer name embedded in the file, or the Company field of its version info; it is a label to recognise a program by, **not** a verification (the signature is not validated). *Note* flags `Target file missing` (the entry points at a file that no longer exists, so it does nothing; only a path Windows says is absent counts, "access denied" does not), locked and read-only rows. Selecting one row shows, under the grid, what it runs (the full command line for Run keys), its publisher, and why it is locked or read-only. All paths are treated as untrusted text: they are parsed and checked for existence, never executed.
- **Self-re-enabling programs.** If a program you disabled here is enabled again at the next refresh (it rewrote its own entry, or something else re-enabled it), the log says so once. Managed (Group Policy / MDM) entries appear under *Policy* and cannot be switched here. After Fast Startup or hibernation the "next login" may not re-read startup entries; a full restart does.
- **Category** column — a best-effort hint (Telemetry / Core / Developer Tool / Vendor-Diagnostics / Uncategorized) based on name patterns, to help you decide what's safe to touch. It's a suggestion, not a verdict — the app never selects or acts on items based on it, and the same task can be safe for one person and load-bearing for another.

- **Leftover `.disabled` files are repaired.** Versions up to 1.2.x switched a Startup-folder shortcut off by renaming it to `name.lnk.disabled`. Windows tries to open every file in a Startup folder at sign-in, so those copies made it ask "Which app do you want to use to open this .disabled file?" on every start (seen on several laptops). On every refresh the app now moves such a copy back to its real name and switches it off with the same `StartupApproved` flag Task Manager uses, so the item stays exactly as off as you left it and the question stops. If a real copy already exists, the stale one is moved (never deleted or overwritten) to `%LocalAppData%\BgOptimizer\legacy-startup`. Only startup-type files (`.lnk .url .cmd .bat .exe .vbs .js .ps1 .appref-ms`) are touched, hidden/system files and links are skipped, the Startup folder must lie inside your profile (all-users: ProgramData) and not be a junction, and nothing is done in view-only mode. Each file is written to the action log (`STARTUP_REPAIR_LEGACY`).

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
- **Battery health** — shows full-charge vs. design capacity (wear %) and a live charge %/rate, computed from `RemainingCapacity ÷ FullChargedCapacity` (the same math Windows itself uses) and refreshed every 5 seconds. If the battery's driver doesn't report a design capacity, it falls back to showing just the full-charge figure.
- **Hibernate on lid close (battery only)** — **Enable** turns on hibernation and, for battery power only, (1) switches the lid-close action from Sleep to Hibernate and (2) sets "hibernate after" to 30 minutes of sleep. Both matter on Modern Standby laptops: there, screen-off and idle-timeout enter Modern Standby (the power light stays on) and the lid action does not apply to them, so without the 30-minute timeout the laptop stays in standby until Windows' own battery-budget rule hibernates it, which can take many hours. Plugged-in settings are not changed. Not all hardware allows changing the lid action (Modern Standby-only laptops can silently ignore it), so the toggle re-reads that setting after writing it and reports failure instead of claiming success if it didn't stick. **Disable** sets the lid action back to Sleep (the Windows default) and puts back the "hibernate after" value it replaced; if the app was restarted between Enable and Disable it no longer knows the old value and sets it to "never".

### Tweaks tab

A curated set of reversible registry tweaks, each behind a confirmation: minimum diagnostic data (HKLM policy, needs admin), advertising ID and tailored experiences, suggested apps/Start ads/lock-screen tips, background Store apps, web results in Start search, Game DVR, and the startup delay. Unticking a tweak restores the Windows default (deleting the value where Windows has none); **Undo** restores your exact previous value. Nothing here touches Defender, SmartScreen, UAC, Windows Update or the firewall. Some changes need a sign-out/sign-in (web search may need Explorer restarted). Widgets is deliberately not included: Windows 11 blocks other programs from writing that setting.

### Activity Log

A running log at the bottom of the window records every action taken (or blocked), with a timestamp. The **Undo** and **Clear** buttons sit in its header bar.

### Multi-language UI

Switch languages live from the dropdown in the top-right — no restart needed. Supported: English, Ukrainian, Spanish, German, Italian, Polish, Russian. Every label, button, column header, dialog, and log message is translated.

### Controls and look

Settings that are simply on or off (Auto-Optimize, Allow suspend, Persist across restarts, Auto restore point, the Tweaks) are **switches**; lists where you pick items to act on (Cleanup) use **checkboxes**. Both are the same underlying control, so Space toggles them from the keyboard and screen readers announce them as checkboxes with their on/off state. State is shown by shape (tick, dash, thumb side) as well as colour. Checkboxes, switches, text boxes, combo boxes and scroll bars are all drawn in the dark theme (the stock Windows ones are light grey and clash), with hover, keyboard-focus and disabled looks; borders and filled states keep at least 3:1 contrast against the surface and the tick/thumb at least 3:1 on the fill (checked by the theme tests), and every checkbox/switch row is at least 24 px tall. The look lives in [Theme.xaml](Theme.xaml).

### Settings tab: view-only mode and system load

- **View-only mode** (a switch). The app only watches: it will not slow down, freeze, trim or close any process, change startup items, services, security settings or tweaks, clean files or create restore points. **Auto-Optimize keeps running** and records, once per process and episode, what it *would* have done ("View-only: would lower the priority and trim the RAM of ..."). The refusal sits inside the services that make changes, so no button or background path can slip past it, and a banner at the top of the window shows the mode is on. Putting something back is never blocked: turning the mode on first releases anything the app had slowed or frozen, and resuming, restoring priorities and the **Resume all suspended** button keep working. A persistent-priority rule already written to the registry keeps applying (that is Windows, not this app). The choice is remembered between launches. While the mode is on, rows in the Processes, Startup Apps and Services lists cannot be selected (every button there would only be refused); the Checks lists stay selectable because **Open location** only reads.
- **Code lock** (optional). **Set code...** stores a secret code of at least 8 characters; from then on the view-only switch only turns off after the code is entered, and changing or removing the code asks for it too. The mode, a salted PBKDF2-SHA256 hash of the code (never the code) and the wrong-try count are kept in the registry key `HKEY_LOCAL_MACHINE\SOFTWARE\BgOptimizer`, which only an administrator can write, not in `settings.json` (any program running as you can edit that file). So malware running as you without administrator rights cannot switch view-only mode off or the lock away by editing a file, and quitting and relaunching does not reset the delay: after two wrong tries each further one adds a 5-second wait (up to 60 s). It does **not** stop an administrator, who can delete the key; that is also how to recover from a forgotten code: as administrator, delete `HKEY_LOCAL_MACHINE\SOFTWARE\BgOptimizer` (the uninstaller removes it too). A code set with 1.2.0 was stored in `settings.json`, is no longer trusted, and must be set again.
- **System load.** Live bars for CPU, memory, disk activity and GPU, refreshed with the normal 5-second refresh while the window is open. A white line on each bar is your **load limit**, set with the slider (50-100 %, default 90 %); a bar turns red above it and says "above limit", so colour is never the only signal. Disk and GPU come from the WMI performance classes (same names on every Windows language) and are only read while this tab is open or the alert is on; GPU is the busiest engine type (3D, copy, video decode, ...), like Task Manager, and shows "not available" on a machine that does not report it.
- **Notify me when load stays above the limit** (off by default). After three samples in a row above the limit (about 15 s) it writes one line naming the busiest programs (CPU and memory), and shows a tray notification if the window is hidden, at most once per load every 5 minutes. While it is on, the app keeps refreshing even when hidden in the tray. It only informs.
- **Optimize automatically when load stays above the limit** (off by default). The same limit, but it acts: once any of CPU, memory, disk or GPU has been above the limit for three refreshes in a row (about 15 s), the same gentle step as Auto-Optimize is applied to background programs that have been idle for a while: their RAM is trimmed and their priority lowered to BelowNormal. It stays in force until every load has been at least 5 points under the limit for three refreshes (so it does not flip on and off at the line), then everything it changed is restored. The program you are using, browsers, audio apps, security software and local-AI tools are never touched (the same lists as Auto-Optimize), and a load alone never freezes anything: **Allow suspending long-idle apps** stays tied to low free RAM. In view-only mode it only logs what it would have done. It is independent of the RAM-based **Auto-Optimize** switch (use either or both) and does not depend on the PC's size, so it applies the same on an 8 GB laptop. Because it only touches idle programs, it helps most when memory or disk (paging) is the bottleneck; it cannot reduce CPU load made by the program you are actively using. The action log records when it starts and ends (`Load pressure: ...`).
- **Load readings are cheap.** Disk and GPU load are read through the Windows Performance Data Helper (the counters Task Manager's numbers come from, by their English names, so any Windows language works), not through WMI. A sample takes a few milliseconds; the WMI queries it replaced took about 0.6 s of wall time and roughly 0.4 CPU-seconds each on a fast laptop CPU, every few seconds, even while the window was hidden, which is a constant drain on an old dual-core. Nothing is drawn while the window is hidden, the battery line is only read while the Security tab is showing, and the temperature readout only while the Settings tab is.
- **Programs to never touch.** A comma-separated list of program names (for example `AnyDesk, MyVpnClient`). Auto-Optimize, Laptop saver, trimming, freezing and Persist across restarts skip them, with no override. For corporate agents that sit idle but must stay reachable and that no built-in list can know. Names may contain spaces; `.exe` is dropped; paths, wildcards and anything over 64 characters are ignored, at most 100 names count, and a hand-edited `settings.json` is validated the same way.
- **Cooling.** Shows what Windows reports (hottest ACPI thermal zone, whether Windows is slowing the CPU to cool it, CPU speed as a share of its maximum; read-only, no driver, no administrator rights; some laptops report a fixed or no temperature) and offers the one safe lever: a **CPU speed limit** (90/80/70/60 % of top speed, plugged in and on battery) in the current power plan, via powercfg. It is read back after writing and reported as failed (and put back) if Windows did not accept it; **Undo Last Change** restores the previous values. This is deliberately **not a fan controller**: fan speed belongs to the laptop's firmware (on ASUS: MyASUS, Fan mode), Windows has no safe interface for it, and reaching it from user mode needs a kernel driver, which an app that runs as administrator should not ship.

### System tray

Minimizing the window sends it to the system tray (look for the blue "B" icon — Windows may tuck new tray icons behind the `^` "show hidden icons" arrow near the clock the first time). Double-click the tray icon, or right-click → **Open**, to restore it. Right-click → **Exit** fully quits the app; on the way out it restores any priorities it lowered and resumes any process it froze, so nothing is left suspended once the app is gone. If the app is instead crashed or force-killed while something is frozen, that process stays frozen until the app is started again: every freeze is recorded (process name and start time) in `%LocalAppData%\BgOptimizer\suspended.json`, and the next launch resumes whatever on that list is still the same process and logs how many it released. A recycled process ID is never resumed.

Only one copy of the app runs at a time: starting a second copy (for example by clicking the pinned taskbar icon or the Start menu entry while the app sits in the tray) exits immediately and asks the running copy to show its window. Because minimizing hides the window to the tray, the taskbar icon shows no "running" line while it is minimized; that is expected.

### Saved settings

These choices are remembered between launches, in `%LocalAppData%\BgOptimizer\settings.json`, and saved the moment you change them (not only on exit, so a crash or kill can't lose them): the language, **Auto-Optimize** on/off, its free-RAM threshold, **Allow suspending long-idle apps**, **Auto restore point before risky changes**, **Persist across restarts** (next to Apply Priority), the load alert, the load limit, **Optimize automatically when load stays above the limit** and the **programs to never touch**.

Two things are deliberately not remembered. **Allow throttling interactive apps** starts unticked on every launch, because it is the override that can make browsers and shell surfaces lag. The priority dropdown also starts at Normal. If Auto-Optimize and the suspend option were left on, they are on again at the next launch, and the suspend tier still asks for confirmation the first time it is about to freeze something in each session. A missing, corrupt or hand-edited settings file is ignored field by field: unusable values fall back to the defaults (everything unattended is off, language English, threshold 2 GB). To reset everything, exit the app and delete `settings.json`.

## Action log

Everything the app does is also appended to `%LocalAppData%\BgOptimizer\actions.log` (written straight through to disk, rolled over to `actions.log.1` at 1 MB): start and exit with the Auto-Optimize settings in force, when free RAM crosses the threshold, every throttle/trim/suspend/resume and refusal, and unhandled errors. The on-screen log is capped and disappears on exit; this one survives a crash or a hard reset, so after a freeze it shows whether the app was running and what it had touched. Lines are in the UI language you had selected.

## Idle when hidden

Minimised to the tray, the app stays one click away but stops polling (no process scans, no battery or load queries) as long as **Auto-Optimize is off**. Opening the window resumes it at once with fresh data. With Auto-Optimize on it keeps running while hidden, because watching memory and releasing what it changed is its standing job; turning Auto-Optimize off while hidden first releases anything it had throttled or frozen, then idles. Both transitions are written to the action log.

## Cleanup

The **Cleanup** tab deletes old, regenerable files. **Scan** shows what each group would free; **Clean selected** asks for confirmation and then deletes. Groups: your temp files and Windows temp (7+ days old), Windows error reports (7+ days), old Windows servicing/Update logs and LM Studio / VS Code logs (30+ days) — all ticked by default — plus GPU shader caches, Edge/Chrome caches and developer package caches (pip, npm, NuGet, Scoop), which start unticked because they are rebuilt or re-downloaded at a cost. It stays inside fixed folders, never follows junctions or symlinks, skips files that are in use, and refuses drive roots and broad folders (profile, Windows, Program Files). Crash dumps, the Recycle Bin, documents and BgOptimizer's own `actions.log` are never touched.

## Checks

The **Checks** tab holds three scans, one sub-tab each so the result list gets the whole height of the window. They only look: nothing is deleted, moved or changed, with one exception, **Remove selected…** on the Duplicate drivers sub-tab (below), which asks first. Each can be stopped with **Stop**, and only one runs at a time. **Open location** shows the selected row in Explorer (for a path with spaces such as `C:\Program Files\Asus` it selects that folder; an earlier build opened Documents instead, because it handed Explorer the path in a form Explorer cannot read). Hovering a cut-off cell shows its full text.

- **Deleted programs: leftovers and files still in memory.** Reports (1) running programs, and programs holding loaded DLLs, whose file has since been deleted from the disk — the files are gone but the program is still in memory until restarted; (2) entries in Installed apps whose install folder or uninstaller no longer exists; (3) folders in Program Files, Program Files (x86) and `%LocalAppData%\Programs` that no installed app, service or running process owns, classified as empty, data-only (no program files) or "has program files but no matching installed app". It is deliberately conservative: a path it merely cannot read (access denied, unplugged drive) is never called deleted; Windows' own folders, hidden/system folders and folders whose name matches an installed app or publisher are skipped. A folder can still belong to software installed without a normal uninstaller, so review before deleting.
- **Duplicate files.** Finds files with identical content: same size, then the same hash of the first 64 KB, then the same SHA-256 of the whole file, for files of 1 MB or more. It scans Downloads, Documents, Desktop, Pictures, Videos and Music, or one folder you choose. It never follows junctions, skips `.git` and `node_modules`, and skips cloud-only (OneDrive online-only) files so reading them does not download them. Groups are numbered and sorted by the space keeping one copy would free; you decide which copy to keep.
- **Duplicate drivers.** Windows keeps every version of a driver it has installed in its driver store. This lists drivers stored more than once, newest first, marking each copy *in use* or *not in use* by a device and giving its `oemNN.inf` name, version, date, size and provider. Old copies are harmless (they cost disk space and allow a rollback). The check reads the driver store folders, each package's INF and the `oemNN.inf` copies in `C:\Windows\INF`, so it does not depend on the Windows display language. Three buttons act on the list:
  - **Open folder** opens the selected copy's folder in the driver store (`C:\Windows\System32\DriverStore\FileRepository\...`). It only reads.
  - **Select old, unused** ticks every row that **Remove selected…** would accept.
  - **Remove selected…** deletes the selected copies with `pnputil /delete-driver oemNN.inf`, after a confirmation that lists them (Cancel is the default) and, if **Auto restore point** is on, after a restore point. It only ever accepts a copy that is **older** than another copy of the same driver, **not in use** by any device, has a date and has an `oemNN.inf` name (the name is checked to be exactly that before it reaches `pnputil`; Windows' own inbox drivers have none and are never offered). The newest copy and copies in use are never offered, `/force` is never used (so Windows itself still refuses a package a device uses, even if the "in use" label was wrong because WMI was unavailable), and view-only mode blocks it. Every removal is written to the action log as `DRIVER_DELETE`. A removed copy cannot be brought back from here; the restore point is the way back.

## Safety

This app runs elevated (admin) because managing services and killing other processes requires it — which also means it *could* be used to break things if you're not careful. Several layers of protection:

1. **Protected list** ([Services/ProtectedItems.cs](Services/ProtectedItems.cs)) — hard-blocks changes to ~40 core Windows processes and services (`csrss`, `lsass`, `svchost`, `explorer`, `RpcSs`, `WinDefend`, `TrustedInstaller`, Windows Defender's engine and related services, etc.). These show as "Protected" in the grids and every action against them is refused, not just warned.
2. **Interactive-app list** (same file) — browsers, shell surfaces, Task Manager, and dev tools can't be set to Idle/BelowNormal without an explicit override, and are never eligible for automatic throttling or suspension at all (see below) — this is what stops Auto-Optimize from ever repeating the mistake of pinning a browser to Idle priority, which can break things like Cloudflare Turnstile bot verification.
3. **Security-tool list** (same file) — ~30 common third-party antivirus/EDR/backup/endpoint-management process names (CrowdStrike, SentinelOne, Sophos, ESET, Malwarebytes, Kaspersky, Norton, McAfee, Bitdefender, Veeam, Acronis, and others). These are never eligible for Auto-Optimize's throttling or the auto-suspend tier, or for Laptop saver, with no override — unlike priority changes, freezing a security tool stops it from protecting the machine at all, not just runs it slower.
3a. **Local-AI list** (same file) — LM Studio (`LM Studio`, `lms`, `llama-server`), Ollama, llama.cpp, koboldcpp and similar model hosts, plus agent runtimes (`python`, `node`, `deno`, `bun`) and WSL (`vmmem`). They sit near 0% CPU while waiting on the GPU or on each other, which the idle detector can't tell apart from "doing nothing", so they are never trimmed, slowed down or suspended — by Auto-Optimize or by hand — with no override. Lowering priority is blocked for them too, including via Persist across restarts.
4. **Confirmation dialogs** — killing a process, disabling a startup item, disabling a service, stopping a service, or restarting a memory hog all prompt for confirmation before acting. Destructive confirmations default to **Cancel** (and take focus), so a stray Enter can't run the action. Action buttons that need a selection log "Nothing selected" instead of silently doing nothing. The auto-suspend tier additionally asks once, the first time it's about to freeze a process in a session, before proceeding unattended after that.
5. **Live identity check** — every kill/priority/trim/suspend/resume re-reads the process's real name at the moment of the action and refuses if it no longer matches the grid row (or, for everything except resume, is now on the protected list), so a recycled PID can never redirect an action onto the wrong process. The process list itself also notices a recycled PID (different name or start time) and treats it as a new process, so its name and protected flag are never inherited from the old one.
6. **Restore Point safety net** — the "Auto restore point before risky changes" switch (Startup Apps tab, on by default) creates a System Restore point via the `SystemRestore` WMI class before Disable Startup, Set: Disabled, or Stop Now runs. There's also a manual **Create Restore Point** button for creating one on demand. This is best-effort: if System Restore is off for the system drive, or Windows' own once-per-24-hours throttle kicks in, the app logs it and proceeds anyway rather than blocking your actual request.
7. **Undo Last Change** — the button in the Activity Log bar (it is global, so it lives with the log rather than on any one tab); reverts the most recent reversible action (startup enable/disable, service start-type change, service stop, firewall rule, registry tweak, or Laptop saver) from a session-only LIFO stack. It isn't persisted across app restarts — closing the app and reopening Startup Apps/Services to set something back by hand is equivalent. Auto-Optimize's own changes revert themselves automatically once RAM pressure clears, and suspended processes can always be resumed via **Resume All Suspended**.

8. **Hardened tool launching** — the Windows tools the app runs while elevated (`netsh`, `powercfg`, `schtasks`) are started from the System32 folder by full path, so a look-alike `.exe` placed next to the app can't run as administrator, and their arguments are passed as a list rather than built into one string, so a quote or space in a firewall rule name or program path can't inject extra parameters.

9. **Audio-app protection** (same file) — audio apps idle at near 0% CPU while playing sound (the work happens in Windows' `audiodg` and the driver), so an idle-based optimizer would mistake them for background junk, and throttling, trimming or freezing one cuts the sound. Known audio apps (AudioTune Pro, Realtek/Nahimic/Dolby helpers, Spotify, VLC, Voicemeeter, Discord, Teams, Zoom, ...) and any process whose name contains "audio" or "sound" are treated like interactive apps: Auto-Optimize, the auto-suspend tier, Laptop saver, the background priority re-apply and **Free RAM (all)** never touch them, **Apply Priority** refuses to lower them, and a persistent priority rule can't be set below Normal. Two things are still your call: ticking **Allow throttling interactive apps** lets a manual **Apply Priority** lower an audio app, and **Free RAM (selected)** will trim one if you select it yourself. Audio-related startup items and services are labelled **Core** in the Category column as a hint not to disable them (the app still lets you, after confirmation).

The protected/interactive/security-tool lists can't know about every third-party or OEM (e.g. laptop vendor) process or service, so exercise the same judgment yourself there — prefer **Manual** over **Disabled** for anything you're not 100% sure about, change one thing at a time, and lean on the Category hint and Restore Point button above for anything you're unsure about.

9a. **Work-agent list** (same file) — messengers and mail (Slack, Telegram, Outlook, ...), VPN and zero-trust clients (OpenVPN, WireGuard, Tailscale, GlobalProtect, Zscaler, ...), remote access (AnyDesk, TeamViewer, RustDesk, ...), sync clients (OneDrive, Dropbox, Google Drive, ...), password managers and VM/container hosts (VMware, VirtualBox, Docker Desktop). They sit near 0% CPU while waiting for the next message, packet or sync request, which the idle detector reads as "doing nothing"; freezing or slowing one means missed notifications, a dropped tunnel, a dead remote-support session, a frozen virtual machine, or a sync provider that hangs Explorer. Treated like interactive apps by every guard (Auto-Optimize, auto-suspend, Laptop saver, persistent rules).
9b. **Windows/driver helpers** (same file) — the Wi-Fi extension (`wlanext`), device association (`dasHost`), Windows Hello isolation (`NgcIso`, `BioIso`, `LsaIso`), and the AMD, Intel and graphics power/thermal helpers (`amdpmfservice`, `esif_uf`, `igfx*`, ...). They are idle by CPU almost always, take part in sleep and wake, and a priority rule in the registry applies from the moment they start. They are never slowed, trimmed or frozen by any route, with no override (raising a priority is still allowed). This was added after a run of hard freezes right after resume from Modern Standby; it is **not** proven that slowed helpers caused them, but slowing them gains nothing.
9c. **Leftover permanent rules are removed at start.** Idle or Below-normal rules in Image File Execution Options on interactive apps, work agents and Windows/driver helpers (which older versions or a rule set by hand can leave behind, invisibly) are removed when the app starts, written to the action log (`IFEO_CLEAR`, with the value each had) and reported in the Activity Log. Normal-or-higher rules are your choice and are kept, and so are rules on audio apps, local-AI tools and anything not in those groups. It needs administrator rights and does nothing in view-only mode.
9d. **Small-RAM laptops.** The free-RAM threshold is never allowed to exceed a third of the installed memory (a fixed 2 GB is half of a 4 GB laptop, where Auto-Optimize could otherwise never stand down); when the cap applies the Activity Log says so once. A program is trimmed at most once per 10 minutes, because a trim makes RAM look recovered, Auto-Optimize then releases everything, the pages fault back in and the cycle would repeat. The busiest-programs line now shows how many processes a name stands for (`svchost ×84`), since Windows runs dozens and the total is not one leaking program.

## Security notes

The app runs as administrator by design (it manages services, firewall rules and registry keys), so what it reads and writes is treated as a security boundary:

- **Install location.** Install to Program Files (the installer and `Install-Shortcut.ps1` do), so non-admin programs cannot replace the exe or a DLL that later runs elevated.
- **Its data folder is the user's.** `%LocalAppData%\BgOptimizer` (settings, action log, frozen-process list) can be changed by any program running as you, so the elevated app refuses to write, replace or delete there if the folder or file has been turned into a junction or symbolic link, rather than acting on wherever the link points. The check is best-effort, but the file names and contents are fixed, which keeps the exposure small.
- **Untrusted paths are never contacted.** Paths read from the registry (Run keys, Installed apps), shortcuts and scheduled tasks can be written by other programs. A network path (`\\server\share\...`) or a path on a removable or mapped drive is shown but never checked, opened or read, so a planted entry cannot make the elevated app connect to a server (which would also reveal the user's Windows credential hash) or stall on a dead drive. Nothing taken from those places is ever executed.
- **Publisher is a label, not a verification** (see the Startup tab). Binaries and the installer are **unsigned**: Windows SmartScreen / Smart App Control may warn or block them until they are code-signed. Each release lists SHA-256 checksums; compare them before running a download. Release files are built by the CI workflow and carry a signed **build attestation** (which commit and workflow produced them), which you can check with the GitHub CLI: `gh attestation verify <file> --repo Aleks108-heaven/windows-background-optimizer`. That proves where a file came from; it is not a substitute for code signing.
- **Build supply chain.** No known-vulnerable or deprecated NuGet packages at release time (`dotnet list package --vulnerable --include-transitive`). Test packages are pinned to exact versions and the CI actions to commit SHAs. There is no NuGet lock file yet; adding one (`RestorePackagesWithLockFile`) is a sensible next step.
- **Uninstall.** The Inno uninstaller deletes only the files the app writes in `%LocalAppData%\BgOptimizer` of the installing user (settings, action log, frozen-process list), by name, and then removes the folder only if it is empty. It never deletes recursively, so a junction or symbolic link planted there is not followed. Anything else you put in that folder is left for you to delete.

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
3. Copy that whole folder to the target laptop (USB drive, file share, etc.). You can run it straight from that folder, or install it properly with the **Shortcut script** or **Inno Setup installer** described below.
4. Run `BgOptimizer.exe`. Expect two prompts on first launch, both expected and safe to accept:
   - **Windows SmartScreen** ("Windows protected your PC") — the exe isn't code-signed, so files downloaded from the internet trigger this the first time. Click **More info** → **Run anyway**.
   - **UAC** (User Account Control) — the app needs admin rights to manage services and kill other processes. Click **Yes**.

If a security tool (Defender or a laptop OEM's antivirus) flags it heuristically because it can kill processes and reconfigure services, that's expected for this class of tool — check **Safety** above for what's actually locked down.

**Windows Smart App Control**, if enabled in enforcement mode (Settings → Privacy & security → Windows Security → App & browser control), judges every executable and DLL by its signature or Microsoft's reputation data, with no per-app override. Nothing in this repo is code-signed yet, so any unsigned file can be blocked: a freshly built `BgOptimizer.dll` was blocked this way during development (error `0x800711C7`, "An Application Control policy has blocked this file"), while a different build of the same code loaded fine. Installing doesn't change that: an unsigned installer `.exe` is judged the same way as an unsigned app, and a file that has been allowed once is not guaranteed to be allowed after the next build. The reliable fix is code-signing `BgOptimizer.exe`, `BgOptimizer.dll` and any `Setup.exe`; turning Smart App Control off is possible but permanent (Windows won't re-enable it without a reinstall), so it is not recommended. This is why this repo has a shortcut-creating PowerShell script alongside the installer — see below.

For wider distribution than "hand someone a folder," a tagged GitHub Release with the zip attached as a release asset would let anyone download it without needing repo access or a GitHub login — ask if you'd like that set up.

There are two ways to install, and you should use only one of them (they install into different folders, so using both leaves two copies):

| | Shortcut script | Inno Setup installer |
| --- | --- | --- |
| Needs | .NET 10 SDK to publish, elevated PowerShell | Nothing to build: download the CI artifact |
| Installs to | `C:\Program Files\BgOptimizer` | `C:\Program Files\Background Optimizer` |
| Uninstall entry in Settings → Apps | No | Yes |
| Works with unsigned files | Uses a build you made yourself | Unsigned `Setup.exe`: SmartScreen warns and Smart App Control may block it |

### Shortcut script (installs the app)

[installer/Install-Shortcut.ps1](installer/Install-Shortcut.ps1) **installs** the app: it copies a published build to `%ProgramFiles%\BgOptimizer`, creates Start Menu and Desktop shortcuts for all users, and removes any earlier per-user install under `%LocalAppData%\Programs\BgOptimizer` along with its shortcuts. It is a plain copy, not a packaged installer, so it does **not** add an entry to Settings → Apps.

It must be run from an **elevated (Administrator) PowerShell**, because it writes to Program Files and Program Files is writable only by administrators. That is deliberate: the app runs elevated, so if it lived in a user-writable folder such as `%LocalAppData%`, any non-admin process running as you could swap one of its DLLs and get code running as administrator the next time you accept the UAC prompt. The script does a clean replace (no stale files from older builds) and refuses to run while the app is open (exit it from the tray icon first).

Run both commands **from the repository folder**, the one that contains `BgOptimizer.csproj` and `installer\`, not from your user folder:

```powershell
cd "C:\path\to\Windows Background Optimizer"
dotnet publish BgOptimizer.csproj -c Release -r win-x64 --self-contained true -o publish
powershell -ExecutionPolicy Bypass -File installer\Install-Shortcut.ps1
```

To update, exit the app, publish again and re-run the script. To uninstall, exit the app, then delete `C:\Program Files\BgOptimizer` and the two `Background Optimizer.lnk` shortcuts (in `C:\ProgramData\Microsoft\Windows\Start Menu\Programs` and on the public Desktop). Optionally also delete `%LocalAppData%\BgOptimizer` (your saved settings and the frozen-process list). Changes the app made to Windows (persistent priority rules, firewall rules, tweaks, the hibernate setting) are not removed: undo them in the app first.

The app still prompts for admin on every launch either way — that's the manifest (`app.manifest`), not the shortcut.

### Inno Setup installer (unsigned)

[installer/BgOptimizer.iss](installer/BgOptimizer.iss) builds a conventional `BgOptimizer-Setup-<version>.exe`. CI builds it on every push and uploads it as the `BgOptimizer-Setup` artifact; to build it yourself, install [Inno Setup 6](https://jrsoftware.org/isinfo.php), publish as above, then run `ISCC.exe /DAppVersion=1.0.0 installer\BgOptimizer.iss` (output goes to `installer\Output\`). It installs for all users (it needs admin), offers an optional desktop icon, has a wizard in all seven app languages, offers to close a running copy before upgrading, and registers an uninstaller. Uninstalling removes the app and `%LocalAppData%\BgOptimizer` (including your saved settings, so they reset if you reinstall later), and leaves Windows changes the app made, as above.

**It is not code-signed.** Expect a SmartScreen warning, and on a machine with Smart App Control enforcing it may be blocked outright (see above). Until the exe, dll and `Setup.exe` are signed, prefer the shortcut script on such a machine.

### Switching between the two, or if both are installed

Running both installs leaves two copies of the app on the machine (`C:\Program Files\BgOptimizer` and `C:\Program Files\Background Optimizer`), and the shortcuts can point at different ones: both installs create a Desktop shortcut with the same name, `Background Optimizer.lnk` on the public Desktop, so whichever ran last owns it, while the Start menu gets a separate entry from each. If the app you launch isn't the build you expect, right-click `BgOptimizer.exe` in Task Manager → Details → **Open file location** to see which copy is running.

**Recommended on a machine with Smart App Control, until the files are signed:** keep the shortcut-script install, since an unsigned `Setup.exe` may be blocked.

1. Exit the app (tray icon → **Exit**).
2. Open **Settings → Apps → Installed apps**, find **Background Optimizer**, and uninstall it. This removes the Inno copy in `C:\Program Files\Background Optimizer` together with its Desktop and Start-menu shortcuts.
3. Re-run `installer\Install-Shortcut.ps1` from an elevated PowerShell (see above). If the Inno install created the Desktop shortcut (the optional desktop-icon checkbox), the uninstall in step 2 deletes that shared shortcut even if the script had created the same file earlier, and the script puts it back.

**To keep the Inno `Setup.exe` instead** (for example, to update with a newer one):

- **Updating:** run the newer `BgOptimizer-Setup-<version>.exe` over the existing install. It uses the same application ID, so it replaces the old copy rather than installing a second one, and it offers to close a running copy first (exit the app from the tray icon yourself if you'd rather). Get the new `Setup.exe` from the latest CI run's `BgOptimizer-Setup` artifact (a GitHub login is required), or build it yourself as described above.
- **Removing the shortcut-script copy** so only one copy remains: exit the app, delete `C:\Program Files\BgOptimizer` and the Start-menu shortcut `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Background Optimizer.lnk` (a single file, not the folder of the same name that Inno creates). The shared Desktop shortcut is overwritten by whichever install ran last, so re-run `Setup.exe` afterwards if it still points at the old folder.
- If Windows or Smart App Control blocks `Setup.exe`, nothing has been installed. Use the shortcut script instead.

### Troubleshooting

- `MSBUILD : error MSB1009: Project file does not exist`, `MSB1003: Specify a project or solution file`, `Couldn't find a project to run`, or `The argument 'installer\Install-Shortcut.ps1' ... does not exist`: the terminal is in the wrong folder (for example `C:\Users\<you>`). `cd` into the repository folder first.
- `Run this script from an elevated (Administrator) PowerShell`: open PowerShell with **Run as administrator**.
- `Background Optimizer is running. Exit it (tray icon -> Exit)`: close the app from its tray icon, then re-run the script.
- Starting the app does nothing: another copy is already running, and a build older than the "show the running copy" change quits silently instead of showing it. Open it from the tray icon (see **System tray**), or exit it there and start it again. If not even a tray icon appears, the launch was blocked before the app started; see the Windows event log (Application, `.NET Runtime` / `Application Error`) and a Code Integrity "blocked" entry.
- `An Application Control policy has blocked this file` / `0x800711C7`: Smart App Control blocked an unsigned file (see above).

## Build & run from source

Run these from the repository folder (the one containing `BgOptimizer.csproj`). The app requires administrator rights, so `dotnet run` only works from an elevated terminal:

```bash
dotnet build
dotnet run
```

Or run the compiled executable directly: `bin/Debug/net10.0-windows/BgOptimizer.exe`. If a command says it can't find a project, you are in the wrong folder (see **Troubleshooting** above).

## Tests

```bash
dotnet test tests/BgOptimizer.Tests
```

xUnit tests cover the safety rules (protected / interactive / security-tool / audio-app lists, the live-identity check on kill, priority, suspend and resume, firewall-block refusals), the idle-detection logic (including that its thresholds hold at a different refresh interval), the saved list of frozen processes and its recycled-PID check, the saved settings (round trip, corrupt/missing file, invalid values), the service start-mode mapping, the Category classifier, and the parsers for `powercfg` output and firewall rules (checked with non-English labels). Newer tests cover the PDH sampler on the real machine (and its failure paths), the thermal combine rules, the CPU-limit parser, the RAM cap, the trim cooldown, the three never-touch groups across every route, the permanent-rule cleanup (with an injected store and log), the `.disabled` repair (temp folders, a junction, view-only mode), and parity of every new string across all seven languages. Tests that act on a process only ever touch a `ping.exe` child they start themselves, and nothing changes system settings: the repair tests use temp folders and a throwaway key under `HKCU\Software\BgOptimizerTests` that is deleted afterwards, and none of them write to the real action log. CI runs them on every push.

## Project structure

```text
App.xaml(.cs)               Application entry point; single-instance check
MainWindow.xaml(.cs)        Main UI and event handlers (MainWindow.Checks.cs, .Settings.cs, .Cooling.cs and .Startup.cs hold the Checks tab, the Settings tab, its Cooling section and the Startup extras)
ConfirmDialog.xaml(.cs)    Themed confirmation dialog (lists affected items; Cancel is default for destructive actions)
Theme.xaml                  Colour tokens and control styles (buttons, tabs, grids, ComboBox)
Converters/                 Severity rules + brush/glyph/weight converters for the grids
Models/                     Data models (ProcessEntry, StartupEntry, ServiceEntry, ItemCategory) and the Checks tab's grid rows
Services/
  ProcessMonitor.cs         Process enumeration, CPU% sampling, kill/priority/suspend-resume
  StartupManager.cs         Startup item enumeration and enable/disable (Run keys, Startup folders, Scheduled Tasks) and the repair of leftover `.disabled` copies
  SvcManager.cs              Windows service enumeration and start-type/stop/start control (via WMI)
  ProtectedItems.cs         The safety deny-lists (protected, interactive, security-tool, audio apps, work agents, Windows/driver helpers, the user's own list)
  CleanupService.cs         Age-filtered, junction-safe deletion of temp files, old logs and caches (Cleanup tab)
  LeftoverScanner.cs        Leftovers of deleted programs: deleted-but-loaded files, broken Installed-apps entries, orphan folders (Checks tab)
  DuplicateFinder.cs        Duplicate files by size, partial hash, then SHA-256 (Checks tab)
  DriverStoreScanner.cs     Drivers stored more than once in the driver store (Checks tab; read-only)
  DriverStoreCleaner.cs     Removes an old, unused driver copy with pnputil /delete-driver (never /force); only for what the scan labelled older and not in use
  ExplorerLauncher.cs       Opens/selects a path in Explorer with the quoting Explorer needs (paths with spaces)
  LoadPressure.cs           'Sustained above the limit, released 5 points under it' state for load-triggered optimizing
  PathProbe.cs              "Definitely missing" vs "cannot tell" for file and folder paths
  SingleInstance.cs         Name of the signal a second launch sends to the running copy
  ViewOnlyMode.cs           The view-only switch and the refusal every changing service returns while it is on
  ViewOnlyLock.cs           The optional code that keeps view-only mode on (salted PBKDF2 hash, wrong-try delay)
  LockStore.cs              Administrator-only registry key holding the view-only mode, the code hash and the wrong-try count
  PdhQuery.cs               Performance Data Helper query (SafeHandle, System32-only DLL, bounds-checked buffers) used by the samplers below
  LoadSampler.cs / LoadWatch.cs  Disk and GPU load (PDH) and the 'sustained above the limit' rule for the load alert
  ThermalSampler.cs         Temperature, thermal limit and CPU speed readout (PDH, read-only)
  CoolingManager.cs         The CPU speed limit in the current power plan (powercfg, read back after writing)
  RamPolicy.cs              Caps the free-RAM threshold at a third of the installed memory
  StartupApprovedValue.cs   Reads/writes the Task Manager on/off flag for startup items
  StartupSafety.cs          Which startup items cannot be disabled here (security software, Windows components, this app)
  PublisherInfo.cs / CommandPath.cs  Publisher label for a file; program path from a command line (untrusted text, never run)
  RefreshPolicy.cs          When the periodic refresh runs (visible window, Auto-Optimize, the load alert or load-triggered optimizing on)
  ActionLog.cs              Persistent action log (%LocalAppData%\BgOptimizer\actions.log, rolls over at 1 MB)
  SuspendedStore.cs         Saves frozen process IDs to disk so a crash can't leave them frozen
  AppSettings.cs            Remembered checkbox/threshold/language choices (settings.json)
  BatteryManager.cs         Battery health and the hibernate-on-lid-close / hibernate-after setting
  ItemClassifier.cs         Best-effort Category labeling for Startup Apps/Services
  PriorityPersistence.cs    Persistent process priority via Image File Execution Options, and the start-up removal of leftover slow-down rules
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
  Install-Shortcut.ps1      Copies a published build to Program Files + creates Start Menu/Desktop shortcuts
  BgOptimizer.iss           Inno Setup script for an (unsigned) Setup.exe
icon.ico                    App icon (exe, window title bar, and tray all share this)
.github/workflows/build.yml CI: builds, tests, publishes and builds the installer on every push/PR
```

## CI/CD

Every push and pull request triggers a GitHub Actions build on `windows-latest` (required since this is a WPF app), which restores, builds, runs the tests, and publishes a self-contained `win-x64` build, then compiles the Inno Setup installer from it. Each run uploads two downloadable artifacts: `BgOptimizer` (the published folder) and `BgOptimizer-Setup` (the unsigned `Setup.exe`). See **Installing a pre-built copy** above for how to use them.
