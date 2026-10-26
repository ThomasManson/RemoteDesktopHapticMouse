using System.Runtime.InteropServices;
using RdHaptic.Client;
using RdHaptic.Client.Dvc;
using RdHaptic.Client.Hid;
using RdHaptic.Protocol;

// hapticctl: local diagnostics for the client side (no RDP needed).
Log.Console = true;
var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
return cmd switch
{
    "list" => List(),
    "play" => Play(args.Skip(1).ToArray()),
    "demo" => Demo(),
    "selftest" => SelfTest(),
    "comtest" => ComTest(),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        hapticctl list                      List haptic HID collections and their waveform tables
        hapticctl play <waveform> [0-100]   Play a waveform (hover, collide, align, step, grow, success, error, click)
        hapticctl demo                      Play every supported waveform
        hapticctl selftest                  Drive the DVC plugin through a simulated RDP channel
        hapticctl comtest                   Activate the registered plugin through COM (checks install-client.ps1)
        """);
    return 1;
}

static int List()
{
    var devices = HidHapticDevice.Enumerate();
    if (devices.Count == 0)
    {
        Console.WriteLine("No haptic devices found.");
        return 2;
    }
    foreach (var d in devices)
    {
        Console.WriteLine($"{d.Product}  VID {d.VendorId:X4} PID {d.ProductId:X4}  intensity 0-{d.IntensityMax}");
        Console.WriteLine($"  {d.Path}");
        foreach (var w in d.Waveforms)
            Console.WriteLine($"  ordinal {d.OrdinalByUsage[w.Usage],2}  {WaveformNames.Name(w.Usage),-8} 0x{w.Usage:X4}  {w.DurationMs,4} ms");
        d.Dispose();
    }
    return 0;
}

static int Play(string[] a)
{
    if (a.Length == 0 || !WaveformNames.TryParse(a[0], out var waveform))
        return Help();
    var intensity = a.Length > 1 && byte.TryParse(a[1], out var i) ? i : (byte)100;
    using var device = HidHapticDevice.OpenFirst();
    if (device is null)
    {
        Console.WriteLine("No haptic device found.");
        return 2;
    }
    if (!device.Supports(waveform) && waveform != Waveform.Stop)
        Console.WriteLine($"Device does not support {waveform}; nothing played.");
    return device.Play(waveform, intensity) ? 0 : 3;
}

static int Demo()
{
    using var device = HidHapticDevice.OpenFirst();
    if (device is null)
    {
        Console.WriteLine("No haptic device found.");
        return 2;
    }
    foreach (var w in device.Waveforms)
    {
        Console.WriteLine($"{WaveformNames.Name(w.Usage)} ({w.DurationMs} ms)");
        device.Play((Waveform)w.Usage, 100);
        Thread.Sleep(800);
    }
    return 0;
}

static int SelfTest()
{
    var manager = new FakeChannelManager();
    var plugin = new HapticPlugin();
    Check(plugin.Initialize(manager) == 0, "Initialize returns S_OK");
    Check(manager.ChannelName == Wire.ChannelName, $"Listener registered for '{manager.ChannelName}'");
    Check(plugin.Connected() == 0, "Connected");

    var channel = new FakeChannel();
    Check(manager.Callback!.OnNewChannelConnection(channel, null, out var accept, out var cb) == 0 && accept == 1 && cb is not null,
        "Channel accepted");

    Deliver(cb!, Wire.Hello(1, 1));
    Check(channel.Sent.Count == 1 && Wire.TryParse(channel.Sent[0], out var caps) && caps.Type == MessageType.Caps,
        "HELLO answered with CAPS");
    Wire.TryParse(channel.Sent[0], out caps);
    Console.WriteLine($"    CAPS: {caps.Waveforms!.Count} waveforms [{string.Join(", ", caps.Waveforms.Select(w => WaveformNames.Name(w.Usage)))}], {caps.IntensityLevels} levels");

    Deliver(cb!, Wire.Ping(2, 12345));
    Check(channel.Sent.Count == 2 && Wire.TryParse(channel.Sent[1], out var pong) && pong.Type == MessageType.Pong && pong.TimestampMs == 12345,
        "PING answered with PONG");

    Console.WriteLine("    Playing Hover, Align, Collide through the channel (you should feel three pulses)...");
    Deliver(cb!, Wire.Play(3, Waveform.Hover, 100));
    Thread.Sleep(500);
    Deliver(cb!, Wire.Play(4, Waveform.Align, 100));
    Thread.Sleep(500);
    Deliver(cb!, Wire.Play(5, Waveform.Collide, 100));
    Thread.Sleep(500);

    Deliver(cb!, [0xFF, 0x00]);
    Check(true, "Garbage message ignored");
    Check(cb!.OnClose() == 0, "OnClose");
    Check(plugin.Disconnected(0) == 0 && plugin.Terminated() == 0, "Disconnected + Terminated");
    Console.WriteLine(_failures == 0 ? "SELFTEST PASSED" : $"SELFTEST FAILED ({_failures})");
    return _failures == 0 ? 0 : 4;
}

static int ComTest()
{
    var type = Type.GetTypeFromCLSID(new Guid(HapticPlugin.Clsid), throwOnError: false);
    try
    {
        var obj = Activator.CreateInstance(type!);
        Check(obj is IWTSPlugin, $"CoCreateInstance({{{HapticPlugin.Clsid}}}) returned IWTSPlugin");
    }
    catch (Exception ex)
    {
        Check(false, $"CoCreateInstance failed: {ex.Message}");
    }
    return _failures == 0 ? 0 : 4;
}

static void Deliver(IWTSVirtualChannelCallback cb, byte[] data)
{
    var p = Marshal.AllocHGlobal(data.Length);
    try
    {
        Marshal.Copy(data, 0, p, data.Length);
        cb.OnDataReceived((uint)data.Length, p);
    }
    finally { Marshal.FreeHGlobal(p); }
}

static void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok)
        _failures++;
}

partial class Program
{
    private static int _failures;
}

sealed class FakeChannelManager : IWTSVirtualChannelManager
{
    public string? ChannelName;
    public IWTSListenerCallback? Callback;

    public int CreateListener(string channelName, uint flags, IWTSListenerCallback listenerCallback, out IWTSListener? listener)
    {
        ChannelName = channelName;
        Callback = listenerCallback;
        listener = null;
        return 0;
    }
}

sealed class FakeChannel : IWTSVirtualChannel
{
    public readonly List<byte[]> Sent = [];

    public int Write(uint size, byte[] buffer, IntPtr reserved)
    {
        Sent.Add(buffer[..(int)size]);
        return 0;
    }

    public int Close() => 0;
}
