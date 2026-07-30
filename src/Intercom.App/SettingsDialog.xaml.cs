using Microsoft.UI.Xaml.Controls;
using Intercom.Audio;
using Intercom.App.Audio;
using Intercom.Diagnostics;
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

    async void OnAudioLifecycleTestClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        AudioLifecycleTestButton.IsEnabled = false;
        AudioLifecycleTestStatus.Text = "Starting audio lifecycle test…";
        var progress = new Progress<AudioLifecycleProgress>(update =>
            AudioLifecycleTestStatus.Text =
                $"Completed {update.CompletedCycles} of {AudioLifecycleSoakTest.CycleCount} cycles… " +
                $"{update.Resources.PrivateMemory / 1024d / 1024d:0.0} MB private, " +
                $"{update.Resources.ManagedMemory / 1024d / 1024d:0.0} MB managed, " +
                $"{update.Resources.Handles} handles.\n" +
                $"Input: {update.InputDevice}\nOutput: {update.OutputDevice}");
        try
        {
            var result = await AudioLifecycleSoakTest.RunAsync(progress);
            AudioLifecycleTestStatus.Text = result.Summary;
            DiagnosticLog.Current.Info("audio.lifecycle-soak-complete", result.Summary);
        }
        catch (Exception ex)
        {
            AudioLifecycleTestStatus.Text = $"Test failed: {ex.Message}";
            DiagnosticLog.Current.Error("audio.lifecycle-soak-failed", AudioLifecycleTestStatus.Text, ex);
        }
        finally
        {
            AudioLifecycleTestButton.IsEnabled = true;
        }
    }

    void OnAudioMeasurementProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AudioMeasurementProfileCombo.SelectedItem is not ComboBoxItem { Tag: string profile }) return;
        AudioMeasurementSettings.Save(AudioMeasurementProfileParser.Parse(profile));
    }
}
