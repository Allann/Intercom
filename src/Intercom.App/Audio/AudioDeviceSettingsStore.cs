using System.Text.Json;

namespace Intercom.App.Audio;

public sealed record AudioDeviceSettings(string? InputDeviceId, string? OutputDeviceId);

public sealed class AudioDeviceSettingsStore
{
    readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Intercom", "audio-devices.json");

    public AudioDeviceSettings Load()
    {
        try { return JsonSerializer.Deserialize<AudioDeviceSettings>(File.ReadAllText(_path)) ?? new(null, null); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(null, null); }
    }

    public void Save(AudioDeviceSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings));
    }
}

public sealed record AudioDeviceChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}
