using RdHaptic.Protocol;

namespace RdHaptic.Tests;

public class WireTests
{
    [Fact]
    public void Play_RoundTrips()
    {
        Assert.True(Wire.TryParse(Wire.Play(7, Waveform.Align, 80), out var m));
        Assert.Equal(MessageType.Play, m.Type);
        Assert.Equal(7, m.Seq);
        Assert.Equal(Waveform.Align, m.Waveform);
        Assert.Equal(80, m.IntensityPct);
    }

    [Fact]
    public void Play_ClampsIntensity()
    {
        Assert.True(Wire.TryParse(Wire.Play(0, Waveform.Hover, 250), out var m));
        Assert.Equal(100, m.IntensityPct);
    }

    [Fact]
    public void Caps_RoundTrips()
    {
        WaveformInfo[] waveforms = [new(0x1008, 9), new(0x1013, 9), new(0x1015, 200)];
        Assert.True(Wire.TryParse(Wire.Caps(1, waveforms, 4), out var m));
        Assert.Equal(MessageType.Caps, m.Type);
        Assert.Equal(waveforms, m.Waveforms);
        Assert.Equal(4, m.IntensityLevels);
    }

    [Fact]
    public void PingPong_CarryTimestamp()
    {
        Assert.True(Wire.TryParse(Wire.Ping(3, 0xDEADBEEF), out var ping));
        Assert.True(Wire.TryParse(Wire.Pong(3, 0xDEADBEEF), out var pong));
        Assert.Equal(MessageType.Ping, ping.Type);
        Assert.Equal(MessageType.Pong, pong.Type);
        Assert.Equal(0xDEADBEEF, pong.TimestampMs);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 1, 0x10, 0 })]               // short header
    [InlineData(new byte[] { 2, 0x10, 0, 0, 0x13, 0x10, 50 })] // wrong version
    [InlineData(new byte[] { 1, 0x10, 0, 0, 0x13 })]       // truncated PLAY
    [InlineData(new byte[] { 1, 0x02, 0, 0, 5, 1, 2 })]    // CAPS claims 5 entries
    [InlineData(new byte[] { 1, 0x77, 0, 0 })]             // unknown type
    public void Malformed_IsRejected(byte[] data) => Assert.False(Wire.TryParse(data, out _));

    [Theory]
    [InlineData("align", Waveform.Align)]
    [InlineData("HOVER", Waveform.Hover)]
    [InlineData("0x1014", Waveform.Step)]
    public void WaveformNames_Parse(string text, Waveform expected)
    {
        Assert.True(WaveformNames.TryParse(text, out var w));
        Assert.Equal(expected, w);
    }

    [Fact]
    public void WaveformNames_RejectsUnknown() => Assert.False(WaveformNames.TryParse("bogus", out _));
}
