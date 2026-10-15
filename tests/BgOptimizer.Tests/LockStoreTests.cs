using BgOptimizer.Services;
using Microsoft.Win32;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>
/// The store that keeps view-only mode and its code out of the user-editable settings.json. The tests use a
/// throwaway key under HKEY_CURRENT_USER (writable without administrator rights); the app uses HKEY_LOCAL_MACHINE.
/// </summary>
public class LockStoreTests : IDisposable
{
    private readonly string _subKey = $@"Software\BgOptimizerTests\{Guid.NewGuid():N}";
    private readonly LockStore _store;

    public LockStoreTests() => _store = new LockStore(RegistryHive.CurrentUser, _subKey);

    public void Dispose()
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        hkcu.DeleteSubKeyTree(_subKey, throwOnMissingSubKey: false);
        using var parent = hkcu.OpenSubKey(@"Software\BgOptimizerTests");
        if (parent is { SubKeyCount: 0 }) hkcu.DeleteSubKey(@"Software\BgOptimizerTests", throwOnMissingSubKey: false);
    }

    private void Poke(string name, object value, RegistryValueKind kind)
    {
        using var hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = hkcu.CreateSubKey(_subKey, writable: true)!;
        key.SetValue(name, value, kind);
    }

    [Fact]
    public void NothingSavedMeansNothingSet()
    {
        var s = _store.Load();

        Assert.Null(s.ViewOnly);   // the caller may then import the old settings.json value once
        Assert.Null(s.CodeHash);
        Assert.Equal(0, s.Failures);
    }

    [Fact]
    public void ViewOnlyRoundTripsBothWays()
    {
        Assert.True(_store.SetViewOnly(true));
        Assert.True(_store.Load().ViewOnly);

        Assert.True(_store.SetViewOnly(false));
        Assert.False(_store.Load().ViewOnly);   // an explicit "off" is remembered, not treated as "never saved"
    }

    [Fact]
    public void TheCodeHashRoundTripsAndStillVerifies()
    {
        var hash = ViewOnlyLock.Create("a long passphrase");
        Assert.True(_store.SetCodeHash(hash));

        Assert.True(ViewOnlyLock.Verify("a long passphrase", _store.Load().CodeHash));
        Assert.False(ViewOnlyLock.Verify("another passphrase", _store.Load().CodeHash));
    }

    [Fact]
    public void RemovingTheCodeRemovesItAndTheWrongTryCount()
    {
        _store.SetCodeHash(ViewOnlyLock.Create("a long passphrase"));
        _store.SetFailures(4, DateTime.UtcNow.AddSeconds(10));

        Assert.True(_store.SetCodeHash(null));

        var s = _store.Load();
        Assert.Null(s.CodeHash);
        Assert.Equal(0, s.Failures);
    }

    [Fact]
    public void ChangingTheCodeStartsTheWrongTryCountAfresh()
    {
        _store.SetCodeHash(ViewOnlyLock.Create("first passphrase"));
        _store.SetFailures(7, DateTime.UtcNow.AddSeconds(30));

        _store.SetCodeHash(ViewOnlyLock.Create("second passphrase"));

        Assert.Equal(0, _store.Load().Failures);
    }

    [Fact]
    public void WrongTriesSurviveARestartSoRelaunchingDoesNotResetTheDelay()
    {
        var until = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        Assert.True(_store.SetFailures(5, until));

        var s = new LockStore(RegistryHive.CurrentUser, _subKey).Load();   // a fresh object, as after a restart

        Assert.Equal(5, s.Failures);
        Assert.Equal(until, s.LockedUntilUtc);
    }

    [Fact]
    public void ClearingTheFailuresDeletesThem()
    {
        _store.SetFailures(3, DateTime.UtcNow.AddSeconds(5));
        Assert.True(_store.SetFailures(0, DateTime.MinValue));

        var s = _store.Load();
        Assert.Equal(0, s.Failures);
        Assert.Equal(DateTime.MinValue, s.LockedUntilUtc);
    }

    [Fact]
    public void AnUnusableStoredHashCountsAsNoCodeAndOddValuesNeverThrow()
    {
        Poke("CodeHash", "plain-text-code", RegistryValueKind.String);
        Poke("ViewOnly", "yes", RegistryValueKind.String);                  // wrong type: ignored, not "on"
        Poke("CodeFailures", int.MaxValue, RegistryValueKind.DWord);        // clamped
        Poke("CodeLockedUntilUtcTicks", long.MaxValue, RegistryValueKind.QWord); // out of range: ignored

        var s = _store.Load();

        Assert.Null(s.CodeHash);
        Assert.Null(s.ViewOnly);
        Assert.InRange(s.Failures, 0, 1000);
        Assert.Equal(DateTime.MinValue, s.LockedUntilUtc);
    }

    [Fact]
    public void AWriteThatCannotBeMadeReportsFailureInsteadOfPretending()
    {
        // HKEY_LOCAL_MACHINE\SYSTEM\... is not writable from a test run; a read-only hive stands in for "not an administrator".
        var denied = new LockStore(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BgOptimizerTestNever\x");
        if (IsElevated()) return;   // an elevated run could actually write there; do not touch HKLM in that case

        Assert.False(denied.SetViewOnly(true));
        Assert.False(denied.SetCodeHash("pbkdf2-sha256$200000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));
    }

    private static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
