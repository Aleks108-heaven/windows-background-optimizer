using BgOptimizer.Localization;

namespace BgOptimizer.Services;

/// <summary>
/// "View-only mode": the app watches and reports, and changes nothing on the system. The check lives in the
/// services that make the changes (not just in the buttons), so no code path can slip past it: a process is
/// never slowed, frozen, trimmed or closed; startup items, services, security settings, tweaks, restore points
/// and files are never touched.
/// Deliberately NOT blocked: undoing or releasing something (resuming a frozen process, restoring a priority).
/// Turning the mode on must never leave anything stuck, so the way back always stays open.
/// </summary>
internal static class ViewOnlyMode
{
    private static volatile bool _enabled;

    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Null when changes are allowed; otherwise the refusal to return from the action.</summary>
    public static ActionResult? Block(string what) =>
        _enabled ? new ActionResult(false, "Msg_ViewOnlyBlocked", what) : null;
}
