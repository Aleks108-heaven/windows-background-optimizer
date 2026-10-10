using System.IO;
using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>The persistent action log: it must keep a record without ever getting in the app's way.</summary>
public class ActionLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bgopt-log-{Guid.NewGuid():N}");
    private string LogPath => Path.Combine(_dir, "sub", "actions.log");

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void AppendsTimestampedLinesAndCreatesTheFolder()
    {
        ActionLog.Write("first", LogPath);
        ActionLog.Write("second", LogPath);
        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        Assert.Matches(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d \[\d+\] first$", lines[0]);
        Assert.EndsWith("second", lines[1]);
    }

    [Fact]
    public void MultilineMessagesStayOnOneLine()
    {
        ActionLog.Write("a\r\nb\nc", LogPath);
        Assert.Single(File.ReadAllLines(LogPath));
    }

    [Fact]
    public void RollsOverToOneBackupAtOneMegabyte()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, new string('x', 1024 * 1024));
        ActionLog.Write("after rollover", LogPath);
        Assert.True(File.Exists(LogPath + ".1"));
        Assert.Single(File.ReadAllLines(LogPath));
        Assert.Contains("after rollover", File.ReadAllText(LogPath));
    }

    [Fact]
    public void NeverThrowsWhenThePathIsUnusable()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "x");
        var ex = Record.Exception(() => ActionLog.Write("nope", Path.Combine(blocker, "actions.log")));
        Assert.Null(ex);
    }
}
