using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Intercom.Diagnostics;

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
        var log = DiagnosticLog.Current;
        log.Info("process.start", $"version={typeof(Program).Assembly.GetName().Version} os={Environment.OSVersion} args={string.Join(' ', args)}");
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            log.Error("process.unhandled", "Unhandled AppDomain exception.", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            log.Error("task.unobserved", "Unobserved task exception.", eventArgs.Exception);

        try
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
                log.Info("process.redirect", "Redirecting activation to the resident instance.");
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
            log.Info("process.exit", "Application.Start returned.");
        }
        catch (Exception ex)
        {
            // Last managed boundary around bootstrap, activation and the XAML
            // message loop. Re-throw after persisting the full exception so
            // Windows still records the process as failed.
            log.Error("process.fatal", "Fatal exception escaped the application entry point.", ex);
            throw;
        }
        finally
        {
            // Serilog's file sink buffers briefly; an abnormal startup must
            // flush before Windows tears the process down.
            log.Dispose();
        }
    }
}
