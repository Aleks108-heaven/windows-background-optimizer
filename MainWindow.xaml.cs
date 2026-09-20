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

    private readonly DispatcherTimer _refreshTimer;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    // 30 samples at the 2s refresh interval mirrors Task Manager's 60-second window.
    private const int GraphHistoryLength = 30;
    private readonly List<double> _cpuHistory = new();
    private readonly List<double> _memHistory = new();

    public MainWindow()
    {
        InitializeComponent();
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
        UpdateSystemStats();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshProcesses();
            UpdateSystemStats();
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

    private void ApplyPriority_Click(object sender, RoutedEventArgs e)
    {
        var selected = ProcessGrid.SelectedItems.Cast<ProcessEntry>().ToList();
        if (selected.Count == 0) return;

        var priorityName = (PriorityCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Normal";
        if (!Enum.TryParse<ProcessPriorityClass>(priorityName, out var priority)) return;

        foreach (var entry in selected)
        {
            if (entry.IsProtected) Log(Loc.Instance.Format("Msg_BlockedProcess", entry.Name));
            else Log(ProcessMonitor.SetPriority(entry.Pid, entry.Name, priority));
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
            Log(_startupManager.SetEnabled(entry, true));

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

        foreach (var entry in selected)
            Log(_startupManager.SetEnabled(entry, false));

        RefreshStartup();
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
        }

        foreach (var entry in targets)
            Log(_svcManager.SetStartMode(entry, mode));

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

        foreach (var entry in targets)
            Log(_svcManager.Stop(entry));

        RefreshServices();
    }
}
