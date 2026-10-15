using System.IO;
using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>The optional code that keeps view-only mode on: only a hash is stored, and nothing odd in the file can lock the app or open it.</summary>
public class ViewOnlyLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bgopt-lock-{Guid.NewGuid():N}");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void TheRightCodeIsAcceptedAndOthersAreNot()
    {
        var stored = ViewOnlyLock.Create("correct horse");

        Assert.True(ViewOnlyLock.Verify("correct horse", stored));
        Assert.False(ViewOnlyLock.Verify("correct hors", stored));
        Assert.False(ViewOnlyLock.Verify("Correct horse", stored));
        Assert.False(ViewOnlyLock.Verify("", stored));
        Assert.False(ViewOnlyLock.Verify(null, stored));
    }

    [Fact]
    public void TheCodeItselfIsNeverStoredAndEveryHashUsesItsOwnSalt()
    {
        var a = ViewOnlyLock.Create("swordfish");
        var b = ViewOnlyLock.Create("swordfish");

        Assert.DoesNotContain("swordfish", a);
        Assert.NotEqual(a, b);
        Assert.True(ViewOnlyLock.Verify("swordfish", a));
        Assert.True(ViewOnlyLock.Verify("swordfish", b));
    }

    [Fact]
    public void NonAsciiCodesWork()
    {
        var stored = ViewOnlyLock.Create("пароль-Ж1");
        Assert.True(ViewOnlyLock.Verify("пароль-Ж1", stored));
        Assert.False(ViewOnlyLock.Verify("пароль-ж1", stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hunter2")]
    [InlineData("pbkdf2-sha256$200000$AAAA")]                                  // missing the hash
    [InlineData("md5$200000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$notanumber$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]          // far too few rounds
    [InlineData("pbkdf2-sha256$2000000000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")] // would hang the check
    [InlineData("pbkdf2-sha256$200000$%%%$%%%")]
    public void ADamagedOrForeignStoredValueIsRejectedWithoutThrowing(string? stored)
    {
        Assert.False(ViewOnlyLock.IsValid(stored));
        Assert.False(ViewOnlyLock.Verify("anything", stored));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 5)]
    [InlineData(4, 10)]
    [InlineData(100, 60)]
    public void WrongTriesSlowDownAfterTheSecondOne(int failures, int expectedSeconds) =>
        Assert.Equal(expectedSeconds, ViewOnlyLock.WaitSeconds(failures));

    [Fact]
    public void TheHashSurvivesARestart()
    {
        var path = Path.Combine(_dir, "settings.json");
        var stored = ViewOnlyLock.Create("open sesame");
        new AppSettings { ViewOnly = true, ViewOnlyCodeHash = stored }.Save(path);

        var loaded = AppSettings.Load(path);

        Assert.True(loaded.ViewOnly);
        Assert.True(ViewOnlyLock.Verify("open sesame", loaded.ViewOnlyCodeHash));
    }

    [Fact]
    public void AHandEditedUnusableHashMeansNoCodeRatherThanAModeNobodyCanLeave()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "edited.json");
        File.WriteAllText(path, "{ \"ViewOnly\": true, \"ViewOnlyCodeHash\": \"plain-text-code\" }");

        var loaded = AppSettings.Load(path);

        Assert.True(loaded.ViewOnly);
        Assert.Null(loaded.ViewOnlyCodeHash);
    }
}
