using RdHaptic.Agent;
using RdHaptic.Protocol;

// RdHapticAgent [--local] [--console]
//   (default)   send haptics over the RDHAPTIC channel to the RDP client
//   --local     play on this machine's haptic mouse instead (tune heuristics without RDP)
//   --console   show a console window with the log
// Physical pixels everywhere, so cursor, monitor and window rects agree on scaled displays.
Native.SetProcessDpiAwarenessContext(Native.DpiAwarenessContextPerMonitorAwareV2);
var local = args.Contains("--local", StringComparer.OrdinalIgnoreCase);
if (args.Contains("--console", StringComparer.OrdinalIgnoreCase))
{
    if (!Native.AttachConsole(-1))
        Native.AllocConsole();
    Log.Console = true;
}

Log.Init("agent");
using var instance = new Mutex(true, @"Local\RdHapticAgent", out var first);
if (!first)
{
    Log.Info("Agent already running in this session; exiting");
    return 1;
}

var config = AgentConfig.Load();
using var configWatch = config.Watch();
Log.Info($"Agent starting ({(local ? "local mouse" : "RDP channel")}); config {AgentConfig.FilePath}");

using IHapticSink sink = local ? new LocalSink() : new DvcSink();
var router = new HapticRouter(sink, config);
using var windows = new WindowMoveHaptics(router, config);
using var pipe = new PipeServer(router);
pipe.Start();

var exit = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Set(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => exit.Set();
exit.Wait();
Log.Info("Agent stopping");
return 0;
