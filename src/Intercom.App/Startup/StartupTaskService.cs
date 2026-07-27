using Windows.ApplicationModel;

namespace Intercom.App.Startup;

public enum StartupPreference
{
    Enabled,
    DisabledByUser,
    DisabledByPolicy,
    Unknown,
}

/// <summary>
/// Wraps Windows.ApplicationModel.StartupTask. Per docs/adr/0003 the app enables
/// this automatically on first run with no prompt, then always reflects the real
/// OS state rather than assuming its own request is authoritative.
///
/// NOTE: StartupTask requires package identity (MSIX). It will throw when run
/// unpackaged — this is expected until the MSIX packaging pass (ticket #18's
/// remaining scope) is wired up, not a bug in this class.
/// </summary>
public sealed class StartupTaskService
{
    const string TaskId = "IntercomStartupTask";

    public async Task<StartupPreference> GetCurrentStateAsync()
    {
        var task = await StartupTask.GetAsync(TaskId);
        return task.State switch
        {
            StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => StartupPreference.Enabled,
            StartupTaskState.Disabled => StartupPreference.DisabledByUser,
            StartupTaskState.DisabledByPolicy or StartupTaskState.DisabledByUser => StartupPreference.DisabledByPolicy,
            _ => StartupPreference.Unknown,
        };
    }

    /// <summary>
    /// Called once, silently, during first-run setup (ADR-0003: no prompt — installing
    /// an always-reachable intercom already implies wanting it to start with Windows).
    /// </summary>
    public async Task<StartupPreference> EnableOnFirstRunAsync()
    {
        var task = await StartupTask.GetAsync(TaskId);
        if (task.State == StartupTaskState.Disabled)
        {
            await task.RequestEnableAsync();
        }
        return await GetCurrentStateAsync();
    }
}
