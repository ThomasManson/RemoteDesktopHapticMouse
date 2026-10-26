using System.Runtime.InteropServices;
using RdHaptic.Protocol;
using static RdHaptic.Agent.Native;

namespace RdHaptic.Agent;

/// <summary>
/// Server end of the RDHAPTIC dynamic virtual channel. Keeps trying to open it (the session may be on the
/// console, or the client may not have the plugin), and reopens after disconnect/reconnect.
/// </summary>
public sealed class DvcSink : IHapticSink
{
    public const ushort AgentVersion = 1;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly object _writeGate = new();
    private IntPtr _channel;
    private IntPtr _file;
    private ushort _seq;
    private TaskCompletionSource<TimeSpan>? _pendingPing;
    private uint _pendingPingStamp;
    private string _status = "starting";

    public DvcSink()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "dvc" };
        _thread.Start();
    }

    public string Status => _status;

    public void Play(Waveform waveform, byte intensityPct) => Send(Wire.Play(_seq++, waveform, intensityPct));

    public async Task<TimeSpan?> PingAsync(TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stamp = (uint)Environment.TickCount;
        lock (_writeGate)
        {
            _pendingPing = tcs;
            _pendingPingStamp = stamp;
        }
        if (!Send(Wire.Ping(_seq++, stamp)))
            return null;
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    private void Run()
    {
        string? lastError = null;
        while (!_cts.IsCancellationRequested)
        {
            if (!TryOpen(out var error))
            {
                if (error != lastError)
                    Log.Info($"Channel not available: {error} (retrying every {RetryDelay.TotalSeconds:0}s)");
                lastError = error;
                _status = $"waiting for client ({error})";
                _cts.Token.WaitHandle.WaitOne(RetryDelay);
                continue;
            }
            lastError = null;
            _status = "connected";
            Log.Info("Channel open; sending HELLO");
            Send(Wire.Hello(_seq++, AgentVersion));
            _ = LogLatencyAsync();
            ReadLoop();
            CloseChannel();
            _status = "disconnected";
            Log.Info("Channel closed");
        }
    }

    private async Task LogLatencyAsync()
    {
        var rtt = await PingAsync(TimeSpan.FromSeconds(3));
        Log.Info(rtt is { } t ? $"Round-trip latency {t.TotalMilliseconds:0} ms" : "No PONG from client");
    }

    private bool TryOpen(out string error)
    {
        error = "";
        var channel = WTSVirtualChannelOpenEx(WtsCurrentSession, Wire.ChannelName, WtsChannelOptionDynamic | WtsChannelOptionDynamicPriReal);
        if (channel == IntPtr.Zero)
        {
            error = $"open failed, err {Marshal.GetLastWin32Error()}";
            return false;
        }
        if (!WTSVirtualChannelQuery(channel, WtsVirtualFileHandle, out var buffer, out var len) || len < IntPtr.Size)
        {
            error = $"query failed, err {Marshal.GetLastWin32Error()}";
            WTSVirtualChannelClose(channel);
            return false;
        }
        var file = Marshal.ReadIntPtr(buffer);
        WTSFreeMemory(buffer);
        lock (_writeGate)
        {
            _channel = channel;
            _file = file; // owned by the channel: closed by WTSVirtualChannelClose, never CloseHandle
        }
        return true;
    }

    private void ReadLoop()
    {
        var chunk = new byte[ChannelPduHeaderSize + ChannelChunkLength];
        var message = new MemoryStream();
        var evt = CreateEvent(IntPtr.Zero, true, false, null);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!Transfer(read: true, chunk, chunk.Length, evt, out var got) || got < ChannelPduHeaderSize)
                    return;
                var total = BitConverter.ToUInt32(chunk, 0);
                var flags = BitConverter.ToUInt32(chunk, 4);
                if ((flags & ChannelFlagFirst) != 0)
                    message.SetLength(0);
                message.Write(chunk, ChannelPduHeaderSize, got - ChannelPduHeaderSize);
                if ((flags & ChannelFlagLast) != 0 || message.Length >= total)
                {
                    Handle(message.ToArray());
                    message.SetLength(0);
                }
            }
        }
        finally { CloseHandle(evt); }
    }

    private void Handle(byte[] data)
    {
        if (!Wire.TryParse(data, out var msg))
        {
            Log.Error($"Unparseable message from client ({data.Length} bytes)");
            return;
        }
        switch (msg.Type)
        {
            case MessageType.Caps:
                var names = string.Join(", ", msg.Waveforms!.Select(w => WaveformNames.Name(w.Usage)));
                Log.Info($"Client CAPS: {msg.Waveforms!.Count} waveforms [{names}], {msg.IntensityLevels} levels");
                _status = msg.Waveforms.Count > 0 ? $"connected ({msg.Waveforms.Count} waveforms)" : "connected (client has no haptic device)";
                break;
            case MessageType.Pong:
                TaskCompletionSource<TimeSpan>? tcs;
                lock (_writeGate)
                {
                    tcs = msg.TimestampMs == _pendingPingStamp ? _pendingPing : null;
                    if (tcs is not null)
                        _pendingPing = null;
                }
                tcs?.TrySetResult(TimeSpan.FromMilliseconds((uint)Environment.TickCount - msg.TimestampMs));
                break;
        }
    }

    private bool Send(byte[] message)
    {
        lock (_writeGate)
        {
            if (_file == IntPtr.Zero)
                return false;
            var evt = CreateEvent(IntPtr.Zero, true, false, null);
            try
            {
                return Transfer(read: false, message, message.Length, evt, out _);
            }
            finally { CloseHandle(evt); }
        }
    }

    private unsafe bool Transfer(bool read, byte[] buffer, int length, IntPtr evt, out int transferred)
    {
        transferred = 0;
        var file = _file;
        if (file == IntPtr.Zero)
            return false;
        var ov = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        try
        {
            ov->EventHandle = evt;
            fixed (byte* p = buffer)
            {
                var ok = read
                    ? ReadFile(file, p, length, IntPtr.Zero, ov)
                    : WriteFile(file, p, length, IntPtr.Zero, ov);
                if (!ok && Marshal.GetLastWin32Error() != ErrorIoPending)
                {
                    if (read)
                        Log.Info($"Channel read ended (err {Marshal.GetLastWin32Error()})");
                    else
                        Log.Error($"Channel write failed (err {Marshal.GetLastWin32Error()})");
                    return false;
                }
                // Wait with the buffer still pinned; cancellation closes the channel which completes the I/O.
                return GetOverlappedResult(file, ov, out transferred, true);
            }
        }
        finally { NativeMemory.Free(ov); }
    }

    private void CloseChannel()
    {
        lock (_writeGate)
        {
            if (_channel != IntPtr.Zero)
                WTSVirtualChannelClose(_channel);
            _channel = IntPtr.Zero;
            _file = IntPtr.Zero;
            _pendingPing?.TrySetCanceled();
            _pendingPing = null;
        }
    }

    public unsafe void Dispose()
    {
        _cts.Cancel();
        var file = _file;
        if (file != IntPtr.Zero)
            CancelIoEx(file, null);
        _thread.Join(TimeSpan.FromSeconds(2));
        CloseChannel();
    }
}
