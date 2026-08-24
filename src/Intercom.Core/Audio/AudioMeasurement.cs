using System.Text.Json;

namespace Intercom.Audio;

public enum AudioMeasurementProfile
{
    Disabled,
    Clean,
    Loss1Percent,
    Loss5Percent,
    Burst,
}

public static class AudioMeasurementProfileParser
{
    static readonly IReadOnlyDictionary<AudioMeasurementProfile, string> ConfigValues =
        new Dictionary<AudioMeasurementProfile, string>
        {
            [AudioMeasurementProfile.Loss1Percent] = "loss1",
            [AudioMeasurementProfile.Loss5Percent] = "loss5",
            [AudioMeasurementProfile.Burst] = "burst",
            [AudioMeasurementProfile.Clean] = "clean",
            [AudioMeasurementProfile.Disabled] = "disabled",
        };
    public static AudioMeasurementProfile Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "clean" => AudioMeasurementProfile.Clean,
        "loss1" => AudioMeasurementProfile.Loss1Percent,
        "loss5" => AudioMeasurementProfile.Loss5Percent,
        "burst" => AudioMeasurementProfile.Burst,
        _ => AudioMeasurementProfile.Disabled,
    };

    public static string ToConfigValue(this AudioMeasurementProfile profile) =>
        ConfigValues.GetValueOrDefault(profile, "disabled");
}

/// <summary>Deterministic outbound loss used only by the two-PC measurement
/// harness. End markers are never dropped, keeping release-to-silence
/// measurable even in an impaired run.</summary>
public sealed class AudioMeasurementImpairment(AudioMeasurementProfile profile)
{
    public AudioMeasurementProfile Profile { get; } = profile;

    public bool ShouldDrop(ulong sequence, AudioPacketFlags flags = AudioPacketFlags.None)
    {
        if ((flags & AudioPacketFlags.EndOfTalkspurt) != 0) return false;
        return Profile switch
        {
            AudioMeasurementProfile.Loss1Percent => sequence % 100 == 99,
            AudioMeasurementProfile.Loss5Percent => sequence % 20 == 19,
            AudioMeasurementProfile.Burst => sequence % 250 >= 244,
            _ => false,
        };
    }
}

/// <summary>Small file boundary shared by the installed app and harness CLI.
/// Normal installs default to Clean, which performs no packet impairment.</summary>
public static class AudioMeasurementSettings
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Intercom", "audio-measurement.json");

    public static AudioMeasurementProfile Load(string? path = null)
    {
        try
        {
            var json = File.ReadAllText(path ?? DefaultPath);
            return AudioMeasurementProfileParser.Parse(JsonSerializer.Deserialize<SettingsDto>(json)?.Profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return AudioMeasurementProfile.Disabled;
        }
    }

    public static void Save(AudioMeasurementProfile profile, string? path = null)
    {
        var target = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, JsonSerializer.Serialize(new SettingsDto { Profile = profile.ToConfigValue() }));
    }

    sealed class SettingsDto
    {
        public string? Profile { get; set; }
    }
}
