using Serilog;
using Serilog.Core;

namespace Intercom.Diagnostics;

/// <summary>
/// Persistent, best-effort diagnostics for installed builds. One small
/// interface hides timestamping, exception formatting, synchronization,
/// bounded rotation, directory creation, and I/O failure handling.
/// Logging must never become a new reason for the resident app to fail.
/// </summary>
public sealed class DiagnosticLog : IDisposable
{
    const long DefaultMaxBytes = 1024 * 1024;
    const int DefaultHistoryFiles = 2;

    readonly Logger _logger;

    public static DiagnosticLog Current { get; } = new();

    public string LogFilePath { get; }

    public DiagnosticLog(string? appDataDirectory = null, long maxBytes = DefaultMaxBytes, int historyFiles = DefaultHistoryFiles)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (historyFiles < 0) throw new ArgumentOutOfRangeException(nameof(historyFiles));

        var appData = appDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intercom");
        LogFilePath = Path.Combine(appData, "logs", "intercom.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
            _logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    LogFilePath,
                    outputTemplate: "{Timestamp:O} [{Level:u3}] {EventName} | {Message:lj}{NewLine}{Exception}",
                    fileSizeLimitBytes: maxBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: historyFiles + 1,
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1))
                .CreateLogger();
        }
        catch
        {
            _logger = new LoggerConfiguration().CreateLogger();
        }
    }

    public void Info(string eventName, string message) =>
        _logger.ForContext("EventName", eventName).Information("{DiagnosticMessage}", message);

    public void Warning(string eventName, string message, Exception? exception = null) =>
        _logger.ForContext("EventName", eventName).Warning(exception, "{DiagnosticMessage}", message);

    public void Error(string eventName, string message, Exception? exception = null) =>
        _logger.ForContext("EventName", eventName).Error(exception, "{DiagnosticMessage}", message);

    public void Dispose() => _logger.Dispose();
}
