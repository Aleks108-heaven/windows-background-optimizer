using Microsoft.Win32;

namespace BgOptimizer.Services;

/// <summary>
/// Where the view-only switch, the code hash and the wrong-try counter are kept: a registry key under
/// HKEY_LOCAL_MACHINE, which by default only an administrator can change. settings.json lives in a folder that
/// any program running as the user can edit, so a lock kept there could be switched off by editing a file; this
/// one cannot be, short of administrator rights (which the lock never claimed to stop). The app runs elevated,
/// so it can write here. Reading never throws; a write reports failure instead of pretending it worked.
/// </summary>
internal sealed class LockStore
{
    public const string DefaultSubKey = @"SOFTWARE\BgOptimizer";

    private const string ViewOnlyValue = "ViewOnly";
    private const string CodeHashValue = "CodeHash";
    private const string FailuresValue = "CodeFailures";
    private const string LockedUntilValue = "CodeLockedUntilUtcTicks";
    private const int MaxFailures = 1000;

    private readonly RegistryHive _hive;
    private readonly string _subKey;

    /// <param name="hive">Tests pass CurrentUser with a throwaway key; the app uses the default.</param>
    public LockStore(RegistryHive hive = RegistryHive.LocalMachine, string subKey = DefaultSubKey)
    {
        _hive = hive;
        _subKey = subKey;
    }

    /// <param name="ViewOnly">Null when never saved (the caller may then import the old settings.json value once).</param>
    /// <param name="CodeHash">Null when no code is set, or when the stored value is unusable.</param>
    public sealed record State(bool? ViewOnly, string? CodeHash, int Failures, DateTime LockedUntilUtc)
    {
        public static State Empty { get; } = new(null, null, 0, DateTime.MinValue);
    }

    public State Load()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(_subKey);
            if (key is null) return State.Empty;

            bool? viewOnly = Read(key, ViewOnlyValue) is int v ? v != 0 : null;
            var hash = Read(key, CodeHashValue) as string;
            if (!ViewOnlyLock.IsValid(hash)) hash = null;
            var failures = Read(key, FailuresValue) is int f ? Math.Clamp(f, 0, MaxFailures) : 0;
            var locked = DateTime.MinValue;
            if (Read(key, LockedUntilValue) is long ticks && ticks > DateTime.MinValue.Ticks && ticks < DateTime.MaxValue.Ticks)
                locked = new DateTime(ticks, DateTimeKind.Utc);
            return new State(viewOnly, hash, failures, locked);
        }
        catch { return State.Empty; }
    }

    public bool SetViewOnly(bool on) => Write(key => key.SetValue(ViewOnlyValue, on ? 1 : 0, RegistryValueKind.DWord));

    /// <summary>Stores the hash (null removes the code). A new or removed code also starts the wrong-try count afresh.</summary>
    public bool SetCodeHash(string? hash) => Write(key =>
    {
        if (hash is null) key.DeleteValue(CodeHashValue, throwOnMissingValue: false);
        else key.SetValue(CodeHashValue, hash, RegistryValueKind.String);
        key.DeleteValue(FailuresValue, throwOnMissingValue: false);
        key.DeleteValue(LockedUntilValue, throwOnMissingValue: false);
    });

    public bool SetFailures(int failures, DateTime lockedUntilUtc) => Write(key =>
    {
        if (failures <= 0)
        {
            key.DeleteValue(FailuresValue, throwOnMissingValue: false);
            key.DeleteValue(LockedUntilValue, throwOnMissingValue: false);
            return;
        }
        key.SetValue(FailuresValue, Math.Min(failures, MaxFailures), RegistryValueKind.DWord);
        key.SetValue(LockedUntilValue, lockedUntilUtc.ToUniversalTime().Ticks, RegistryValueKind.QWord);
    });

    private bool Write(Action<RegistryKey> change)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(_subKey, writable: true);
            if (key is null) return false;
            change(key);
            return true;
        }
        catch { return false; }
    }

    private static object? Read(RegistryKey key, string name)
    {
        try { return key.GetValue(name); }
        catch { return null; }
    }
}
