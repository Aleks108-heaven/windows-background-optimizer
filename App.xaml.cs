using System.Diagnostics;
using System.Windows;

namespace BgOptimizer;

public partial class App : System.Windows.Application
{
    // Two copies would each run Auto-Optimize (fighting over priorities and the frozen-process list)
    // and show two tray icons, so only the first instance runs. Counted by exe path rather than a
    // named mutex; two copies launched in the same instant can both pass, which is harmless here.
    protected override void OnStartup(StartupEventArgs e)
    {
        if (IsAnotherInstanceRunning())
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    private static bool IsAnotherInstanceRunning()
    {
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
                        if (string.Equals(other.MainModule?.FileName, selfPath, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch { /* not readable: not ours to count */ }
                }
            }
        }
        catch { /* if we can't tell, start normally rather than refuse to run */ }
        return false;
    }
}
