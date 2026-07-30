using Microsoft.UI.Xaml.Controls;
using Intercom.Audio;
namespace Intercom.App;

public sealed partial class SettingsDialog : ContentDialog
{
    bool _loading;

    public SettingsDialog()
    {
        InitializeComponent();
        _loading = true;
        var configured = AudioMeasurementSettings.Load().ToConfigValue();
        AudioMeasurementProfileCombo.SelectedItem = AudioMeasurementProfileCombo.Items
            .OfType<ComboBoxItem>().First(item => (string)item.Tag == configured);
        _loading = false;
    }

    async void OnOpenMicrophoneSettingsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound-defaultinputproperties"));

    async void OnOpenSpeakerSettingsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:sound-defaultoutputproperties"));

    void OnAudioMeasurementProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AudioMeasurementProfileCombo.SelectedItem is not ComboBoxItem { Tag: string profile }) return;
        AudioMeasurementSettings.Save(AudioMeasurementProfileParser.Parse(profile));
    }
}
