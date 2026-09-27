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

    private readonly ObservableCollection<ProcessEntry> _processes = new();
    private readonly ObservableCollection<StartupEntry> _startupItems = new();
    private readonly ObservableCollection<ServiceEntry> _services = new();

    private readonly Dictionary<string, ProcessPriorityClass> _enforcedPriorities = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

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
            RefreshProcesses();
            UpdateSystemStats();
            RefreshBattery(); // cheap WMI reads only; the rest of RefreshSecurity() spawns netsh and stays on-demand
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

        canvas.Children.Add(new System.Windows.Shapes.Polygon
        {
            Points = fillPoints,
            Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 14, 99, 156)),
        });
        canvas.Children.Add(new System.Windows.Shapes.Polyline
        {
            Points = linePoints,
            Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 99, 156)),
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

        Closed += (_, _) => _trayIcon.Dispose();
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

    private void Log(string message) =>
        LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");

    private void Log(ActionResult result) => Log(result.Message);

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
                if (!p.IsProtected && _enforcedPriorities.TryGetValue(p.Name, out var wanted)
                    && p.Priority != wanted.ToString())
                    // allowInteractiveThrottle: true - persisting this rule already required the
                    // user to explicitly opt in via the checkbox at the time it was applied.
                    ProcessMonitor.SetPriority(p.Pid, p.Name, wanted, allowInteractiveThrottle: true);
            }
        }
        var freshPids = new HashSet<int>(fresh.Count);
        foreach (var p in fresh) freshPids.Add(p.Pid);

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
            else if (currentIndex != i) _processes.Move(currentIndex, i);
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
        if (selected.Count == 0) return;

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
            .Where(p => !p.IsProtected && !ProtectedItems.IsInteractive(p.Name)
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
                return new ActionResult(true, "Msg_PriorityChanged", "laptop saver", n, "restored");
            });
    }

    private void ApplyPriority_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProcessGrid.SelectedItems.Cast<ProcessEntry>().ToList();
        if (selected.Count == 0) return;

        var priorityName = (PriorityCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Normal";
        if (!Enum.TryParse<ProcessPriorityClass>(priorityName, out var priority)) return;

        var persist = PersistPriorityCheck.IsChecked == true;
        var allowInteractive = AllowInteractiveThrottleCheck.IsChecked == true;
        foreach (var entry in selected)
        {
            if (entry.IsProtected) { Log(Loc.Instance.Format("Msg_BlockedProcess", entry.Name)); continue; }

            Log(ProcessMonitor.SetPriority(entry.Pid, entry.Name, priority, allowInteractive));

            // Sync the persistent IFEO rule to the checkbox state: checked writes
            // it, unchecked clears whatever rule (if any) was there before, so
            // toggling the box off and re-applying is how you undo persistence.
            if (persist)
            {
                _enforcedPriorities[entry.Name] = priority;
                Log(PriorityPersistence.SetPersistent(entry.Name, priority));
            }
            else
            {
                _enforcedPriorities.Remove(entry.Name);
                if (PriorityPersistence.IsPersisted(entry.Name)) Log(PriorityPersistence.ClearPersistent(entry.Name));
            }
        }
        RefreshProcesses();
    }

    // ---------- Startup ----------

    private void RefreshStartup()
    {
        _startupItems.Clear();
        foreach (var s in _startupManager.GetAll()) _startupItems.Add(s);
    }

    private void RefreshStartup_Click(object sender, RoutedEventArgs e) => RefreshStartup();

    private void EnableStartup_Click(object sender, RoutedEventArgs e)
    {
        var selected = StartupGrid.SelectedItems.Cast<StartupEntry>().ToList();
        if (selected.Count == 0) return;

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
        if (selected.Count == 0) return;

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

    private void RefreshSecurity()
    {
        string Fw(bool? on) => Loc.Instance[on switch { true => "Sec_On", false => "Sec_Off", null => "Sec_Unknown" }];
        var (domain, priv, pub) = _security.GetFirewallProfiles();
        FirewallStatusText.Text = Loc.Instance.Format("Sec_FwLine", Fw(domain), Fw(priv), Fw(pub));

        var winRm = _security.GetWinRm();
        WinRmStatusText.Text = Loc.Instance.Format("Sec_WinRmLine", winRm.Status, winRm.StartMode);

        SmbStatusText.Text = Loc.Instance.Format("Sec_SmbLine",
            Loc.Instance[_security.IsSmbBlocked() ? "Sec_Blocked" : "Sec_NotBlocked"]);

        RefreshBlockedApps();
        RefreshBattery();
        RefreshHibernateStatus();
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

    // Spawns powercfg, so this stays on-demand (tab load, Refresh button, after toggling) rather than on the 2s timer.
    private void RefreshHibernateStatus()
    {
        var hib = _battery.IsHibernateOnLidClose();
        HibernateStatusText.Text = Loc.Instance.Format("Sec_HibernateLine",
            Loc.Instance[hib switch { true => "Sec_HibernateOn", false => "Sec_HibernateOff", null => "Sec_HibernateUnknown" }]);
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

    private void RefreshBlockedApps()
    {
        BlockedAppsCombo.ItemsSource = _security.GetBlockedApps();
        if (BlockedAppsCombo.Items.Count > 0) BlockedAppsCombo.SelectedIndex = 0;
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

    private void RefreshServices()
    {
        _services.Clear();
        foreach (var s in _svcManager.GetAll()) _services.Add(s);
    }

    private void RefreshServices_Click(object sender, RoutedEventArgs e) => RefreshServices();

    private void SetServiceMode(string mode, bool needsConfirm)
    {
        var selected = ServiceGrid.SelectedItems.Cast<ServiceEntry>().ToList();
        if (selected.Count == 0) return;

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
        if (selected.Count == 0) return;

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
