using Intercom.Diagnostics;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Intercom.App.Notifications;

/// <summary>Owns the app's Windows taskbar unread badge.</summary>
public sealed class UnreadBadgePresenter
{
    const int MaximumDisplayedCount = 99;
    int _count;

    public int Count => _count;

    public void Increment()
    {
        if (_count < int.MaxValue) _count++;
        UpdateWindowsBadge();
    }

    public void Clear()
    {
        if (_count == 0) return;
        _count = 0;
        TryUpdate(updater => updater.Clear());
    }

    void UpdateWindowsBadge()
    {
        var displayedCount = Math.Min(_count, MaximumDisplayedCount);
        var xml = new XmlDocument();
        xml.LoadXml($"<badge value=\"{displayedCount}\"/>");
        var notification = new BadgeNotification(xml);
        TryUpdate(updater => updater.Update(notification));
    }

    static void TryUpdate(Action<BadgeUpdater> update)
    {
        try
        {
            update(BadgeUpdateManager.CreateBadgeUpdaterForApplication());
        }
        catch (Exception ex)
        {
            // A badge is supplementary presentation. Unpackaged/dev runs or
            // shell policy can reject it; receiving the message must not.
            DiagnosticLog.Current.Warning("notifications.badge-unavailable", "Could not update the Windows taskbar badge.", ex);
        }
    }
}
