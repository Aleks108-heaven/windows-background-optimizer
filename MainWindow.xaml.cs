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
    // Refresh cadence for the process list, graphs and Auto-Optimize. 2s was needlessly heavy for the
    // app's own CPU; 5s still feels live. Thresholds elsewhere are derived from this, not hard-coded.
    private const int RefreshSeconds = 5;
    private readonly WorkloadOptimizer _workloadOptimizer = new(RefreshSeconds);

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

    // Keeps a 60-second window (like Task Manager) whatever the refresh interval is.
    private const int GraphHistoryLength = 60 / RefreshSeconds;
    private readonly List<double> _cpuHistory = new();
    private readonly List<double> _memHistory = new();

    // True while saved settings are being applied to the controls, so that doesn't trigger a save per control.
    private bool _loadingSettings;

    public MainWindow()
    {
        // The language is applied before the UI is built so it never flashes the default one first.
        var settings = AppSettings.Load();
        Loc.Instance.Language = settings.Language;
        ActionLog.Write($"=== Started. AutoOptimize={settings.AutoOptimize}, AllowSuspend={settings.AllowSuspend}, " +
                        $"FreeRamThresholdGb={settings.FreeRamThresholdGb}, PersistPriority={settings.PersistPriority}");

        InitializeComponent();
        ApplySettings(settings);
        PriorityPersistence.ClearInteractiveRules();
        SetupTrayIcon();

        ProcessGrid.ItemsSource = _processes;
        StartupGrid.ItemsSource = _startupItems;
        ServiceGrid.ItemsSource = _services;
        foreach (var target in CleanupService.Targets()) _cleanupItems.Add(new CleanupItem(target));
        CleanupList.ItemsSource = _cleanupItems;

        LanguageCombo.ItemsSource = Loc.Languages;
        LanguageCombo.SelectedItem = Loc.Languages.First(l => l.Code == Loc.Instance.Language);
        LanguageCombo.SelectionChanged += LanguageCombo_SelectionChanged;

        CpuGraphCanvas.SizeChanged += (_, _) => DrawSparkline(CpuGraphCanvas, _cpuHistory);
        MemoryGraphCanvas.SizeChanged += (_, _) => DrawSparkline(MemoryGraphCanvas, _memHistory);

        // A previous run that crashed or was killed may have left processes frozen; only we can thaw them.
        var leftover = SuspendedStore.ResumeLeftovers();
        if (leftover > 0) Log(Loc.Instance.Format("Msg_ResumedLeftover", leftover));

        RefreshProcesses();
        RefreshStartup();
        RefreshServices();
        RefreshSecurity();
        BuildTweaks();
        UpdateSystemStats();

        Loc.Instance.PropertyChanged += (_, _) => { RefreshSecurity(); BuildTweaks(); };

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(RefreshSeconds) };
        _refreshTimer.Tick += async (_, _) => await TickAsync();
        _refreshTimer.Start();

        // Idle while hidden in the tray with nothing to supervise; wake instantly when shown again.
        IsVisibleChanged += (_, _) => ApplyRefreshPolicy();
        AutoOptimizeCheck.Checked += (_, _) => ApplyRefreshPolicy();
        AutoOptimizeCheck.Unchecked += (_, _) => ApplyRefreshPolicy();
        ApplyRefreshPolicy();
    }

    private void ApplyRefreshPolicy()
    {
        if (_closed) return;
        var run = RefreshPolicy.ShouldRun(IsVisible, AutoOptimizeCheck.IsChecked == true);
        if (run == _refreshTimer.IsEnabled) return;

        if (run)
        {
            _refreshTimer.Start();
            ActionLog.Write("Refresh resumed (window shown or Auto-Optimize turned on).");
            _ = TickAsync(); // show fresh data immediately instead of after the first interval
        }
        else
        {
            // Auto-Optimize releases what it changed from the tick; with no more ticks coming, do it now.
            if (_autoThrottled.Count > 0 || _autoSuspended.Count > 0) RestoreAutoThrottled();
            _belowThreshold = false;
            _refreshTimer.Stop();
            ActionLog.Write("Idle: window hidden and Auto-Optimize off, refresh paused.");
        }
    }

    private bool _closed;

    private async Task TickAsync()
    {
        // The guard stays set across the await, so a slow scan can never overlap the next tick; it also
        // covers AutoOptimizeTick opening a modal dialog (the timer keeps ticking in that nested loop).
        if (_tickRunning || _closed) return;
        _tickRunning = true;
        try
        {
            // Enumerating every process and the battery WMI query are the slow parts; they run off the
            // UI thread so the window never stutters during a refresh. Only the results are applied here.
            var (fresh, battery) = await Task.Run(() => (_processMonitor.Refresh(), _battery.GetHealth()));
            if (_closed) return;

            ApplyProcesses(fresh);
            UpdateSystemStats();
            ApplyBattery(battery);
            AutoOptimizeTick();
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { _tickRunning = false; }
    }

    // ---------- Saved settings ----------

    private void ApplySettings(AppSettings s)
    {
        _loadingSettings = true;
        try
        {
            AutoOptimizeCheck.IsChecked = s.AutoOptimize;
            AutoOptimizeThresholdBox.Text = s.FreeRamThresholdGb;
            AllowSuspendCheck.IsChecked = s.AllowSuspend;
            AutoRestorePointCheck.IsChecked = s.AutoRestorePoint;
            PersistPriorityCheck.IsChecked = s.PersistPriority;
        }
        finally { _loadingSettings = false; }

        // Saved on every change rather than on exit, so a crash or kill can't lose a choice. Unchecking
        // AllowSuspend in code (declined consent dialog) raises Unchecked too, so that is saved as well.
        foreach (var box in new[] { AutoOptimizeCheck, AllowSuspendCheck, AutoRestorePointCheck, PersistPriorityCheck })
        {
            box.Checked += (_, _) => SaveSettings();
            box.Unchecked += (_, _) => SaveSettings();
        }
        AutoOptimizeThresholdBox.TextChanged += (_, _) => SaveSettings();
    }

    private void SaveSettings()
    {
        if (_loadingSettings) return;
        new AppSettings
        {
            Language = Loc.Instance.Language,
            AutoOptimize = AutoOptimizeCheck.IsChecked == true,
            FreeRamThresholdGb = AutoOptimizeThresholdBox.Text,
            AllowSuspend = AllowSuspendCheck.IsChecked == true,
            AutoRestorePoint = AutoRestorePointCheck.IsChecked == true,
            PersistPriority = PersistPriorityCheck.IsChecked == true,
        }.Save();
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
        {
            Loc.Instance.Language = option.Code;
            SaveSettings();
        }
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
            _closed = true;
            _refreshTimer.Stop();
            ActionLog.Write($"=== Exiting. Releasing {_autoThrottled.Count} throttled and {_autoSuspended.Count} suspended process(es).");
            RestoreAutoThrottled();
            _trayIcon.Dispose();
        };
    }

    internal void RestoreFromTray()
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
        ActionLog.Write((failed ? "FAILED " : "") + message);
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
    // Synchronous variant for startup and the button handlers; the timer uses TickAsync instead.
    private void RefreshProcesses() => ApplyProcesses(_processMonitor.Refresh());

    private void ApplyProcesses(IReadOnlyList<ProcessEntry> fresh)
    {
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
        TrimProcesses(_processes.Where(p => !p.IsProtected && !ProtectedItems.IsAudioApp(p.Name) && p.MemoryBytes > 20L * 1024 * 1024).ToList());

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
            _belowThreshold = false;
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
            if (!_belowThreshold)
            {
                _belowThreshold = true;
                ActionLog.Write($"Memory pressure: {FormatGb(freeBytes)} GB free is below the {thresholdGb:0.##} GB threshold; Auto-Optimize is acting.");
            }
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
                        PersistSuspended(); // written right after each freeze, so a crash can't strand it
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
            if (_belowThreshold)
            {
                _belowThreshold = false;
                ActionLog.Write($"Memory pressure over: {FormatGb(freeBytes)} GB free; releasing anything Auto-Optimize changed.");
            }
            RestoreAutoThrottled();
        }
    }

    // Edge-detects the free-RAM threshold so the action log records when pressure began and ended
    // without one line per tick.
    private bool _belowThreshold;

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
        PersistSuspended();
        SuspendedCountText.Text = "";
    }

    private void PersistSuspended() =>
        SuspendedStore.Save(_autoSuspended.Select(kv => (kv.Key, kv.Value)));

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
        if (resumed.Count > 0) PersistSuspended();
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

    // Restore points (a WMI call that can take many seconds, or stall while Volume Shadow Copy is busy),
    // schtasks.exe and service WMI/SCM calls all block. They used to run on the UI thread, which is what
    // made the window "not respond" while a startup item was being disabled. They now run on a worker
    // while the UI shows a wait cursor; a second action is ignored until the first has finished.
    private bool _actionBusy;

    private bool BeginBusy()
    {
        if (_actionBusy) return false;
        _actionBusy = true;
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        return true;
    }

    private void EndBusy()
    {
        _actionBusy = false;
        System.Windows.Input.Mouse.OverrideCursor = null;
    }

    private static readonly TimeSpan RestorePointTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Creates a restore point off the UI thread; a stuck WMI call is abandoned after 90 s, not waited on forever.</summary>
    private static async Task<ActionResult> CreateRestorePointAsync(string description)
    {
        var work = Task.Run(() => SystemRestoreService.CreateRestorePoint(description));
        return await Task.WhenAny(work, Task.Delay(RestorePointTimeout)) == work
            ? await work
            : new ActionResult(false, "Msg_RestorePointFailed", "timed out");
    }

    private async void EnableStartup_Click(object sender, RoutedEventArgs e)
    {
        var selected = StartupGrid.SelectedItems.Cast<StartupEntry>().ToList();
        if (selected.Count == 0) { Log(Loc.Instance["Msg_NothingSelected"]); return; }
        await SetStartupEnabledAsync(selected, enable: true, restorePointDescription: null);
    }

    private async void DisableStartup_Click(object sender, RoutedEventArgs e)
    {
        var selected = StartupGrid.SelectedItems.Cast<StartupEntry>().ToList();
        if (selected.Count == 0) { Log(Loc.Instance["Msg_NothingSelected"]); return; }

        var items = selected.Select(s => s.Name).ToList();
        if (!ConfirmDialog.Show(this, Loc.Instance["Dlg_ConfirmTitle"], Loc.Instance["Dlg_ConfirmDisableStartupMsg"], items,
                danger: true, Loc.Instance["Dlg_Confirm"], Loc.Instance["Dlg_Cancel"]))
            return;

        await SetStartupEnabledAsync(selected, enable: false,
            AutoRestorePointCheck.IsChecked == true ? "BgOptimizer: before disabling startup items" : null);
    }

    private async Task SetStartupEnabledAsync(List<StartupEntry> selected, bool enable, string? restorePointDescription)
    {
        if (!BeginBusy()) return;
        try
        {
            if (restorePointDescription != null)
                Log(await CreateRestorePointAsync(restorePointDescription));

            var results = await Task.Run(() => selected.Select(entry =>
            {
                var wasEnabled = entry.Enabled;
                return (Entry: entry, WasEnabled: wasEnabled, Result: _startupManager.SetEnabled(entry, enable));
            }).ToList());

            foreach (var (entry, wasEnabled, result) in results)
            {
                Log(result);
                if (result.Ok) _undoStack.Push(() => _startupManager.SetEnabled(entry, wasEnabled));
            }
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { EndBusy(); }

        RefreshStartup();
    }

    private async void CreateRestorePoint_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginBusy()) return;
        try { Log(await CreateRestorePointAsync("BgOptimizer: manual restore point")); }
        finally { EndBusy(); }
    }

    // ---------- Cleanup ----------

    private readonly ObservableCollection<CleanupItem> _cleanupItems = new();

    // Walking cache folders can take a while, so both scanning and deleting run on a worker; the rows update as each finishes.
    private async void CleanupScan_Click(object sender, RoutedEventArgs e)
    {
        if (!BeginBusy()) return;
        try
        {
            long bytes = 0; var files = 0;
            foreach (var item in _cleanupItems.ToList())
            {
                var scan = await Task.Run(() => CleanupService.Scan(item.Target));
                item.SetScan(scan);
                if (item.Selected) { bytes += scan.Bytes; files += scan.Files; }
            }
            Log(Loc.Instance.Format("Msg_CleanupScanned", CleanupService.FormatBytes(bytes), files));
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { EndBusy(); }
    }

    private async void CleanupRun_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _cleanupItems.Where(i => i.Selected).ToList();
        if (chosen.Count == 0) { Log(Loc.Instance["Msg_NothingSelected"]); return; }

        if (!ConfirmDialog.Show(this, Loc.Instance["Dlg_ConfirmTitle"], Loc.Instance["Dlg_ConfirmCleanupMsg"],
                chosen.Select(i => i.Name).ToList(), danger: true, Loc.Instance["Dlg_Confirm"], Loc.Instance["Dlg_Cancel"]))
            return;

        if (!BeginBusy()) return;
        try
        {
            long total = 0;
            foreach (var item in chosen)
            {
                var run = await Task.Run(() => CleanupService.Clean(item.Target));
                total += run.BytesFreed;
                Log(Loc.Instance.Format("Msg_CleanupDone", CleanupService.FormatBytes(run.BytesFreed), item.Name, run.Deleted, run.Skipped));
                item.SetScan(await Task.Run(() => CleanupService.Scan(item.Target))); // what is left (in-use files stay)
            }
            Log(Loc.Instance.Format("Msg_CleanupTotal", CleanupService.FormatBytes(total)));
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { EndBusy(); }
    }

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
    private void RefreshBattery() => ApplyBattery(_battery.GetHealth());

    private void ApplyBattery(BatteryManager.BatteryHealth h)
    {
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

    private async Task SetServiceMode(string mode, bool needsConfirm)
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
        }

        if (!BeginBusy()) return;
        try
        {
            if (needsConfirm && AutoRestorePointCheck.IsChecked == true)
                Log(await CreateRestorePointAsync($"BgOptimizer: services set to {mode}"));

            var results = await Task.Run(() => targets.Select(entry =>
            {
                var previousStartType = entry.StartType;
                return (Entry: entry, Previous: previousStartType, Result: _svcManager.SetStartMode(entry, mode));
            }).ToList());

            foreach (var (entry, previousStartType, result) in results)
            {
                Log(result);
                if (result.Ok) _undoStack.Push(() => _svcManager.SetStartMode(entry, previousStartType));
            }
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { EndBusy(); }

        RefreshServices();
    }

    private async void ServiceAuto_Click(object sender, RoutedEventArgs e) => await SetServiceMode("Auto", needsConfirm: false);
    private async void ServiceManual_Click(object sender, RoutedEventArgs e) => await SetServiceMode("Manual", needsConfirm: false);
    private async void ServiceDisabled_Click(object sender, RoutedEventArgs e) => await SetServiceMode("Disabled", needsConfirm: true);

    private async void ServiceStop_Click(object sender, RoutedEventArgs e)
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

        if (!BeginBusy()) return;
        try
        {
            if (AutoRestorePointCheck.IsChecked == true)
                Log(await CreateRestorePointAsync("BgOptimizer: before stopping services"));

            // Each Stop waits up to 10 s for the service, so a batch can take a while: keep it off the UI thread.
            var results = await Task.Run(() => targets.Select(entry => (Entry: entry, Result: _svcManager.Stop(entry))).ToList());
            foreach (var (entry, result) in results)
            {
                Log(result);
                if (result.Ok) _undoStack.Push(() => _svcManager.Start(entry));
            }
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { EndBusy(); }

        RefreshServices();
    }
}
