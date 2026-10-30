using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using BgOptimizer.Localization;
using BgOptimizer.Models;
using BgOptimizer.Services;

namespace BgOptimizer;

// Checks tab: three read-only scans. Each runs on a worker thread and can be stopped; only one runs at a
// time. Results are kept as data and turned into grid rows, so a language switch can rebuild the rows.
public partial class MainWindow
{
    private readonly ObservableCollection<LeftoverRow> _leftoverRows = new();
    private readonly ObservableCollection<DuplicateRow> _duplicateRows = new();
    private readonly ObservableCollection<DriverRow> _driverRows = new();

    private IReadOnlyList<LeftoverFinding> _leftoverFindings = Array.Empty<LeftoverFinding>();
    private IReadOnlyList<DuplicateGroup> _duplicateGroups = Array.Empty<DuplicateGroup>();
    private IReadOnlyList<DriverCopy> _driverCopies = Array.Empty<DriverCopy>();
    private string _leftoverSummaryKey = "", _duplicateSummaryKey = "", _driverSummaryKey = "";

    private CancellationTokenSource? _checkCts;
    private bool _checkRunning;

    private void InitChecks()
    {
        LeftoverGrid.ItemsSource = _leftoverRows;
        DuplicateGrid.ItemsSource = _duplicateRows;
        DriverGrid.ItemsSource = _driverRows;
        Loc.Instance.PropertyChanged += (_, _) => RebuildCheckRows();
    }

    private void RebuildCheckRows()
    {
        Fill(_leftoverRows, CheckRows.ForLeftovers(_leftoverFindings));
        Fill(_duplicateRows, CheckRows.ForDuplicates(_duplicateGroups));
        Fill(_driverRows, CheckRows.ForDrivers(_driverCopies));
        if (_leftoverSummaryKey.Length > 0) LeftoverStatusText.Text = SummaryFor(_leftoverSummaryKey);
        if (_duplicateSummaryKey.Length > 0) DuplicateStatusText.Text = SummaryFor(_duplicateSummaryKey);
        if (_driverSummaryKey.Length > 0) DriverStatusText.Text = SummaryFor(_driverSummaryKey);
    }

    private string SummaryFor(string which) => which switch
    {
        "left" => _leftoverFindings.Count == 0 ? Loc.Instance["Ck_NothingFound"] : Loc.Instance.Format("Ck_LeftDone", _leftoverFindings.Count),
        "dup" => CheckRows.DuplicateSummary(_duplicateGroups.ToList()),
        _ => CheckRows.DriverSummary(_driverCopies.ToList()),
    };

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }

    private void SetChecksEnabled(bool idle)
    {
        LeftoverScanButton.IsEnabled = idle;
        DupScanButton.IsEnabled = idle;
        DupScanFolderButton.IsEnabled = idle;
        DriverScanButton.IsEnabled = idle;
        DriverSelectOldButton.IsEnabled = idle;
        DriverDeleteButton.IsEnabled = idle;
        CheckStopButton.IsEnabled = !idle;
    }

    /// <summary>Runs one scan off the UI thread. <paramref name="work"/> returns the summary key to show; the scan itself stores its results.</summary>
    private async Task RunCheckAsync(System.Windows.Controls.TextBlock status, Func<CancellationToken, Task<string>> work)
    {
        if (_checkRunning || _closed) return;
        _checkRunning = true;
        SetChecksEnabled(false);
        _checkCts = new CancellationTokenSource();
        status.Text = Loc.Instance["Ck_Scanning"];
        try
        {
            var key = await work(_checkCts.Token);
            status.Text = SummaryFor(key);
            Log(status.Text);
        }
        catch (OperationCanceledException)
        {
            status.Text = Loc.Instance["Ck_Cancelled"];
            Log(status.Text);
        }
        catch (Exception ex)
        {
            status.Text = Loc.Instance.Format("Ck_Failed", ex.Message);
            Log(status.Text);
        }
        finally
        {
            _checkCts?.Dispose();
            _checkCts = null;
            _checkRunning = false;
            if (!_closed) SetChecksEnabled(true);
        }
    }

    private void CheckStop_Click(object sender, RoutedEventArgs e) => _checkCts?.Cancel();

    private async void LeftoverScan_Click(object sender, RoutedEventArgs e)
    {
        _leftoverSummaryKey = "";
        await RunCheckAsync(LeftoverStatusText, async ct =>
        {
            _leftoverFindings = await Task.Run(() => LeftoverScanner.Scan(ct), ct);
            Fill(_leftoverRows, CheckRows.ForLeftovers(_leftoverFindings));
            _leftoverSummaryKey = "left";
            return "left";
        });
    }

    private async void DuplicateScan_Click(object sender, RoutedEventArgs e) =>
        await ScanDuplicatesAsync(DuplicateFinder.DefaultRoots().ToList());

    private async void DuplicateScanFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = Loc.Instance["Ck_DupFolderPrompt"],
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        await ScanDuplicatesAsync(new List<string> { dialog.SelectedPath });
    }

    private async Task ScanDuplicatesAsync(List<string> roots)
    {
        _duplicateSummaryKey = "";
        // Created here, on the UI thread, so each report is delivered back on it.
        var progress = new Progress<DuplicateProgress>(p => DuplicateStatusText.Text = Loc.Instance.Format(
            p.Comparing ? "Ck_DupComparing" : "Ck_DupProgress", p.Count));
        await RunCheckAsync(DuplicateStatusText, async ct =>
        {
            _duplicateGroups = await Task.Run(() => DuplicateFinder.Find(roots, DuplicateFinder.DefaultMinBytes, ct, progress), ct);
            Fill(_duplicateRows, CheckRows.ForDuplicates(_duplicateGroups));
            _duplicateSummaryKey = "dup";
            return "dup";
        });
    }

    private async void DriverScan_Click(object sender, RoutedEventArgs e)
    {
        _driverSummaryKey = "";
        await RunCheckAsync(DriverStatusText, async ct =>
        {
            _driverCopies = await Task.Run(() => DriverStoreScanner.Scan(ct), ct);
            Fill(_driverRows, CheckRows.ForDrivers(_driverCopies));
            _driverSummaryKey = "drv";
            return "drv";
        });
    }

    private void LeftoverOpen_Click(object sender, RoutedEventArgs e) =>
        OpenInExplorer((LeftoverGrid.SelectedItem as LeftoverRow)?.Path);

    private void DuplicateOpen_Click(object sender, RoutedEventArgs e) =>
        OpenInExplorer((DuplicateGrid.SelectedItem as DuplicateRow)?.Path);

    /// <summary>Shows the item in Explorer, or its nearest existing parent folder when it is already gone.</summary>
    private void OpenInExplorer(string? path, bool openFolderItself = false)
    {
        if (string.IsNullOrWhiteSpace(path)) { Log(Loc.Instance["Msg_NothingSelected"]); return; }
        // Some of these paths come from the registry, which other programs can write: handing Explorer a
        // network path would make it (and so this elevated app's request) contact whatever server it names.
        if (!PathProbe.IsOnLocalFixedDrive(path)) { Log(Loc.Instance["Ck_PathNotLocal"]); return; }
        try
        {
            bool opened;
            if (openFolderItself && Directory.Exists(path))
                opened = ExplorerLauncher.Open(path, select: false);
            else if (File.Exists(path) || Directory.Exists(path))
                opened = ExplorerLauncher.Open(path, select: true);
            else
            {
                var parent = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) { Log(Loc.Instance["Ck_PathGone"]); return; }
                opened = ExplorerLauncher.Open(parent, select: false);
            }
            if (!opened) Log(Loc.Instance["Ck_PathGone"]);
        }
        catch (Exception ex) { Log(ex.Message); }
    }

    // ---------- Duplicate drivers: open a copy's folder, remove old unused copies ----------

    private DriverCopy? CopyFor(DriverRow row) =>
        _driverCopies.FirstOrDefault(c => string.Equals(c.Package.FolderName, row.FolderName, StringComparison.OrdinalIgnoreCase));

    private void DriverOpen_Click(object sender, RoutedEventArgs e)
    {
        if (DriverGrid.SelectedItem is not DriverRow { FolderName.Length: > 0 } row) { Log(Loc.Instance["Msg_NothingSelected"]); return; }
        OpenInExplorer(Path.Combine(DriverStoreScanner.DriverStorePath, row.FolderName), openFolderItself: true);
    }

    private void DriverSelectOld_Click(object sender, RoutedEventArgs e)
    {
        DriverGrid.UnselectAll();
        var picked = 0;
        foreach (var row in _driverRows)
            if (CopyFor(row) is { } copy && DriverStoreCleaner.CanDelete(copy)) { DriverGrid.SelectedItems.Add(row); picked++; }
        if (picked == 0) Log(Loc.Instance[DriverStoreCleaner.NothingToRemoveKey(_driverCopies)]);
        else if (DriverStoreCleaner.CountBuiltIn(_driverCopies) is > 0 and var kept) Log(Loc.Instance.Format("Ck_DrvBuiltInKept", kept));
    }

    private async void DriverDelete_Click(object sender, RoutedEventArgs e)
    {
        if (ViewOnlyBlocks()) return;
        var picked = DriverGrid.SelectedItems.OfType<DriverRow>().Select(CopyFor).OfType<DriverCopy>().ToList();
        var chosen = picked.Where(DriverStoreCleaner.CanDelete).ToList();
        if (chosen.Count == 0) { Log(Loc.Instance[DriverStoreCleaner.NothingToRemoveKey(picked)]); return; }
        if (DriverStoreCleaner.CountBuiltIn(picked) is > 0 and var kept) Log(Loc.Instance.Format("Ck_DrvBuiltInKept", kept));

        var items = chosen.Select(c =>
            $"{c.Package.InfName}  {c.Package.Version}  ({c.Package.PublishedName}, {CleanupService.FormatBytes(c.Package.Bytes)})").ToList();
        if (!ConfirmDialog.Show(this, Loc.Instance["Dlg_ConfirmTitle"], Loc.Instance["Ck_DrvDeleteMsg"], items,
                danger: true, Loc.Instance["Dlg_Confirm"], Loc.Instance["Dlg_Cancel"]))
            return;

        await RunCheckAsync(DriverStatusText, async ct =>
        {
            if (AutoRestorePointCheck.IsChecked == true)
                Log(await CreateRestorePointAsync("BgOptimizer: before removing old drivers"));

            long freed = 0;
            var removed = 0;
            foreach (var copy in chosen)
            {
                if (ct.IsCancellationRequested) break; // between packages only: a pnputil call is never cut short
                var result = await Task.Run(() => DriverStoreCleaner.Delete(copy));
                Log(result);
                if (!result.Ok) continue;
                removed++;
                freed += copy.Package.Bytes;
            }
            Log(Loc.Instance.Format("Ck_DrvRemovedTotal", removed, CleanupService.FormatBytes(freed)));

            // What is left, relabelled (a group that is down to one copy no longer counts as a duplicate).
            _driverCopies = await Task.Run(() => DriverStoreScanner.Scan(CancellationToken.None));
            Fill(_driverRows, CheckRows.ForDrivers(_driverCopies));
            _driverSummaryKey = "drv";
            return "drv";
        });
    }
}
