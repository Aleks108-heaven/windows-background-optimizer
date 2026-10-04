using System.IO;
using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>The saved checkbox/threshold/language choices: they must survive a restart and never break startup.</summary>
public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bgopt-settings-{Guid.NewGuid():N}");
    private string PathIn(string name) => Path.Combine(_dir, name);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void ChoicesSurviveARestart()
    {
        var path = PathIn("settings.json");
        new AppSettings
        {
            Language = "uk", AutoOptimize = true, FreeRamThresholdGb = "5", AllowSuspend = true,
            AutoRestorePoint = false, PersistPriority = true,
        }.Save(path);

        var loaded = AppSettings.Load(path);
        Assert.Equal("uk", loaded.Language);
        Assert.True(loaded.AutoOptimize);
        Assert.Equal("5", loaded.FreeRamThresholdGb);
        Assert.True(loaded.AllowSuspend);
        Assert.False(loaded.AutoRestorePoint);
        Assert.True(loaded.PersistPriority);
    }

    [Fact]
    public void MissingFileGivesSafeDefaults()
    {
        var s = AppSettings.Load(PathIn("nope.json"));
        Assert.Equal("en", s.Language);
        Assert.False(s.AutoOptimize);   // unattended throttling is never on unless the user turned it on
        Assert.False(s.AllowSuspend);
        Assert.True(s.AutoRestorePoint);
        Assert.Equal("2", s.FreeRamThresholdGb);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    public void CorruptFileFallsBackToDefaultsInsteadOfThrowing(string content)
    {
        Directory.CreateDirectory(_dir);
        var path = PathIn("settings.json");
        File.WriteAllText(path, content);
        var s = AppSettings.Load(path);
        Assert.False(s.AutoOptimize);
        Assert.Equal("en", s.Language);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("99999")]
    public void UnusableThresholdIsReplacedByTheDefault(string threshold)
    {
        Directory.CreateDirectory(_dir);
        var path = PathIn("settings.json");
        File.WriteAllText(path, $"{{\"FreeRamThresholdGb\":\"{threshold}\",\"AutoOptimize\":true}}");
        var s = AppSettings.Load(path);
        Assert.Equal("2", s.FreeRamThresholdGb);
        Assert.True(s.AutoOptimize); // the valid fields in the same file are kept
    }

    [Theory]
    [InlineData("2.5")]
    [InlineData("2,5")]
    [InlineData("8")]
    public void ReasonableThresholdIsKeptAsTyped(string threshold)
    {
        Directory.CreateDirectory(_dir);
        var path = PathIn("settings.json");
        File.WriteAllText(path, $"{{\"FreeRamThresholdGb\":\"{threshold}\"}}");
        Assert.Equal(threshold, AppSettings.Load(path).FreeRamThresholdGb);
    }

    [Fact]
    public void UnknownLanguageFallsBackToEnglish()
    {
        Directory.CreateDirectory(_dir);
        var path = PathIn("settings.json");
        File.WriteAllText(path, "{\"Language\":\"xx\"}");
        Assert.Equal("en", AppSettings.Load(path).Language);
    }

    [Fact]
    public void SaveLeavesNoTempFileAndOverwritesTheOldOne()
    {
        var path = PathIn("settings.json");
        new AppSettings { AutoOptimize = true }.Save(path);
        new AppSettings { AutoOptimize = false }.Save(path);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.False(AppSettings.Load(path).AutoOptimize);
    }

    [Fact]
    public void SaveToAnUnwritableLocationDoesNotThrow()
    {
        // A path whose "directory" is actually a file can't be created.
        Directory.CreateDirectory(_dir);
        var blocker = PathIn("blocker");
        File.WriteAllText(blocker, "x");
        new AppSettings().Save(Path.Combine(blocker, "settings.json"));
    }
}
