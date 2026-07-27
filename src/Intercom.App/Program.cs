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

    public static AppActivationArguments InitialActivationArguments { get; private set; } = null!;
    public static event Action<AppActivationArguments>? RedirectedActivationReceived;

    [STAThread]
    static void Main(string[] args)
    {
        // The auto-generated WinUI 3 Main (disabled here via
        // DISABLE_XAML_GENERATED_MAIN so single-instancing can run first)
        // always calls this before any WinRT activation. Without it, CsWinRT's
        // COM wrapper/activation-factory hooks are never registered, and the
        // first real WinRT call — even AppInstance.GetCurrent() below — fails
        // hard inside combase.dll instead of throwing a catchable exception.
        WinRT.ComWrappersSupport.InitializeComWrappers();

        var currentInstance = AppInstance.GetCurrent();
        var activationArgs = currentInstance.GetActivatedEventArgs();
        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (!mainInstance.IsCurrent)
        {
            // Another instance already owns this key: hand off activation to it
            // and exit before creating any XAML UI.
            mainInstance.RedirectActivationToAsync(activationArgs).AsTask().Wait();
            return;
        }

        InitialActivationArguments = activationArgs;
        mainInstance.Activated += (_, redirectedArgs) =>
            RedirectedActivationReceived?.Invoke(redirectedArgs);

        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}
