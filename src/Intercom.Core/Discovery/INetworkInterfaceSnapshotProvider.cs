namespace Intercom.Discovery;

/// <summary>Wraps NetworkInterface.GetAllNetworkInterfaces() behind a seam so
/// interface-eligibility and re-evaluation logic can be driven by a fake
/// snapshot in tests instead of whatever NICs happen to exist on the test
/// runner. See ITrayIcon/IStartupService in Intercom.Lifecycle for the same
/// pattern applied to other OS-facing concerns.</summary>
public interface INetworkInterfaceSnapshotProvider
{
    IReadOnlyList<LanInterface> GetCurrentInterfaces();
}
