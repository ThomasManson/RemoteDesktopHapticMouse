using System.Runtime.InteropServices;
using RdHaptic.Protocol;

namespace RdHaptic.Client.Dvc;

/// <summary>
/// The DVC plugin mstsc.exe instantiates (via RdHaptic.Client.comhost.dll) when it is registered under
/// HKCU\Software\Microsoft\Terminal Server Client\Default\AddIns\RdHaptic with Name = this CLSID.
/// </summary>
[ComVisible(true)]
[Guid(Clsid)]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("RdHaptic.Plugin")]
public sealed class HapticPlugin : IWTSPlugin
{
    public const string Clsid = "874bf2d7-62ec-4d0b-be18-59fd27596d37";

    private HapticPlayer? _player;
    private IWTSListener? _listener;

    public int Initialize(IWTSVirtualChannelManager channelManager)
    {
        try
        {
            Log.Init("client");
            Log.Info($"Plugin loaded in {Environment.ProcessPath} (pid {Environment.ProcessId})");
            _player = new HapticPlayer(ClientSettings.Load());
            var hr = channelManager.CreateListener(Wire.ChannelName, 0, new ListenerCallback(_player), out _listener);
            Log.Info($"CreateListener({Wire.ChannelName}) hr=0x{hr:X8}");
            return hr;
        }
        catch (Exception ex)
        {
            Log.Error("Initialize failed", ex);
            return ex.HResult;
        }
    }

    public int Connected()
    {
        Log.Info("RDP connected");
        return 0;
    }

    public int Disconnected(uint disconnectCode)
    {
        Log.Info($"RDP disconnected (code {disconnectCode})");
        return 0;
    }

    public int Terminated()
    {
        Log.Info("Plugin terminated");
        _player?.Dispose();
        _player = null;
        _listener = null;
        return 0;
    }
}

public sealed class ListenerCallback(HapticPlayer player) : IWTSListenerCallback
{
    public int OnNewChannelConnection(IWTSVirtualChannel channel, string? data, out int accept, out IWTSVirtualChannelCallback? callback)
    {
        Log.Info("Agent opened channel");
        accept = 1;
        callback = new ChannelCallback(channel, player);
        return 0;
    }
}

public sealed class ChannelCallback(IWTSVirtualChannel channel, HapticPlayer player) : IWTSVirtualChannelCallback
{
    private IWTSVirtualChannel? _channel = channel;
    private ushort _seq;

    public int OnDataReceived(uint size, IntPtr buffer)
    {
        try
        {
            var data = new byte[size];
            Marshal.Copy(buffer, data, 0, (int)size);
            if (!Wire.TryParse(data, out var msg))
            {
                Log.Error($"Unparseable message ({size} bytes)");
                return 0;
            }
            switch (msg.Type)
            {
                case MessageType.Hello:
                    Log.Info($"HELLO from agent v{msg.AgentVersion}");
                    var device = player.Device;
                    Send(Wire.Caps(_seq++, device?.Waveforms ?? [], (byte)(device?.IntensityMax ?? 0)));
                    break;
                case MessageType.Play:
                    player.Enqueue(msg.Waveform, msg.IntensityPct);
                    break;
                case MessageType.Stop:
                    player.Enqueue(Waveform.Stop, 0);
                    break;
                case MessageType.Ping:
                    // Write must happen on the callback thread: the channel RCW is not safe to use from our worker.
                    Send(Wire.Pong(msg.Seq, msg.TimestampMs));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("OnDataReceived failed", ex);
        }
        return 0;
    }

    public int OnClose()
    {
        Log.Info("Channel closed");
        if (_channel is not null && Marshal.IsComObject(_channel))
            Marshal.ReleaseComObject(_channel);
        _channel = null;
        return 0;
    }

    private void Send(byte[] message)
    {
        var hr = _channel?.Write((uint)message.Length, message, IntPtr.Zero) ?? 0;
        if (hr < 0)
            Log.Error($"Channel write failed hr=0x{hr:X8}");
    }
}
