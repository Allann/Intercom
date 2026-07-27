using Windows.Media.Playback;
using Windows.Media.Core;
using Windows.Media.SpeechSynthesis;

namespace Intercom.App.Chat;

/// <summary>
/// Local text-to-speech playback for received chat text (issue #24 "Spoken
/// chat"), per docs/research/windows-resident-app.md's "Local text-to-speech"
/// section: <see cref="SpeechSynthesizer"/> renders text using an installed
/// Windows voice; no audio is ever sent over the network — this is strictly
/// local playback on the receiving device of text that already arrived over
/// the control channel.
///
/// Verified against the Windows.Media.SpeechSynthesis API docs
/// (learn.microsoft.com/uwp/api/windows.media.speechsynthesis.speechsynthesizer
/// and .voiceinformation): <see cref="SpeechSynthesizer.AllVoices"/> enumerates
/// installed voices, <see cref="SpeechSynthesizer.Voice"/> selects one (by
/// default the system voice), and <see cref="SpeechSynthesizer.SynthesizeTextToStreamAsync"/>
/// produces a <see cref="SpeechSynthesisStream"/>. The documented UWP pattern
/// plays that stream through a XAML <c>MediaElement</c>; this class instead
/// uses a bare <see cref="MediaPlayer"/> (no MediaElement in the visual
/// tree needed) via <see cref="MediaSource.CreateFromStream"/>, the
/// standard non-XAML-hosted WinUI 3 desktop equivalent — <see cref="VoiceInformation.Id"/>
/// is a stable identifier suitable for local persistence (see
/// <c>Intercom.Chat.ChatTtsSettingsStore.SelectedVoiceId</c>), while
/// <see cref="VoiceInformation.DisplayName"/> is what a voice picker UI
/// should show a human.
/// </summary>
public sealed class ChatSpeechService : IDisposable
{
    readonly SpeechSynthesizer _synthesizer = new();
    readonly MediaPlayer _player = new();

    /// <summary>All voices installed on this device — for a voice-picker UI
    /// and for validating a persisted <c>SelectedVoiceId</c> is still
    /// installed.</summary>
    public IReadOnlyList<VoiceInformation> InstalledVoices => SpeechSynthesizer.AllVoices;

    public VoiceInformation DefaultVoice => SpeechSynthesizer.DefaultVoice;

    /// <summary>Selects a specific installed voice by its stable
    /// <see cref="VoiceInformation.Id"/>, or falls back to the system default
    /// if no voice with that ID is currently installed (e.g. it was
    /// uninstalled since the preference was saved).</summary>
    public void SelectVoice(string? voiceId)
    {
        var match = voiceId is null
            ? null
            : SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voiceId);
        _synthesizer.Voice = match ?? SpeechSynthesizer.DefaultVoice;
    }

    /// <summary>Synthesizes <paramref name="text"/> with the currently
    /// selected voice and plays it immediately, stopping/replacing whatever
    /// was playing before (a burst of chat messages should speak the newest,
    /// not queue up a backlog of stale audio).</summary>
    public async Task SpeakAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var stream = await _synthesizer.SynthesizeTextToStreamAsync(text);
        _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
        _player.Play();
    }

    /// <summary>Stops any playback in progress — the "stop control" issue
    /// #24 / docs/research/windows-resident-app.md ask for alongside
    /// preview.</summary>
    public void Stop() => _player.Pause();

    public void Dispose()
    {
        _player.Dispose();
        _synthesizer.Dispose();
    }
}
