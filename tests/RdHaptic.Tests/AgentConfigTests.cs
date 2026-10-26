using RdHaptic.Agent;
using RdHaptic.Protocol;

namespace RdHaptic.Tests;

public class AgentConfigTests
{
    [Fact]
    public void Defaults_MatchBuiltInMapping()
    {
        var c = new AgentConfig();
        Assert.Equal(Waveform.Collide, c.WaveformFor(Trigger.TopEdgeEnter));
        Assert.Equal(Waveform.Align, c.WaveformFor(Trigger.SnapRelease));
        Assert.Equal(Waveform.Collide, c.WaveformFor(Trigger.ResizeLimit));
    }

    [Fact]
    public void Override_IsCaseInsensitive()
    {
        var c = new AgentConfig { Waveforms = new() { ["TopEdgeEnter"] = "align" } };
        Assert.Equal(Waveform.Align, c.WaveformFor(Trigger.TopEdgeEnter));
    }

    [Fact]
    public void MissingOrUnknown_FallsBackToDefault()
    {
        var c = new AgentConfig { Waveforms = new() { ["EdgeEnter"] = "bogus" } };
        Assert.Equal(Waveform.Hover, c.WaveformFor(Trigger.EdgeEnter));
        Assert.Equal(Waveform.Step, c.WaveformFor(Trigger.MonitorCross));
    }
}
