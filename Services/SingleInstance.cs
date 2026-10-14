using System.Security.Cryptography;
using System.Text;

namespace BgOptimizer.Services;

/// <summary>
/// Naming for the "show yourself" signal between a second launch and the copy that is already running.
/// The app hides in the tray when minimised, so launching it again (taskbar, Start menu) must bring that
/// copy back rather than exit silently. The name is derived from the exe path so the installed copy and a
/// copy run from somewhere else stay independent, which matches how the single-instance check counts copies.
/// </summary>
internal static class SingleInstance
{
    internal static string ActivationEventName(string exePath)
    {
        var normalised = exePath.Trim().ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        // Local\ keeps it per logon session, so another user's copy is never signalled.
        return "Local\\BgOptimizer.Activate." + Convert.ToHexString(hash, 0, 8);
    }
}
