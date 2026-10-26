using RdHaptic.Client;
using RdHaptic.Protocol;

namespace RdHaptic.Agent;

public interface IHapticSink : IDisposable
{
    void Play(Waveform waveform, byte intensityPct);
    Task<TimeSpan?> PingAsync(TimeSpan timeout);
    string Status { get; }
}

/// <summary>Plays straight on a local haptic mouse — for tuning the heuristics without an RDP session.</summary>
public sealed class LocalSink : IHapticSink
{
    private readonly HapticPlayer _player = new(ClientSettings.Load());

    public void Play(Waveform waveform, byte intensityPct) => _player.Enqueue(waveform, intensityPct);

    public Task<TimeSpan?> PingAsync(TimeSpan timeout) => Task.FromResult<TimeSpan?>(TimeSpan.Zero);

    public string Status => _player.Device is { } d ? $"local: {d.Product} ({d.Waveforms.Count} waveforms)" : "local: no haptic device";

    public void Dispose() => _player.Dispose();
}

/// <summary>Applies config intensity and per-waveform cooldowns, then forwards to the sink.</summary>
public sealed class HapticRouter(IHapticSink sink, AgentConfig config)
{
    private readonly Dictionary<Waveform, long> _lastFired = new();
    private readonly object _gate = new();

    public IHapticSink Sink => sink;

    public void Fire(Waveform waveform, string reason)
    {
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_lastFired.TryGetValue(waveform, out var last) && now - last < config.CooldownMs)
                return;
            _lastFired[waveform] = now;
        }
        Log.Info($"{waveform} <- {reason}");
        sink.Play(waveform, config.IntensityFor(waveform));
    }
}
