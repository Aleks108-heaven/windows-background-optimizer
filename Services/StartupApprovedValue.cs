namespace BgOptimizer.Services;

/// <summary>
/// The binary values Task Manager and Windows Settings keep under
/// HKCU/HKLM\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Run,Run32,StartupFolder}.
/// The format is undocumented, so this follows what Windows itself writes and is lenient on what it reads:
/// byte 0 is the state (even = enabled, odd = disabled; seen on real machines: 02, 03, 04, 06, 07) and bytes
/// 4..11 hold the time it was last switched as a FILETIME. A missing, empty or unreadable value means "no
/// record", which Windows treats as enabled. Nothing is deleted: switching only rewrites this value.
/// </summary>
internal static class StartupApprovedValue
{
    public const int Length = 12;

    public static bool IsEnabled(object? raw) =>
        raw is not byte[] { Length: > 0 } bytes || (bytes[0] & 1) == 0;

    /// <summary>The 12 bytes Windows writes: 02 + zeros for enabled; 03 + the time of the switch for disabled.</summary>
    public static byte[] Encode(bool enabled, DateTime utcNow)
    {
        var value = new byte[Length];
        value[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(value, 4);
        return value;
    }
}
