using System.Management;
using BgOptimizer.Localization;
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
                Category = ItemClassifier.ClassifyService(name, obj["DisplayName"]?.ToString() ?? name),
            });
        }
        return list.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Win32_Service.StartMode *reports* "Auto" but ChangeStartMode only *accepts* "Automatic"
    // ("Auto" returns 21, invalid parameter), so reading a mode and writing it back needs this mapping.
    internal static string ToWmiStartMode(string mode) =>
        string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase) ? "Automatic" : mode;

    /// <param name="mode">"Auto"/"Automatic", "Manual", or "Disabled"</param>
    public ActionResult SetStartMode(ServiceEntry entry, string mode)
    {
        if (entry.IsProtected)
            return new ActionResult(false, "Msg_ServiceProtectedModify", entry.DisplayName);

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Service");
            using var results = searcher.Get();
            var service = results.Cast<ManagementObject>()
                .FirstOrDefault(o => string.Equals((string?)o["Name"], entry.Name, StringComparison.Ordinal));
            if (service == null)
                return new ActionResult(false, "Msg_ServiceNotFound", entry.Name);

            var result = service.InvokeMethod("ChangeStartMode", new object[] { ToWmiStartMode(mode) });
            var returnCode = Convert.ToUInt32(result);
            if (returnCode == 0)
            {
                entry.StartType = mode;
                return new ActionResult(true, "Msg_ServiceModeChanged", entry.DisplayName, mode);
            }
            return new ActionResult(false, "Msg_ServiceModeFailed", entry.DisplayName, returnCode);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_ServiceModeException", entry.DisplayName, ex.Message);
        }
    }

    public ActionResult Stop(ServiceEntry entry)
    {
        if (entry.IsProtected)
            return new ActionResult(false, "Msg_ServiceProtectedStop", entry.DisplayName);

        try
        {
            using var controller = new System.ServiceProcess.ServiceController(entry.Name);
            if (controller.Status == System.ServiceProcess.ServiceControllerStatus.Running)
            {
                controller.Stop();
                controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
            entry.Status = controller.Status.ToString();
            return new ActionResult(true, "Msg_ServiceStopped", entry.DisplayName);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_ServiceStopFailed", entry.DisplayName, ex.Message);
        }
    }

    // Used to undo a Stop; not exposed as its own toolbar button since Windows
    // already starts Automatic services on its own and Manual ones on demand.
    public ActionResult Start(ServiceEntry entry)
    {
        if (entry.IsProtected)
            return new ActionResult(false, "Msg_ServiceProtectedModify", entry.DisplayName);

        try
        {
            using var controller = new System.ServiceProcess.ServiceController(entry.Name);
            if (controller.Status != System.ServiceProcess.ServiceControllerStatus.Running)
            {
                controller.Start();
                controller.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            }
            entry.Status = controller.Status.ToString();
            return new ActionResult(true, "Msg_ServiceStarted", entry.DisplayName);
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_ServiceStartFailed", entry.DisplayName, ex.Message);
        }
    }
}
