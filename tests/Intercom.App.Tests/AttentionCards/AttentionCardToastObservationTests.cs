using Xunit;

namespace Intercom.App.Tests.AttentionCards;

public sealed class AttentionCardToastObservationTests
{
    [Fact]
    public void IncomingCardPresentation_IsObservedAndFailuresAreSurfaced()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "src", "Intercom.App", "MainWindow.xaml.cs"));
        var source = File.ReadAllText(path);

        Assert.Contains("await _attentionCardToastPresenter.ShowAsync", source);
        Assert.DoesNotContain("_ = _attentionCardToastPresenter?.ShowAsync", source);
        Assert.Contains("attention-card.toast-failed", source);
    }
}
