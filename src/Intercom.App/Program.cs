using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Intercom.App;

/// <summary>
/// Custom entry point (XAML's generated Main is disabled via
/// DISABLE_XAML_GENERATED_MAIN) so we can check single-instancing before any
/// XAML/window is created, per docs/research/windows-resident-app.md.
/// </summary>
public static class Program
{
    const string InstanceKey = "Intercom.SingleInstance";

    [STAThread]
    static void Main(string[] args)
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (!mainInstance.IsCurrent)
        {
            // Another instance already owns this key: hand off activation to it
            // and exit before creating any XAML UI.
            mainInstance.RedirectActivationToAsync(activationArgs).AsTask().Wait();
            return;
        }

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
