using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using RdHaptic.Protocol;

namespace RdHaptic.Agent;

/// <summary>
/// Local API on \\.\pipe\RdHaptic (current user only). One line per connection, one line of reply:
///   play &lt;waveform&gt; [0-100]   |   ping   |   status
/// </summary>
public sealed class PipeServer(HapticRouter router) : IDisposable
{
    public const string PipeName = "RdHaptic";
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
                await writer.WriteLineAsync(await HandleAsync(line ?? ""));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log.Error("Pipe request failed", ex);
            }
        }
    }

    private async Task<string> HandleAsync(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.FirstOrDefault()?.ToLowerInvariant())
        {
            case "play" when parts.Length >= 2 && WaveformNames.TryParse(parts[1], out var w):
                var pct = parts.Length >= 3 && byte.TryParse(parts[2], out var p) ? Math.Min(p, (byte)100) : (byte)100;
                Log.Info($"{w} <- pipe");
                router.Sink.Play(w, pct);
                return "ok";
            case "ping":
                var rtt = await router.Sink.PingAsync(TimeSpan.FromSeconds(3));
                return rtt is { } t ? $"ok {t.TotalMilliseconds:0} ms" : "error: no reply from client";
            case "status":
                return $"ok {router.Sink.Status}";
            default:
                return "error: expected 'play <waveform> [0-100]', 'ping' or 'status'";
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
    }
}
