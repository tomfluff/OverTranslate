using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class LocalSpeechTests
{
    // SAPI's rate is logarithmic: ±10 is three times faster or slower.
    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(3.0, 10)]
    [InlineData(1 / 3.0, -10)]
    [InlineData(2.0, 6)]
    [InlineData(10.0, 10)]   // clamped
    public void SapiRate_maps_speed(double speed, int expected) =>
        Assert.Equal(expected, LocalSpeech.SapiRate(speed));

    // Pitch is made by relabelling the sample rate, so the fmt chunk must be found past any chunk before it.
    [Fact]
    public void ScaleSampleRate_rewrites_the_fmt_chunk()
    {
        var wav = new List<byte>();
        wav.AddRange("RIFF"u8.ToArray()); wav.AddRange(BitConverter.GetBytes(0u)); wav.AddRange("WAVE"u8.ToArray());
        wav.AddRange("LIST"u8.ToArray()); wav.AddRange(BitConverter.GetBytes(3u)); wav.AddRange(new byte[] { 1, 2, 3, 0 }); // odd size, padded
        wav.AddRange("fmt "u8.ToArray()); wav.AddRange(BitConverter.GetBytes(16u));
        wav.AddRange(BitConverter.GetBytes((ushort)1)); wav.AddRange(BitConverter.GetBytes((ushort)1));
        wav.AddRange(BitConverter.GetBytes(16000u)); wav.AddRange(BitConverter.GetBytes(32000u));
        wav.AddRange(BitConverter.GetBytes((ushort)2)); wav.AddRange(BitConverter.GetBytes((ushort)16));
        var bytes = wav.ToArray();

        LocalSpeech.ScaleSampleRate(bytes, 1.25);

        var fmt = 12 + 12;   // after the padded LIST chunk
        Assert.Equal(20000u, BitConverter.ToUInt32(bytes, fmt + 12));
        Assert.Equal(40000u, BitConverter.ToUInt32(bytes, fmt + 16));
    }
}
