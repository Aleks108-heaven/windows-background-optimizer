using System.Management;
using BgOptimizer.Models;

namespace BgOptimizer.Services;

/// <summary>
/// Lists and reconfigures Windows services via WMI (Win32_Service), which
/// exposes ChangeStartMode directly rather than requiring a shell-out to sc.exe.
/// </summary>
public class SvcManager
{
    public List<ServiceEntry> GetAll()
    {
        var list = new List<ServiceEntry>();
        using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, State, StartMode FROM Win32_Service");
        foreach (ManagementObject obj in searcher.Get())
        {
            var name = obj["Name"]?.ToString() ?? "";
            list.Add(new ServiceEntry
            {
                Name = name,
                DisplayName = obj["DisplayName"]?.ToString() ?? name,
                Status = obj["State"]?.ToString() ?? "Unknown",
                StartType = obj["StartMode"]?.ToString() ?? "Unknown",
                IsProtected = ProtectedItems.IsServiceProtected(name),
            });
        }
        return list.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <param name="mode">"Auto", "Manual", or "Disabled"</param>
    public (bool ok, string message) SetStartMode(ServiceEntry entry, string mode)
    {
        if (entry.IsProtected)
            return (false, $"'{entry.DisplayName}' is a protected system service and cannot be modified.");

        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT * FROM Win32_Service WHERE Name = '{Escape(entry.Name)}'");
            using var results = searcher.Get();
            var service = results.Cast<ManagementObject>().FirstOrDefault();
            if (service == null)
                return (false, $"Service '{entry.Name}' not found.");

            var result = service.InvokeMethod("ChangeStartMode", new object[] { mode });
            var returnCode = Convert.ToUInt32(result);
            if (returnCode == 0)
            {
                entry.StartType = mode;
                return (true, $"Set '{entry.DisplayName}' startup type to {mode}.");
            }
            return (false, $"Failed to change '{entry.DisplayName}' (WMI error code {returnCode}).");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to change '{entry.DisplayName}': {ex.Message}");
        }
    }

    public (bool ok, string message) Stop(ServiceEntry entry)
    {
        if (entry.IsProtected)
            return (false, $"'{entry.DisplayName}' is a protected system service and cannot be stopped.");

        try
        {
            using var controller = new System.ServiceProcess.ServiceController(entry.Name);
            if (controller.Status == System.ServiceProcess.ServiceControllerStatus.Running)
            {
                controller.Stop();
                controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
            entry.Status = controller.Status.ToString();
            return (true, $"Stopped '{entry.DisplayName}'.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to stop '{entry.DisplayName}': {ex.Message}");
        }
    }

    private static string Escape(string s) => s.Replace("'", "''");
}
