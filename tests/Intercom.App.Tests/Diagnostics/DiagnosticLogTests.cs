using Intercom.Diagnostics;
using Xunit;

namespace Intercom.App.Tests.Diagnostics;

public sealed class DiagnosticLogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "IntercomLogTests_" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void WritesStructuredEventAndExceptionToPredictablePath()
    {
        using var log = new DiagnosticLog(_dir);

        log.Error("startup.failed", "Could not start", new InvalidOperationException("broken"));
        log.Dispose();

        Assert.Equal(Path.Combine(_dir, "logs", "intercom.log"), log.LogFilePath);
        var text = File.ReadAllText(log.LogFilePath);
        Assert.Contains("[ERR] startup.failed | Could not start", text);
        Assert.Contains("InvalidOperationException: broken", text);
    }

    [Fact]
    public void RotatesBeforeLogExceedsConfiguredBound()
    {
        using var log = new DiagnosticLog(_dir, maxBytes: 100, historyFiles: 2);

        log.Info("first", new string('a', 80));
        log.Info("second", new string('b', 80));

        log.Dispose();
        var files = Directory.GetFiles(Path.GetDirectoryName(log.LogFilePath)!, "intercom*.log");
        Assert.True(files.Length >= 2);
        var combined = string.Join("\n", files.Select(File.ReadAllText));
        Assert.Contains("second", combined);
        Assert.Contains("first", combined);
    }
}
