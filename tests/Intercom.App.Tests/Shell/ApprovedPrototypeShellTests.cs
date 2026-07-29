using Xunit;

namespace Intercom.App.Tests.Shell;

public sealed class ApprovedPrototypeShellTests
{
    static readonly string XamlPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Intercom.App", "MainWindow.xaml"));

    [Fact]
    public void MainWindow_ContainsTheApprovedIntegratedPrototypeRegions()
    {
        var xaml = File.ReadAllText(XamlPath);

        Assert.Contains("Text=\"FAMILY\"", xaml);
        Assert.Contains("Text=\"GROUP FLOOR\"", xaml);
        Assert.Contains("Text=\"HANDS-FREE\"", xaml);
        Assert.Contains("x:Name=\"PushToTalkButton\"", xaml);
        Assert.Contains("Text=\"CHAT\"", xaml);
        Assert.Contains("Text=\"ATTENTION CARDS\"", xaml);
        Assert.Contains("Text=\"SEND AN ATTENTION CARD\"", xaml);
    }

    [Fact]
    public void MainWindow_DoesNotUseTheRejectedPlaceholderShell()
    {
        var xaml = File.ReadAllText(XamlPath);

        Assert.DoesNotContain("Text=\"NEARBY DEVICES\"", xaml);
        Assert.DoesNotContain("Text=\"STATUS\"", xaml);
        Assert.DoesNotContain("demo peer", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("loopback", xaml, StringComparison.OrdinalIgnoreCase);
    }
}
