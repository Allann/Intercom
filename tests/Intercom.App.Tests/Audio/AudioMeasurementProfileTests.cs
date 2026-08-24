using Intercom.Audio;
using Xunit;

namespace Intercom.App.Tests.Audio;

public sealed class AudioMeasurementProfileTests
{
    [Theory]
    [InlineData(AudioMeasurementProfile.Disabled, "disabled")]
    [InlineData(AudioMeasurementProfile.Clean, "clean")]
    [InlineData(AudioMeasurementProfile.Loss1Percent, "loss1")]
    [InlineData(AudioMeasurementProfile.Loss5Percent, "loss5")]
    [InlineData(AudioMeasurementProfile.Burst, "burst")]
    [InlineData((AudioMeasurementProfile)999, "disabled")]
    public void ToConfigValue_MapsEveryProfileAndUnknownValues(AudioMeasurementProfile profile, string expected) =>
        Assert.Equal(expected, profile.ToConfigValue());

    [Theory]
    [InlineData("clean", AudioMeasurementProfile.Clean)]
    [InlineData("loss1", AudioMeasurementProfile.Loss1Percent)]
    [InlineData("loss5", AudioMeasurementProfile.Loss5Percent)]
    [InlineData("burst", AudioMeasurementProfile.Burst)]
    public void Parse_RecognisesHarnessProfiles(string value, AudioMeasurementProfile expected) =>
        Assert.Equal(expected, AudioMeasurementProfileParser.Parse(value));

    [Fact]
    public void MissingProfile_DisablesHarnessRatherThanImpairingNormalAudio() =>
        Assert.Equal(AudioMeasurementProfile.Disabled, AudioMeasurementProfileParser.Parse(null));

    [Fact]
    public void OnePercent_DropsExactlyOneFramePerHundred()
    {
        var impairment = new AudioMeasurementImpairment(AudioMeasurementProfile.Loss1Percent);
        Assert.Equal(1, Enumerable.Range(0, 100).Count(sequence => impairment.ShouldDrop((ulong)sequence)));
    }

    [Fact]
    public void FivePercent_DropsExactlyFiveFramesPerHundred()
    {
        var impairment = new AudioMeasurementImpairment(AudioMeasurementProfile.Loss5Percent);
        Assert.Equal(5, Enumerable.Range(0, 100).Count(sequence => impairment.ShouldDrop((ulong)sequence)));
    }

    [Fact]
    public void Burst_DropsSixConsecutiveFramesEveryFiveSeconds()
    {
        var impairment = new AudioMeasurementImpairment(AudioMeasurementProfile.Burst);
        var dropped = Enumerable.Range(0, 250).Where(sequence => impairment.ShouldDrop((ulong)sequence)).ToArray();
        Assert.Equal(new[] { 244, 245, 246, 247, 248, 249 }, dropped);
    }

    [Fact]
    public void EndMarker_IsNeverSubjectToImpairment()
    {
        var impairment = new AudioMeasurementImpairment(AudioMeasurementProfile.Loss5Percent);
        Assert.False(impairment.ShouldDrop(99, AudioPacketFlags.EndOfTalkspurt));
    }
}
