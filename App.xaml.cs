using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using BgOptimizer.Services;

namespace BgOptimizer;

public partial class App : System.Windows.Application
{
    private EventWaitHandle? _activateSignal;
    private RegisteredWaitHandle? _activateWait;

    // Two copies would each run Auto-Optimize (fighting over priorities and the frozen-process list)
    // and show two tray icons, so only the first instance runs. Counted by exe path rather than a
    // named mutex; two copies launched in the same instant can both pass, which is harmless here.
    // The copy that is already running is usually hidden in the tray, so a second launch (taskbar icon,
    // Start menu) asks it to show its window instead of just exiting and looking like the app is broken.
    protected override void OnStartup(StartupEventArgs e)
    {
        if (IsAnotherInstanceRunning(out var runningPid))
        {
            AskRunningInstanceToShow(runningPid);
            Shutdown();
            return;
        }
        // Observe only (Handled stays false), so crash behaviour is unchanged but the cause is on record.
        DispatcherUnhandledException += (_, args) =>
            Services.ActionLog.Write("UNHANDLED UI exception: " + args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Services.ActionLog.Write("UNHANDLED exception (terminating=" + args.IsTerminating + "): " + args.ExceptionObject);
        base.OnStartup(e);
        ListenForActivation();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _activateSignal?.Dispose();
        base.OnExit(e);
    }

    private static string ActivationEventName() => SingleInstance.ActivationEventName(Environment.ProcessPath ?? "");

    private void ListenForActivation()
    {
        try
        {
            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName());
            _activateWait = ThreadPool.RegisterWaitForSingleObject(_activateSignal, (_, _) =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(() => (Current.MainWindow as BgOptimizer.MainWindow)?.RestoreFromTray()));
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            // Without the signal a second launch just exits, as before; the app itself is unaffected.
            Services.ActionLog.Write("Could not listen for a second launch: " + ex.Message);
        }
    }

    private static void AskRunningInstanceToShow(int runningPid)
    {
        try
        {
            // This process was just started by the user's click, so it may pass the foreground right on;
            // without it Windows would only flash the other copy's taskbar button.
            AllowSetForegroundWindow(runningPid);
            using var signal = EventWaitHandle.OpenExisting(ActivationEventName());
            signal.Set();
        }
        catch { /* the running copy is still starting, or is an older build without the signal */ }
    }

    [DllImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static bool IsAnotherInstanceRunning(out int otherPid)
    {
        otherPid = 0;
        try
        {
            using var self = Process.GetCurrentProcess();
            var selfPath = self.MainModule?.FileName;
            foreach (var other in Process.GetProcessesByName(self.ProcessName))
            {
                using (other)
                {
                    if (other.Id == self.Id) continue;
                    try
                    {
                        if (string.Equals(other.MainModule?.FileName, selfPath, StringComparison.OrdinalIgnoreCase))
                        {
                            otherPid = other.Id;
                            return true;
                        }
                    }
                    catch { /* not readable: not ours to count */ }
                }
            }
        }
        catch { /* if we can't tell, start normally rather than refuse to run */ }
        return false;
    }
}
