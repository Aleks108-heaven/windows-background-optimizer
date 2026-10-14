using BgOptimizer.Services;
using Xunit;

namespace BgOptimizer.Tests;

/// <summary>The signal a second launch sends to the running copy must reach exactly that copy and no other.</summary>
public class SingleInstanceTests
{
    private const string Installed = @"C:\Program Files\Background Optimizer\BgOptimizer.exe";

    [Fact]
    public void SamePathGivesTheSameName()
    {
        Assert.Equal(SingleInstance.ActivationEventName(Installed), SingleInstance.ActivationEventName(Installed));
    }

    [Fact]
    public void PathCaseDoesNotMatterOnWindows()
    {
        Assert.Equal(
            SingleInstance.ActivationEventName(Installed),
            SingleInstance.ActivationEventName(Installed.ToLowerInvariant()));
    }

    [Fact]
    public void DifferentInstallsDoNotSignalEachOther()
    {
        Assert.NotEqual(
            SingleInstance.ActivationEventName(Installed),
            SingleInstance.ActivationEventName(@"C:\dev\publish\BgOptimizer.exe"));
    }

    [Fact]
    public void NameIsPerSessionAndNotTooLong()
    {
        var name = SingleInstance.ActivationEventName(Installed);
        Assert.StartsWith(@"Local\", name);
        Assert.True(name.Length < 260);
    }
}
