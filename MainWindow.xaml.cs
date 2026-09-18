using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using BgOptimizer.Localization;
using BgOptimizer.Models;
using BgOptimizer.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

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
    private System.Windows.Forms.NotifyIcon? _trayIcon;

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

        RefreshProcesses();
        RefreshStartup();
        RefreshServices();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += (_, _) => RefreshProcesses();
        _refreshTimer.Start();
    }

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
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.Transparent);
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(14, 99, 156));
        g.FillEllipse(brush, 1, 1, 30, 30);
        using var font = new System.Drawing.Font("Segoe UI", 14, System.Drawing.FontStyle.Bold);
        var textSize = g.MeasureString("B", font);
        g.DrawString("B", font, System.Drawing.Brushes.White, (32 - textSize.Width) / 2, (32 - textSize.Height) / 2 - 1);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    private void Log(string message) =>
        LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");

    private void Log(ActionResult result) => Log(result.Message);

    private bool Confirm(string message) =>
        MessageBox.Show(message, Loc.Instance["Dlg_ConfirmTitle"], MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

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
            Log(Loc.Instance.Format("Msg_BlockedProcess", entry.Name));
            return;
        }
        if (!Confirm(Loc.Instance.Format("Dlg_ConfirmKill", entry.Name, entry.Pid))) return;

        var result = ProcessMonitor.Kill(entry.Pid, entry.Name);
        Log(result);
        if (result.Ok) RefreshProcesses();
    }

    private void ApplyPriority_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessGrid.SelectedItem is not ProcessEntry entry) return;
        if (entry.IsProtected)
        {
            Log(Loc.Instance.Format("Msg_BlockedProcess", entry.Name));
            return;
        }
        var priorityName = (PriorityCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? "Normal";
        if (!Enum.TryParse<ProcessPriorityClass>(priorityName, out var priority)) return;

        var result = ProcessMonitor.SetPriority(entry.Pid, entry.Name, priority);
        Log(result);
        if (result.Ok) RefreshProcesses();
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
        var result = _startupManager.SetEnabled(entry, true);
        Log(result);
        if (result.Ok) RefreshStartup();
    }

    private void DisableStartup_Click(object sender, RoutedEventArgs e)
    {
        if (StartupGrid.SelectedItem is not StartupEntry entry) return;
        if (!Confirm(Loc.Instance.Format("Dlg_ConfirmDisableStartup", entry.Name))) return;

        var result = _startupManager.SetEnabled(entry, false);
        Log(result);
        if (result.Ok) RefreshStartup();
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
            Log(Loc.Instance.Format("Msg_BlockedService", entry.DisplayName));
            return;
        }
        if (needsConfirm && !Confirm(Loc.Instance.Format("Dlg_ConfirmServiceMode", entry.DisplayName, mode))) return;

        var result = _svcManager.SetStartMode(entry, mode);
        Log(result);
        if (result.Ok) RefreshServices();
    }

    private void ServiceAuto_Click(object sender, RoutedEventArgs e) => SetServiceMode("Auto", needsConfirm: false);
    private void ServiceManual_Click(object sender, RoutedEventArgs e) => SetServiceMode("Manual", needsConfirm: false);
    private void ServiceDisabled_Click(object sender, RoutedEventArgs e) => SetServiceMode("Disabled", needsConfirm: true);

    private void ServiceStop_Click(object sender, RoutedEventArgs e)
    {
        if (ServiceGrid.SelectedItem is not ServiceEntry entry) return;
        if (entry.IsProtected)
        {
            Log(Loc.Instance.Format("Msg_BlockedService", entry.DisplayName));
            return;
        }
        if (!Confirm(Loc.Instance.Format("Dlg_ConfirmStopService", entry.DisplayName))) return;

        var result = _svcManager.Stop(entry);
        Log(result);
        if (result.Ok) RefreshServices();
    }
}
