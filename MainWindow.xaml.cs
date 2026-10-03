using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using BgOptimizer.Localization;
using BgOptimizer.Models;
using BgOptimizer.Services;
using Application = System.Windows.Application;

namespace BgOptimizer;

public partial class MainWindow : Window
{
    private readonly ProcessMonitor _processMonitor = new();
    private readonly StartupManager _startupManager = new();
    private readonly SvcManager _svcManager = new();
    private readonly SystemStats _systemStats = new();
    private readonly WorkloadOptimizer _workloadOptimizer = new();

    private readonly ObservableCollection<ProcessEntry> _processes = new();
    private readonly ObservableCollection<StartupEntry> _startupItems = new();
    private readonly ObservableCollection<ServiceEntry> _services = new();

    private readonly Dictionary<string, ProcessPriorityClass> _enforcedPriorities = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _enforceGaveUp = new();
    private readonly DispatcherTimer _refreshTimer;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    // AI Workload Mode: pid -> priority to restore once RAM pressure clears. Kept separate
    // from _enforcedPriorities (the manual "persist across restarts" feature) so the two
    // never fight each other or reapply each other's changes.
    // Names are kept next to the PIDs so a restore/resume can verify it is still talking to the
    // same process after a PID has been recycled.
    private readonly Dictionary<int, (string Name, ProcessPriorityClass Priority)> _autoThrottled = new();
    private readonly Dictionary<int, string> _autoSuspended = new();
    private const long BytesPerGb = 1024L * 1024 * 1024;
    private const long HysteresisBytes = 500L * 1024 * 1024;

    // Asked once per session before the first unattended suspend, since freezing a process
    // (unlike lowering its priority) is invisible unless the user knows to look for it.
    // Declining unchecks the box so the tick loop doesn't ask again until re-enabled.
    private bool _autoSuspendConsentGiven;
    private bool _tickRunning;

    // LIFO log of reversible changes made this session (Startup enable/disable,
    // Service start-type changes, Service stop). Not persisted across restarts -
    // undoing after closing the app isn't meaningfully different from just
    // reopening Startup Apps/Services and setting the value back by hand.
    private readonly Stack<Func<ActionResult>> _undoStack = new();

    // 30 samples at the 2s refresh interval mirrors Task Manager's 60-second window.
    private const int GraphHistoryLength = 30;
    private readonly List<double> _cpuHistory = new();
    private readonly List<double> _memHistory = new();

    public MainWindow()
    {
        InitializeComponent();
        PriorityPersistence.ClearInteractiveRules();
        SetupTrayIcon();

        ProcessGrid.ItemsSource = _processes;
        StartupGrid.ItemsSource = _startupItems;
        ServiceGrid.ItemsSource = _services;

        LanguageCombo.ItemsSource = Loc.Languages;
        LanguageCombo.SelectedItem = Loc.Languages.First(l => l.Code == Loc.Instance.Language);
        LanguageCombo.SelectionChanged += LanguageCombo_SelectionChanged;

        CpuGraphCanvas.SizeChanged += (_, _) => DrawSparkline(CpuGraphCanvas, _cpuHistory);
        MemoryGraphCanvas.SizeChanged += (_, _) => DrawSparkline(MemoryGraphCanvas, _memHistory);

        RefreshProcesses();
        RefreshStartup();
        RefreshServices();
        RefreshSecurity();
        BuildTweaks();
        UpdateSystemStats();

        Loc.Instance.PropertyChanged += (_, _) => { RefreshSecurity(); BuildTweaks(); };

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) =>
        {
            // AutoOptimizeTick can open a modal dialog; DispatcherTimer keeps ticking inside
            // that nested loop, so without this guard a second tick would stack another dialog.
            if (_tickRunning) return;
            _tickRunning = true;
            try
            {
                RefreshProcesses();
                UpdateSystemStats();
                RefreshBattery(); // cheap WMI reads only; the rest of RefreshSecurity() spawns netsh and stays on-demand
                AutoOptimizeTick();
            }
            finally { _tickRunning = false; }
        };
        _refreshTimer.Start();
    }

    // ---------- System stats ----------

    private void UpdateSystemStats()
    {
        var stats = _systemStats.Sample();
        CpuUsageText.Text = $"{stats.CpuPercent:F0}%";
        MemoryUsageText.Text = $"{FormatGb(stats.UsedMemoryBytes)} / {FormatGb(stats.TotalMemoryBytes)} GB";

        var memPercent = stats.TotalMemoryBytes > 0 ? stats.UsedMemoryBytes * 100.0 / stats.TotalMemoryBytes : 0;
        AppendHistory(_cpuHistory, stats.CpuPercent);
        AppendHistory(_memHistory, memPercent);

        DrawSparkline(CpuGraphCanvas, _cpuHistory);
        DrawSparkline(MemoryGraphCanvas, _memHistory);
    }

    private static string FormatGb(long bytes) => (bytes / 1024.0 / 1024.0 / 1024.0).ToString("F1");

    private static void AppendHistory(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > GraphHistoryLength) history.RemoveAt(0);
    }

    // Draws a Task-Manager-style filled sparkline: a scaled line over the history
    // buffer with a translucent fill beneath it, redrawn from scratch each tick
    // since the point count (and therefore the x-scale) changes as history fills up.
    private static void DrawSparkline(System.Windows.Controls.Canvas canvas, IReadOnlyList<double> history)
    {
        canvas.Children.Clear();
        var width = canvas.ActualWidth;
        var height = canvas.ActualHeight;
        if (width <= 0 || height <= 0 || history.Count < 2) return;

        var linePoints = new System.Windows.Media.PointCollection();
        for (var i = 0; i < history.Count; i++)
        {
            var x = width * i / (history.Count - 1);
            var y = height - Math.Clamp(history[i], 0, 100) / 100.0 * height;
            linePoints.Add(new System.Windows.Point(x, y));
        }

        var fillPoints = new System.Windows.Media.PointCollection(linePoints)
        {
            new System.Windows.Point(width, height),
            new System.Windows.Point(0, height),
        };

        // Accent comes from Theme.xaml so the graphs follow the palette.
        var accent = (Application.Current.TryFindResource("AccentBrush") as System.Windows.Media.SolidColorBrush)?.Color
                     ?? System.Windows.Media.Color.FromRgb(76, 141, 255);
        canvas.Children.Add(new System.Windows.Shapes.Polygon
        {
            Points = fillPoints,
            Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(70, accent.R, accent.G, accent.B)),
        });
        canvas.Children.Add(new System.Windows.Shapes.Polyline
        {
            Points = linePoints,
            Stroke = new System.Windows.Media.SolidColorBrush(accent),
            StrokeThickness = 1.5,
        });
    }

    // ---------- Search filtering ----------

    private static void ApplyFilter<T>(ObservableCollection<T> collection, string query, Func<T, string> textSelector)
    {
        var view = CollectionViewSource.GetDefaultView(collection);
        view.Filter = string.IsNullOrWhiteSpace(query)
            ? null
            : item => item is T entry && textSelector(entry).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ProcessSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplyFilter(_processes, ProcessSearchBox.Text, p => p.Name);

    private void StartupSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplyFilter(_startupItems, StartupSearchBox.Text, s => $"{s.Name} {s.Command}");

    private void ServiceSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ApplyFilter(_services, ServiceSearchBox.Text, s => $"{s.DisplayName} {s.Name}");

    private void LanguageCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is Loc.LanguageOption option)
            Loc.Instance.Language = option.Code;
    }

    // ---------- System tray ----------

    private void SetupTrayIcon()
    {
        var openItem = new System.Windows.Forms.ToolStripMenuItem(Loc.Instance["Tray_Open"]);
        openItem.Click += (_, _) => RestoreFromTray();

        var exitItem = new System.Windows.Forms.ToolStripMenuItem(Loc.Instance["Tray_Exit"]);
        exitItem.Click += (_, _) =>
        {
            _trayIcon!.Visible = false;
            Application.Current.Shutdown();
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Visible = true,
            Text = Loc.Instance["AppTitle"],
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        Loc.Instance.PropertyChanged += (_, _) =>
        {
            _trayIcon.Text = Loc.Instance["AppTitle"];
            openItem.Text = Loc.Instance["Tray_Open"];
            exitItem.Text = Loc.Instance["Tray_Exit"];
        };

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
                Hide();
        };

        // Only this app can undo what Auto-Optimize did: a process it froze stays frozen (and one it
        // throttled stays throttled) after exit unless it is released here.
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            RestoreAutoThrottled();
            _trayIcon.Dispose();
        };
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        var resourceInfo = Application.GetResourceStream(new Uri("icon.ico", UriKind.Relative));
        using var stream = resourceInfo!.Stream;
        return new System.Drawing.Icon(stream);
    }

    // The log is a long-lived tray app's only record, so it is capped; and failures get a glyph plus
    // the danger colour (not colour alone) so they can't be mistaken for successes.
    private const int MaxLogEntries = 500;

    private void Log(string message) => AddLog(message, failed: false);

    private void Log(ActionResult result) => AddLog(result.Message, failed: !result.Ok);

    private void AddLog(string message, bool failed)
    {
        var item = new System.Windows.Controls.ListBoxItem
        {
            Content = $"[{DateTime.Now:HH:mm:ss}] {(failed ? "✖ " : "")}{message}",
        };
        if (failed) item.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        LogList.Items.Insert(0, item);
        while (LogList.Items.Count > MaxLogEntries) LogList.Items.RemoveAt(LogList.Items.Count - 1);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogList.Items.Clear();

    // ---------- Processes ----------

    // Processes are returned in stable PID order (not re-sorted by CPU% each tick), and
    // ProcessMonitor reuses the same ProcessEntry instances across calls. So refreshing
    // only needs to add/remove/reorder the handful of PIDs that actually changed instead
    // of rebuilding the whole ObservableCollection - the previous Clear()+re-Add() forced
    // the DataGrid to regenerate every row's visual tree every 2 seconds, which was the
    // single biggest source of the app's own CPU usage. This also means multi-selection
    // now survives a refresh for free, since the selected objects are never replaced.
    private void RefreshProcesses()
    {
        var fresh = _processMonitor.Refresh();

        // Windows respawns helpers like SearchFilterHost under new PIDs at Normal priority,
        // so a one-shot priority change is lost. Re-apply it to every new instance by name.
        if (_enforcedPriorities.Count > 0)
        {
            foreach (var p in fresh)
            {
                // One attempt per process: if it can't be changed (access denied, priority capped)
                // retrying every 2s would just repeat the same failure, so it is logged once instead.
                // Browsers manage their own helper priorities (background tabs go Idle on purpose) and
                // their short-lived helpers; Windows already applies the persisted rule at launch, so
                // fighting them here only adds churn.
                if (!p.IsProtected && !ProtectedItems.IsInteractive(p.Name) && !_enforceGaveUp.Contains(p.Pid)
                    && _enforcedPriorities.TryGetValue(p.Name, out var wanted)
                    && p.Priority != wanted.ToString())
                {
                    var applied = ProcessMonitor.SetPriority(p.Pid, p.Name, wanted);
                    if (!applied.Ok)
                    {
                        _enforceGaveUp.Add(p.Pid);
                        // A helper that exited between the refresh and this call is not an error worth logging.
                        if (ProcessMonitor.IsRunning(p.Pid)) Log(applied);
                    }
                }
            }
        }
        var freshPids = new HashSet<int>(fresh.Count);
        foreach (var p in fresh) freshPids.Add(p.Pid);
        _enforceGaveUp.RemoveWhere(pid => !freshPids.Contains(pid));

        for (var i = _processes.Count - 1; i >= 0; i--)
        {
            if (!freshPids.Contains(_processes[i].Pid))
                _processes.RemoveAt(i);
        }

        for (var i = 0; i < fresh.Count; i++)
        {
            var entry = fresh[i];
            var currentIndex = i < _processes.Count && _processes[i].Pid == entry.Pid
                ? i
                : IndexOfPid(_processes, entry.Pid);

            if (currentIndex == -1) _processes.Insert(Math.Min(i, _processes.Count), entry);
            else
            {
                if (currentIndex != i) _processes.Move(currentIndex, i);
                // Same PID but a different instance means the PID was recycled by a new process.
                if (!ReferenceEquals(_processes[i], entry)) _processes[i] = entry;
            }
        }
    }

    private static int IndexOfPid(ObservableCollection<ProcessEntry> collection, int pid)
    {
        for (var i = 0; i < collection.Count; i++)
            if (collection[i].Pid == pid) return i;
        return -1;
    }

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e) => RefreshProcesses();

    private void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProcessGrid.SelectedItems.Cast<ProcessEntry>().ToList();
        if (selected.Count == 0) { Log(Loc.Instance["Msg_NothingSelected"]); return; }

        var targets = new List<ProcessEntry>();
        foreach (var entry in selected)
        {
            if (entry.IsProtected) Log(Loc.Instance.Format("Msg_BlockedProcess", entry.Name));
            else targets.Add(entry);
        }
        if (targets.Count == 0) return;

        var items = targets.Select(p => $"{p.Name}  —  PID {p.Pid}, {p.MemoryDisplay}").ToList();
        if (!ConfirmDialog.Show(this, Loc.Instance["Dlg_KillTitle"], Loc.Instance["Dlg_KillMessage"], items,
                danger: true, Loc.Instance["Btn_KillConfirm"], Loc.Instance["Dlg_Cancel"]))
            return;

        foreach (var entry in targets)
            Log(ProcessMonitor.Kill(entry.Pid, entry.Name));

        RefreshProcesses();
    }

    private void FreeRamSelected_Click(object sender, RoutedEventArgs e) =>
        TrimProcesses(ProcessGrid.SelectedItems.Cast<ProcessEntry>().ToList());

    private void FreeRamAll_Click(object sender, RoutedEventArgs e) =>
        TrimProcesses(_processes.Where(p => !p.IsProtected && p.MemoryBytes > 20L * 1024 * 1024).ToList());

    private void TrimProcesses(List<ProcessEntry> entries)
    {
        int ok = 0;
        double freed = 0;
        foreach (var entry in entries)
        {
            if (entry.IsProtected) { Log(Loc.Instance.Format("Msg_BlockedProcess", entry.Name)); continue; }
            var before = entry.MemoryBytes;
            var result = ProcessMonitor.TrimMemory(entry.Pid, entry.Name);
            if (result.Ok) { ok++; freed += double.Parse(result.Args[2].ToString()!, System.Globalization.CultureInfo.CurrentCulture); }
            else if (entries.Count == 1) Log(result);
        }
        Log(Loc.Instance.Format("Msg_TrimSummary", ok, freed.ToString("F0")));
        RefreshProcesses();
    }

    // Lowers priority + trims RAM for telemetry/vendor helper processes only. Core, dev tools,
    // protected and interactive apps are never touched; nothing is killed or disabled.
    private void LaptopSaver_Click(object sender, RoutedEventArgs e)
    {
        var targets = _processes
            .Where(p => !p.IsProtected && !ProtectedItems.IsInteractive(p.Name) && !ProtectedItems.IsSecurityTool(p.Name)
                && ItemClassifier.ClassifyStartup(p.Name, p.Name) is ItemCategory.Telemetry or ItemCategory.VendorDiag)
            .ToList();
        if (targets.Count == 0) { Log(Loc.Instance["Msg_SaverNone"]); return; }

        var items = targets.Select(p => $"{p.Name}  —  PID {p.Pid}, {p.MemoryDisplay}, {p.Priority}").ToList();
        if (!ConfirmDialog.Show(this, Loc.Instance["Dlg_SaverTitle"], Loc.Instance["Dlg_SaverMessage"], items,
                danger: false, Loc.Instance["Dlg_Confirm"], Loc.Instance["Dlg_Cancel"]))
            return;

        var previous = new List<(int Pid, string Name, ProcessPriorityClass Priority)>();
        foreach (var t in targets)
        {
            if (!Enum.TryParse<ProcessPriorityClass>(t.Priority, out var old)) continue;
            var r = ProcessMonitor.SetPriority(t.Pid, t.Name, ProcessPriorityClass.BelowNormal);
            Log(r);
            if (r.Ok) previous.Add((t.Pid, t.Name, old));
        }
        TrimProcesses(targets);

        if (previous.Count > 0)
            _undoStack.Push(() =>
            {
                int n = 0;
                foreach (var (pid, name, prio) in previous)
                    if (ProcessMonitor.SetPriority(pid, name, prio).Ok) n++;
                return new ActionResult(true, "Msg_SaverUndone", n);
            });
    }

    private void ApplyPriority_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProcessGrid.SelectedItems.Cast<ProcessEntry>().ToList();
        if (selected.Count == 0) { Log(Loc.Instance["Msg_NothingSelected"]); return; }

        var priorityName = (PriorityCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Normal";
        if (!Enum.TryParse<ProcessPriorityClass>(priorityName, out var priority)) return;

        _enforceGaveUp.Clear(); // a new manual apply deserves a fresh attempt on every instance
        var persist = PersistPriorityCheck.IsChecked == true;
        var allowInteractive = AllowInteractiveThrottleCheck.IsChecked == true;
        foreach (var entry in selected)
        {
            if (entry.IsProtected) { Log(Loc.Instance.Format("Msg_BlockedProcess", entry.Name)); continue; }

            Log(ProcessMonitor.SetPriority(entry.Pid, entry.Name, priority, allowInteractive));

            // Sync the persistent IFEO rule to the checkbox state: checked writes
            // it, unchecked clears whatever rule (if any) was there before, so
            // toggling the box off and re-applying is how you undo persistence.
            // Only a rule the persistence layer actually accepted is re-applied by the refresh
            // loop; a refused one (e.g. Idle on a browser) must not be enforced behind its back.
            if (persist)
            {
                var persisted = PriorityPersistence.SetPersistent(entry.Name, priority);
                Log(persisted);
                if (persisted.Ok) _enforcedPriorities[entry.Name] = priority;
                else _enforcedPriorities.Remove(entry.Name);
            }
            else
            {
                _enforcedPriorities.Remove(entry.Name);
                if (PriorityPersistence.IsPersisted(entry.Name)) Log(PriorityPersistence.ClearPersistent(entry.Name));
            }
        }
        RefreshProcesses();
    }

    // ---------- AI Workload Mode ----------

    // Runs every 2s alongside RefreshProcesses. Only acts when the checkbox is on; entirely
    // idle-CPU driven (see WorkloadOptimizer), never touches the browser or whatever window
    // is focused, and only ever uses BelowNormal — never Idle, which is what broke Turnstile.
    private void AutoOptimizeTick()
    {
        var foregroundOwners = ForegroundWindow.GetForegroundPids();
        // Everything that belongs to the app being used (helpers, Store-app process) is exempt.
        var foreground = WorkloadOptimizer.ExpandForeground(_processes, foregroundOwners);
        foreach (var p in _processes)
        {
            p.IsForeground = foregroundOwners.Contains(p.Pid);
            p.IsSuspended = _autoSuspended.ContainsKey(p.Pid);
        }
        _workloadOptimizer.UpdateStreaks(_processes, foreground);
        ReleaseForeground(foreground);

        if (AutoOptimizeCheck.IsChecked != true)
        {
            if (_autoThrottled.Count > 0 || _autoSuspended.Count > 0) RestoreAutoThrottled();
            return;
        }

        // Invariant parsing (accepting a comma too): with the current culture "2.5" reads as 25 in
        // locales where "." is the thousands separator, which would keep throttling permanently on.
        if (!double.TryParse(AutoOptimizeThresholdBox.Text.Trim().Replace(',', '.'),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var thresholdGb) || thresholdGb <= 0) thresholdGb = 2;
        var thresholdBytes = (long)(thresholdGb * BytesPerGb);

        var stats = _systemStats.Sample();
        var freeBytes = stats.TotalMemoryBytes - stats.UsedMemoryBytes;

        if (freeBytes < thresholdBytes)
        {
            foreach (var entry in _workloadOptimizer.GetIdleCandidates(_processes, foreground))
            {
                if (_autoThrottled.ContainsKey(entry.Pid)) continue;
                if (!Enum.TryParse<ProcessPriorityClass>(entry.Priority, out var original)) continue;

                ProcessMonitor.TrimMemory(entry.Pid, entry.Name);
                var result = ProcessMonitor.SetPriority(entry.Pid, entry.Name, ProcessPriorityClass.BelowNormal);
                if (result.Ok)
                {
                    _autoThrottled[entry.Pid] = (entry.Name, original);
                    Log(Loc.Instance.Format("Msg_AutoThrottled", entry.Name, entry.Pid));
                }
            }

            if (AllowSuspendCheck.IsChecked == true)
            {
                var longIdle = _workloadOptimizer.GetLongIdleCandidates(_processes, foreground);
                if (longIdle.Count > 0 && !_autoSuspendConsentGiven)
                {
                    if (!ConfirmDialog.Show(this, Loc.Instance["Dlg_AutoSuspendTitle"], Loc.Instance["Dlg_AutoSuspendMessage"],
                            null, danger: true, Loc.Instance["Dlg_Confirm"], Loc.Instance["Dlg_Cancel"]))
                    {
                        AllowSuspendCheck.IsChecked = false;
                        longIdle = new List<ProcessEntry>();
                    }
                    else
                    {
                        _autoSuspendConsentGiven = true;
                    }
                }

                foreach (var entry in longIdle)
                {
                    if (_autoSuspended.ContainsKey(entry.Pid)) continue;
                    var result = ProcessMonitor.SuspendProcess(entry.Pid, entry.Name);
                    if (result.Ok)
                    {
                        _autoSuspended[entry.Pid] = entry.Name;
                        entry.IsSuspended = true;
                        Log(result);
                    }
                }
            }

            SuspendedCountText.Text = _autoSuspended.Count > 0
                ? Loc.Instance.Format("Lbl_SuspendedCount", _autoSuspended.Count)
                : "";
        }
        else if (freeBytes > thresholdBytes + HysteresisBytes)
        {
            RestoreAutoThrottled();
        }
    }

    private void RestoreAutoThrottled()
    {
        foreach (var (pid, (name, priority)) in _autoThrottled)
            ProcessMonitor.SetPriority(pid, name, priority);
        if (_autoThrottled.Count > 0) Log(Loc.Instance.Format("Msg_AutoRestored", _autoThrottled.Count));
        _autoThrottled.Clear();

        ResumeAutoSuspended();
    }

    private void ResumeAutoSuspended()
    {
        foreach (var (pid, name) in _autoSuspended.ToList())
            ResumeOne(pid, name);
        _autoSuspended.Clear();
        SuspendedCountText.Text = "";
    }

    private void ResumeOne(int pid, string name)
    {
        var result = ProcessMonitor.ResumeProcess(pid, name);
        if (result.Ok) Log(result);
        var entry = _processes.FirstOrDefault(p => p.Pid == pid);
        if (entry != null) entry.IsSuspended = false;
    }

    // Auto-Optimize must never keep holding the app the user just switched to: anything it froze
    // or slowed that now belongs to the foreground is released immediately, not when RAM recovers.
    private void ReleaseForeground(HashSet<int> foreground)
    {
        var resumed = _autoSuspended.Keys.Where(foreground.Contains).ToList();
        foreach (var pid in resumed)
        {
            ResumeOne(pid, _autoSuspended[pid]);
            _autoSuspended.Remove(pid);
        }
        if (resumed.Count > 0)
            SuspendedCountText.Text = _autoSuspended.Count > 0
                ? Loc.Instance.Format("Lbl_SuspendedCount", _autoSuspended.Count)
                : "";

        foreach (var pid in _autoThrottled.Keys.Where(foreground.Contains).ToList())
        {
            var (name, priority) = _autoThrottled[pid];
            ProcessMonitor.SetPriority(pid, name, priority);
            _autoThrottled.Remove(pid);
        }
    }

    private void ResumeAllSuspended_Click(object sender, RoutedEventArgs e) => ResumeAutoSuspended();

    // Kills a single memory-hog PID rather than the whole app: safe for browser helper
    // processes (the browser respawns a fresh one), and blocked outright for anything
    // protected or interactive by the same rule every other destructive action follows.
    private void RestartHog_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ProcessEntry entry) return;
        if (entry.IsProtected) { Log(Loc.Instance.Format("Msg_BlockedProcess", entry.Name)); return; }

        var items = new List<string> { $"{entry.Name}  —  PID {entry.Pid}, {entry.MemoryDisplay}" };
        if (!ConfirmDialog.Show(this, Loc.Instance["Dlg_KillTitle"], Loc.Instance["Dlg_KillMessage"], items,
                danger: true, Loc.Instance["Btn_KillConfirm"], Loc.Instance["Dlg_Cancel"]))
            return;

        Log(ProcessMonitor.Kill(entry.Pid, entry.Name));
        RefreshProcesses();
    }

    // ---------- Startup ----------

    // Enumerating startup items (registry, folders, Task Scheduler) is slow, so it runs on a worker
    // thread and only the collection update happens on the UI thread. A newer request supersedes an
    // older one still in flight, so a slow earlier result can never overwrite a fresher list.
    private int _startupLoadId;

    private async void RefreshStartup()
    {
        var id = ++_startupLoadId;
        try
        {
            var items = await Task.Run(() => _startupManager.GetAll());
            if (id != _startupLoadId) return;
            _startupItems.Clear();
            foreach (var s in items) _startupItems.Add(s);
        }
        catch (Exception ex) { Log(ex.Message); }
    }

    private void RefreshStartup_Click(object sender, RoutedEventArgs e) => RefreshStartup();

    private void EnableStartup_Click(object sender, RoutedEventArgs e)
    {
        var selected = StartupGrid.SelectedItems.Cast<StartupEntry>().ToList();
        if (selected.Count == 0) { Log(Loc.Instance["Msg_NothingSelected"]); return; }

        foreach (var entry in selected)
        {
            var wasEnabled = entry.Enabled;
            var result = _startupManager.SetEnabled(entry, true);
            Log(result);
            if (result.Ok) _undoStack.Push(() => _startupManager.SetEnabled(entry, wasEnabled));
        }

        RefreshStartup();
    }

    private void DisableStartup_Click(object sender, RoutedEventArgs e)
    {
        var selected = StartupGrid.SelectedItems.Cast<StartupEntry>().ToList();
        if (selected.Count == 0) { Log(Loc.Instance["Msg_NothingSelected"]); return; }

        var items = selected.Select(s => s.Name).ToList();
        if (!ConfirmDialog.Show(this, Loc.Instance["Dlg_ConfirmTitle"], Loc.Instance["Dlg_ConfirmDisableStartupMsg"], items,
                danger: true, Loc.Instance["Dlg_Confirm"], Loc.Instance["Dlg_Cancel"]))
            return;

        if (AutoRestorePointCheck.IsChecked == true)
            Log(SystemRestoreService.CreateRestorePoint("BgOptimizer: before disabling startup items"));

        foreach (var entry in selected)
        {
            var wasEnabled = entry.Enabled;
            var result = _startupManager.SetEnabled(entry, false);
            Log(result);
            if (result.Ok) _undoStack.Push(() => _startupManager.SetEnabled(entry, wasEnabled));
        }

        RefreshStartup();
    }

    private void CreateRestorePoint_Click(object sender, RoutedEventArgs e) =>
        Log(SystemRestoreService.CreateRestorePoint("BgOptimizer: manual restore point"));

    // ---------- Security ----------

    private readonly SecurityManager _security = new();
    private readonly BatteryManager _battery = new();

    private bool _securityRefreshing;
    private bool _securityRefreshQueued;

    // The reads spawn netsh (several times, one dumping every firewall rule) and powercfg, so
    // they run on a worker thread; only the text/ItemsSource updates touch the UI. A request
    // that arrives mid-refresh (e.g. a language switch) is queued and re-run afterwards so the
    // labels never end up stuck in the previous language.
    private async void RefreshSecurity()
    {
        if (_securityRefreshing) { _securityRefreshQueued = true; return; }
        _securityRefreshing = true;
        try
        {
            do
            {
                _securityRefreshQueued = false;
                var snapshot = await Task.Run(() => (
                    Firewall: _security.GetFirewallProfiles(),
                    WinRm: _security.GetWinRm(),
                    SmbBlocked: _security.IsSmbBlocked(),
                    BlockedApps: _security.GetBlockedApps(),
                    Hibernate: _battery.IsHibernateOnLidClose()));

                string Fw(bool? on) => Loc.Instance[on switch { true => "Sec_On", false => "Sec_Off", null => "Sec_Unknown" }];
                var (domain, priv, pub) = snapshot.Firewall;
                FirewallStatusText.Text = Loc.Instance.Format("Sec_FwLine", Fw(domain), Fw(priv), Fw(pub));
                WinRmStatusText.Text = Loc.Instance.Format("Sec_WinRmLine", snapshot.WinRm.Status, snapshot.WinRm.StartMode);
                SmbStatusText.Text = Loc.Instance.Format("Sec_SmbLine",
                    Loc.Instance[snapshot.SmbBlocked ? "Sec_Blocked" : "Sec_NotBlocked"]);

                BlockedAppsCombo.ItemsSource = snapshot.BlockedApps;
                if (BlockedAppsCombo.Items.Count > 0) BlockedAppsCombo.SelectedIndex = 0;

                RefreshBattery();
                HibernateStatusText.Text = Loc.Instance.Format("Sec_HibernateLine",
                    Loc.Instance[snapshot.Hibernate switch { true => "Sec_HibernateOn", false => "Sec_HibernateOff", null => "Sec_HibernateUnknown" }]);
            } while (_securityRefreshQueued);
        }
        catch (Exception ex)
        {
            Log(Loc.Instance.Format("Msg_SecurityRefreshFailed", ex.Message));
        }
        finally { _securityRefreshing = false; }
    }

    // Cheap WMI reads only (no process spawn) - safe to call every 2s so charge % doesn't go stale.
    private void RefreshBattery()
    {
        var h = _battery.GetHealth();
        if (h.Percent is null)
        {
            BatteryHealthText.Text = Loc.Instance["Sec_BatteryUnavailable"];
            return;
        }

        var state = h.Charging switch { true => "Sec_Charging", false => "Sec_Discharging", null => "Sec_Idle" };
        var rateText = h.RateMw is int mw
            ? Loc.Instance.Format("Sec_BatteryRate", (mw / 1000.0).ToString("F1"), Loc.Instance[state])
            : Loc.Instance[state];

        BatteryHealthText.Text = h.DesignMWh is int design && h.FullChargeMWh is int full && design > 0
            ? Loc.Instance.Format("Sec_BatteryLine", full, design, full * 100 / design, h.Percent, rateText)
            : h.FullChargeMWh is int fullOnly
                ? Loc.Instance.Format("Sec_BatteryLineNoDesign", fullOnly, h.Percent, rateText)
                : Loc.Instance["Sec_BatteryUnavailable"];
    }

    // ---------- Tweaks ----------

    private readonly TweakManager _tweaks = new();

    private void BuildTweaks()
    {
        TweakList.Children.Clear();
        foreach (var tweak in TweakManager.Tweaks)
        {
            var text = new System.Windows.Controls.StackPanel();
            text.Children.Add(new System.Windows.Controls.TextBlock { Text = Loc.Instance["Tw_" + tweak.Id], FontWeight = FontWeights.SemiBold });
            text.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Loc.Instance["Tw_" + tweak.Id + "_d"] + (tweak.NeedsAdmin ? "  " + Loc.Instance["Tw_Admin"] : ""),
                Foreground = (System.Windows.Media.Brush)FindResource("TextFaintBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            var box = new System.Windows.Controls.CheckBox
            {
                Content = text, Tag = tweak, IsChecked = _tweaks.IsApplied(tweak), Margin = new Thickness(0, 0, 0, 10),
            };
            box.Click += Tweak_Click;
            TweakList.Children.Add(box);
        }
    }

    private void Tweak_Click(object sender, RoutedEventArgs e)
    {
        var box = (System.Windows.Controls.CheckBox)sender;
        var tweak = (TweakManager.Tweak)box.Tag;
        var enable = box.IsChecked == true;

        var msg = Loc.Instance["Tw_" + tweak.Id] + "\n\n" + Loc.Instance["Tw_" + tweak.Id + "_d"];
        if (!ConfirmDialog.Show(this, Loc.Instance[enable ? "Dlg_TweakOn" : "Dlg_TweakOff"], msg, null,
                danger: false, Loc.Instance["Dlg_Confirm"], Loc.Instance["Dlg_Cancel"]))
        {
            box.IsChecked = !enable;
            return;
        }

        var (result, snapshot) = _tweaks.Set(tweak, enable);
        Log(result);
        if (result.Ok) _undoStack.Push(() => { var r = _tweaks.Restore(snapshot); BuildTweaks(); return r; });
        BuildTweaks();
    }

    private void AppBlock_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs (*.exe)|*.exe" };
        if (dlg.ShowDialog(this) != true) return;
        var path = dlg.FileName;
        ApplySecurityChangeText(Loc.Instance.Format("Dlg_SecAppBlock", path),
            () => _security.SetAppBlocked(path, true), () => _security.SetAppBlocked(path, false), danger: true);
    }

    private void AppUnblock_Click(object sender, RoutedEventArgs e)
    {
        if (BlockedAppsCombo.SelectedItem is not string path) return;
        ApplySecurityChangeText(Loc.Instance.Format("Dlg_SecAppUnblock", path),
            () => _security.SetAppBlocked(path, false), () => _security.SetAppBlocked(path, true), danger: false);
    }

    private void HibernateOnLidEnable_Click(object sender, RoutedEventArgs e) =>
        ApplySecurityChange("Dlg_SecHibernateOn", () => _battery.SetHibernateOnLidClose(true), () => _battery.SetHibernateOnLidClose(false), danger: false);

    private void HibernateOnLidDisable_Click(object sender, RoutedEventArgs e) =>
        ApplySecurityChange("Dlg_SecHibernateOff", () => _battery.SetHibernateOnLidClose(false), () => _battery.SetHibernateOnLidClose(true), danger: false);

    private void RefreshSecurity_Click(object sender, RoutedEventArgs e) => RefreshSecurity();

    // Confirms, applies, logs, and registers the inverse on the undo stack.
    private void ApplySecurityChange(string confirmKey, Func<ActionResult> apply, Func<ActionResult> revert, bool danger) =>
        ApplySecurityChangeText(Loc.Instance[confirmKey], apply, revert, danger);

    private void ApplySecurityChangeText(string confirmText, Func<ActionResult> apply, Func<ActionResult> revert, bool danger)
    {
        if (!ConfirmDialog.Show(this, Loc.Instance["Dlg_SecTitle"], confirmText, null,
                danger, Loc.Instance["Dlg_Confirm"], Loc.Instance["Dlg_Cancel"]))
            return;

        var result = apply();
        Log(result);
        if (result.Ok) _undoStack.Push(revert);
        else _trayIcon?.ShowBalloonTip(4000, Loc.Instance["Dlg_SecTitle"], result.Message, System.Windows.Forms.ToolTipIcon.Error);
        RefreshSecurity();
    }

    private void WinRmDisable_Click(object sender, RoutedEventArgs e) =>
        ApplySecurityChange("Dlg_SecWinRmOff", () => _security.SetWinRm(false), () => _security.SetWinRm(true), danger: true);

    private void WinRmEnable_Click(object sender, RoutedEventArgs e) =>
        ApplySecurityChange("Dlg_SecWinRmOn", () => _security.SetWinRm(true), () => _security.SetWinRm(false), danger: false);

    private void SmbBlock_Click(object sender, RoutedEventArgs e) =>
        ApplySecurityChange("Dlg_SecSmbBlock", () => _security.SetSmbBlocked(true), () => _security.SetSmbBlocked(false), danger: true);

    private void SmbUnblock_Click(object sender, RoutedEventArgs e) =>
        ApplySecurityChange("Dlg_SecSmbUnblock", () => _security.SetSmbBlocked(false), () => _security.SetSmbBlocked(true), danger: false);

    private void UndoLast_Click(object sender, RoutedEventArgs e)
    {
        if (_undoStack.Count == 0) { Log(Loc.Instance["Msg_UndoNone"]); return; }

        var undo = _undoStack.Pop();
        var result = undo();
        Log(Loc.Instance.Format("Msg_UndoApplied", result.Message));

        RefreshStartup();
        RefreshServices();
        RefreshSecurity();
    }

    // ---------- Services ----------

    private int _servicesLoadId;

    private async void RefreshServices()
    {
        var id = ++_servicesLoadId;
        try
        {
            var items = await Task.Run(() => _svcManager.GetAll());
            if (id != _servicesLoadId) return;
            _services.Clear();
            foreach (var s in items) _services.Add(s);
        }
        catch (Exception ex) { Log(ex.Message); }
    }

    private void RefreshServices_Click(object sender, RoutedEventArgs e) => RefreshServices();

    private void SetServiceMode(string mode, bool needsConfirm)
    {
        var selected = ServiceGrid.SelectedItems.Cast<ServiceEntry>().ToList();
        if (selected.Count == 0) { Log(Loc.Instance["Msg_NothingSelected"]); return; }

        var targets = new List<ServiceEntry>();
        foreach (var entry in selected)
        {
            if (entry.IsProtected) Log(Loc.Instance.Format("Msg_BlockedService", entry.DisplayName));
            else targets.Add(entry);
        }
        if (targets.Count == 0) return;

        if (needsConfirm)
        {
            var items = targets.Select(s => s.DisplayName).ToList();
            var message = Loc.Instance.Format("Dlg_ConfirmServiceModeMsg", mode);
            if (!ConfirmDialog.Show(this, Loc.Instance["Dlg_ConfirmTitle"], message, items,
                    danger: true, Loc.Instance["Dlg_Confirm"], Loc.Instance["Dlg_Cancel"]))
                return;

            if (AutoRestorePointCheck.IsChecked == true)
                Log(SystemRestoreService.CreateRestorePoint($"BgOptimizer: services set to {mode}"));
        }

        foreach (var entry in targets)
        {
            var previousStartType = entry.StartType;
            var result = _svcManager.SetStartMode(entry, mode);
            Log(result);
            if (result.Ok) _undoStack.Push(() => _svcManager.SetStartMode(entry, previousStartType));
        }

        RefreshServices();
    }

    private void ServiceAuto_Click(object sender, RoutedEventArgs e) => SetServiceMode("Auto", needsConfirm: false);
    private void ServiceManual_Click(object sender, RoutedEventArgs e) => SetServiceMode("Manual", needsConfirm: false);
    private void ServiceDisabled_Click(object sender, RoutedEventArgs e) => SetServiceMode("Disabled", needsConfirm: true);

    private void ServiceStop_Click(object sender, RoutedEventArgs e)
    {
        var selected = ServiceGrid.SelectedItems.Cast<ServiceEntry>().ToList();
        if (selected.Count == 0) { Log(Loc.Instance["Msg_NothingSelected"]); return; }

        var targets = new List<ServiceEntry>();
        foreach (var entry in selected)
        {
            if (entry.IsProtected) Log(Loc.Instance.Format("Msg_BlockedService", entry.DisplayName));
            else targets.Add(entry);
        }
        if (targets.Count == 0) return;

        var items = targets.Select(s => s.DisplayName).ToList();
        if (!ConfirmDialog.Show(this, Loc.Instance["Dlg_ConfirmTitle"], Loc.Instance["Dlg_ConfirmStopServiceMsg"], items,
                danger: true, Loc.Instance["Dlg_Confirm"], Loc.Instance["Dlg_Cancel"]))
            return;

        if (AutoRestorePointCheck.IsChecked == true)
            Log(SystemRestoreService.CreateRestorePoint("BgOptimizer: before stopping services"));

        foreach (var entry in targets)
        {
            var result = _svcManager.Stop(entry);
            Log(result);
            if (result.Ok) _undoStack.Push(() => _svcManager.Start(entry));
        }

        RefreshServices();
    }
}
