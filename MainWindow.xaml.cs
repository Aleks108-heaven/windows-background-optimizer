using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using BgOptimizer.Models;
using BgOptimizer.Services;

namespace BgOptimizer;

public partial class MainWindow : Window
{
    private readonly ProcessMonitor _processMonitor = new();
    private readonly StartupManager _startupManager = new();
    private readonly SvcManager _svcManager = new();

    private readonly ObservableCollection<ProcessEntry> _processes = new();
    private readonly ObservableCollection<StartupEntry> _startupItems = new();
    private readonly ObservableCollection<ServiceEntry> _services = new();

    private readonly DispatcherTimer _refreshTimer;

    public MainWindow()
    {
        InitializeComponent();

        ProcessGrid.ItemsSource = _processes;
        StartupGrid.ItemsSource = _startupItems;
        ServiceGrid.ItemsSource = _services;

        RefreshProcesses();
        RefreshStartup();
        RefreshServices();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshProcesses();
        _refreshTimer.Start();
    }

    private void Log(string message) =>
        LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");

    private bool Confirm(string message) =>
        MessageBox.Show(message, "Confirm action", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    // ---------- Processes ----------

    private void RefreshProcesses()
    {
        var selectedPid = (ProcessGrid.SelectedItem as ProcessEntry)?.Pid;
        var fresh = _processMonitor.Refresh();

        _processes.Clear();
        foreach (var p in fresh) _processes.Add(p);

        if (selectedPid.HasValue)
            ProcessGrid.SelectedItem = _processes.FirstOrDefault(p => p.Pid == selectedPid.Value);
    }

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e) => RefreshProcesses();

    private void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessEntry entry) return;
        if (entry.IsProtected)
        {
            Log($"Blocked: '{entry.Name}' is a protected system process.");
            return;
        }
        if (!Confirm($"Kill process '{entry.Name}' (PID {entry.Pid})?")) return;

        var (ok, message) = ProcessMonitor.Kill(entry.Pid, entry.Name);
        Log(message);
        if (ok) RefreshProcesses();
    }

    private void ApplyPriority_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessEntry entry) return;
        if (entry.IsProtected)
        {
            Log($"Blocked: '{entry.Name}' is a protected system process.");
            return;
        }
        var priorityName = (PriorityCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Normal";
        if (!Enum.TryParse<ProcessPriorityClass>(priorityName, out var priority)) return;

        var (ok, message) = ProcessMonitor.SetPriority(entry.Pid, entry.Name, priority);
        Log(message);
        if (ok) RefreshProcesses();
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
        if (StartupGrid.SelectedItem is not StartupEntry entry) return;
        var (ok, message) = _startupManager.SetEnabled(entry, true);
        Log(message);
        if (ok) RefreshStartup();
    }

    private void DisableStartup_Click(object sender, RoutedEventArgs e)
    {
        if (StartupGrid.SelectedItem is not StartupEntry entry) return;
        if (!Confirm($"Disable '{entry.Name}' from starting up automatically?")) return;

        var (ok, message) = _startupManager.SetEnabled(entry, false);
        Log(message);
        if (ok) RefreshStartup();
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
        if (ServiceGrid.SelectedItem is not ServiceEntry entry) return;
        if (entry.IsProtected)
        {
            Log($"Blocked: '{entry.DisplayName}' is a protected system service.");
            return;
        }
        if (needsConfirm && !Confirm($"Set '{entry.DisplayName}' startup type to {mode}?")) return;

        var (ok, message) = _svcManager.SetStartMode(entry, mode);
        Log(message);
        if (ok) RefreshServices();
    }

    private void ServiceAuto_Click(object sender, RoutedEventArgs e) => SetServiceMode("Auto", needsConfirm: false);
    private void ServiceManual_Click(object sender, RoutedEventArgs e) => SetServiceMode("Manual", needsConfirm: false);
    private void ServiceDisabled_Click(object sender, RoutedEventArgs e) => SetServiceMode("Disabled", needsConfirm: true);

    private void ServiceStop_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceGrid.SelectedItem is not ServiceEntry entry) return;
        if (entry.IsProtected)
        {
            Log($"Blocked: '{entry.DisplayName}' is a protected system service.");
            return;
        }
        if (!Confirm($"Stop service '{entry.DisplayName}' right now?")) return;

        var (ok, message) = _svcManager.Stop(entry);
        Log(message);
        if (ok) RefreshServices();
    }
}
