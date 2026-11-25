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

    // A 1% shift of 22050 Hz rounds; the byte rate must follow the rounded rate, not round on its own.
    [Fact]
    public void ScaleSampleRate_keeps_byte_rate_equal_to_rate_times_block_align()
    {
        var bytes = new byte[44];
        "RIFF"u8.CopyTo(bytes); "WAVE"u8.CopyTo(bytes.AsSpan(8)); "fmt "u8.CopyTo(bytes.AsSpan(12));
        BitConverter.TryWriteBytes(bytes.AsSpan(16), 16u);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), 22050u);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), 44100u);
        BitConverter.TryWriteBytes(bytes.AsSpan(32), (ushort)2);

        LocalSpeech.ScaleSampleRate(bytes, 1.01);

        var rate = BitConverter.ToUInt32(bytes, 24);
        Assert.Equal(rate * 2, BitConverter.ToUInt32(bytes, 28));
    }
}
