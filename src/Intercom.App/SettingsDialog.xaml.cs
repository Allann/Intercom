using Microsoft.UI.Xaml.Controls;
namespace Intercom.App;

public sealed partial class SettingsDialog : ContentDialog
{
    public SettingsDialog() => InitializeComponent();

    async void OnOpenMicrophoneSettingsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound-defaultinputproperties"));

    async void OnOpenSpeakerSettingsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound-defaultoutputproperties"));
}
