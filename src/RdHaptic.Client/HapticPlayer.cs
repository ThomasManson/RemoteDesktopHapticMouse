using System.Threading.Channels;
using RdHaptic.Client.Hid;
using RdHaptic.Protocol;

namespace RdHaptic.Client;

/// <summary>
/// Owns the local haptic device and plays requests on a dedicated worker so callers (the RDP client's channel
/// thread) never block on HID I/O. Reopens the device lazily when the Bluetooth mouse drops and comes back.
/// </summary>
public sealed class HapticPlayer : IDisposable
{
    private static readonly TimeSpan ReopenThrottle = TimeSpan.FromSeconds(2);
    private const int MaxGapMs = 40;

    private static readonly Dictionary<Waveform, Waveform[]> Fallbacks = new()
    {
        [Waveform.Align] = [Waveform.Click, Waveform.Step, Waveform.Hover],
        [Waveform.Hover] = [Waveform.Align, Waveform.Click],
        [Waveform.Collide] = [Waveform.Step, Waveform.Click],
        [Waveform.Step] = [Waveform.Click, Waveform.Align],
        [Waveform.Click] = [Waveform.Step, Waveform.Align],
        [Waveform.Press] = [Waveform.Click, Waveform.Step],
        [Waveform.Release] = [Waveform.Click, Waveform.Step],
        [Waveform.Success] = [Waveform.Step, Waveform.Click],
        [Waveform.Error] = [Waveform.Collide, Waveform.Step],
        [Waveform.Grow] = [Waveform.Step, Waveform.Click],
    };

    private readonly Channel<(Waveform Waveform, byte Intensity)> _queue =
        Channel.CreateBounded<(Waveform, byte)>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly object _deviceGate = new();
    private readonly Task _worker;
    private HidHapticDevice? _device;
    private DateTime _lastOpenAttempt = DateTime.MinValue;

    public ClientSettings Settings { get; }

    public HapticPlayer(ClientSettings settings)
    {
        Settings = settings;
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Current device, opened on demand. Null when no haptic mouse is present.</summary>
    public HidHapticDevice? Device
    {
        get
        {
            lock (_deviceGate)
            {
                if (_device is null && DateTime.UtcNow - _lastOpenAttempt > ReopenThrottle)
                {
                    _lastOpenAttempt = DateTime.UtcNow;
                    _device = HidHapticDevice.OpenFirst();
                    if (_device is not null)
                        Log.Info($"Haptic device: {_device.Product} {_device.VendorId:X4}:{_device.ProductId:X4}, {_device.Waveforms.Count} waveforms, intensity 0-{_device.IntensityMax}");
                    else
                        Log.Info("No haptic device found");
                }
                return _device;
            }
        }
    }

    public void Enqueue(Waveform waveform, byte intensityPct)
    {
        if (Settings.Enabled && !Settings.IsMuted(waveform))
            _queue.Writer.TryWrite((waveform, intensityPct));
    }

    private async Task RunAsync()
    {
        await foreach (var (requested, intensity) in _queue.Reader.ReadAllAsync())
        {
            try
            {
                var device = Device;
                if (device is null)
                    continue;
                var waveform = Resolve(device, requested);
                if (waveform is null)
                    continue;
                var scaled = (byte)Math.Clamp(intensity * Settings.IntensityScalePct / 100, 0, 100);
                if (!device.Play(waveform.Value, scaled))
                {
                    InvalidateDevice(device);
                    device = Device;
                    if (device is null || !device.Play(waveform.Value, scaled))
                        continue;
                }
                // Keep back-to-back pulses distinguishable without letting a queue build up.
                await Task.Delay(Math.Min(device.DurationMs(waveform.Value), (ushort)MaxGapMs));
            }
            catch (Exception ex)
            {
                Log.Error("Haptic playback failed", ex);
            }
        }
    }

    private static Waveform? Resolve(HidHapticDevice device, Waveform requested)
    {
        if (requested == Waveform.Stop || device.Supports(requested))
            return requested;
        if (Fallbacks.TryGetValue(requested, out var chain))
            foreach (var alt in chain)
                if (device.Supports(alt))
                    return alt;
        return null;
    }

    private void InvalidateDevice(HidHapticDevice device)
    {
        lock (_deviceGate)
        {
            if (!ReferenceEquals(_device, device))
                return;
            Log.Info("Haptic device lost; will reopen");
            _device.Dispose();
            _device = null;
            _lastOpenAttempt = DateTime.MinValue;
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _worker.Wait(TimeSpan.FromSeconds(1));
        lock (_deviceGate)
        {
            _device?.Dispose();
            _device = null;
        }
    }
}
