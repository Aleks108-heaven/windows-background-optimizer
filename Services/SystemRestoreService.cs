using System.Management;
using BgOptimizer.Localization;

namespace BgOptimizer.Services;

/// <summary>
/// Wraps the SystemRestore WMI class (root\default) so bulk Disable/Stop
/// actions can leave a rollback point behind first. Best-effort: System
/// Restore might be off for the system drive, or Windows may silently skip
/// the call under its own default once-per-24-hours throttle - neither is
/// treated as fatal here, since this is a safety net around the user's
/// actual request, not a gate on it.
/// </summary>
internal static class SystemRestoreService
{
    // RestorePointType / EventType constants from the SystemRestore WMI class
    // (root\default): https://learn.microsoft.com/windows/win32/sr/createrestorepoint-systemrestore
    private const uint ModifySettings = 12; // RestorePointType
    private const uint BeginSystemChange = 100; // EventType

    public static ActionResult CreateRestorePoint(string description)
    {
        try
        {
            using var restoreClass = new ManagementClass(@"root\default", "SystemRestore", null);
            using var inParams = restoreClass.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = ModifySettings;
            inParams["EventType"] = BeginSystemChange;

            using var outParams = restoreClass.InvokeMethod("CreateRestorePoint", inParams, null);
            var returnCode = Convert.ToUInt32(outParams?["ReturnValue"]);
            return returnCode == 0
                ? new ActionResult(true, "Msg_RestorePointCreated")
                : new ActionResult(false, "Msg_RestorePointFailed", $"WMI return code {returnCode}");
        }
        catch (Exception ex)
        {
            return new ActionResult(false, "Msg_RestorePointFailed", ex.Message);
        }
    }
}
