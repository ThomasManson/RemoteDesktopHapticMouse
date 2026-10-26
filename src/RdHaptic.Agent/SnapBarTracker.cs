using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using RdHaptic.Protocol;
using static RdHaptic.Agent.Native;

namespace RdHaptic.Agent;

/// <summary>
/// Pulses as the cursor moves between zones of the Windows 11 Snap Layouts bar that drops down while a window is
/// dragged to the top of the screen. The bar is a shell XAML island, so its zones are found with UI Automation and
/// cached; hit-testing the cursor against the cached rectangles keeps the per-tick cost tiny.
/// Runs on its own MTA thread so UIA's cross-process calls never stall the WinEvent/timer thread.
/// </summary>
public sealed class SnapBarTracker : IDisposable
{
    private const int TickMs = 15;
    private const int RefreshMs = 300; // the bar animates in; re-read zone positions while it is up
    private static readonly string[] BarClassHints = ["XamlExplorerHostIslandWindow", "Snap"];

    private readonly HapticRouter _router;
    private readonly AgentConfig _config;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly HashSet<string> _loggedBars = [];
    private volatile IntPtr _dragged;
    private volatile bool _disposed;

    private sealed record Zone(Native.Rect Bounds, string Label);

    public SnapBarTracker(HapticRouter router, AgentConfig config)
    {
        _router = router;
        _config = config;
        _thread = new Thread(Run) { IsBackground = true, Name = "snapbar" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void DragStarted(IntPtr hwnd)
    {
        _dragged = hwnd;
        _wake.Set();
    }

    public void DragEnded() => _dragged = IntPtr.Zero;

    private void Run()
    {
        while (!_disposed)
        {
            _wake.WaitOne();
            try { TrackDrag(); }
            catch (Exception ex) { Log.Error("Snap bar tracking failed", ex); }
        }
    }

    private void TrackDrag()
    {
        IntPtr bar = IntPtr.Zero;
        List<Zone> zones = [];
        long loadedAt = 0;
        Zone? current = null;

        while (_dragged != IntPtr.Zero && !_disposed)
        {
            Thread.Sleep(TickMs);
            if (!_config.SnapBarHover)
                continue;

            var found = FindBar(_dragged);
            if (found == IntPtr.Zero)
            {
                bar = IntPtr.Zero;
                current = null;
                continue;
            }
            if (found != bar || Environment.TickCount64 - loadedAt > RefreshMs)
            {
                bar = found;
                zones = LoadZones(bar);
                loadedAt = Environment.TickCount64;
            }

            GetCursorPos(out var p);
            var hit = zones
                .Where(z => p.X >= z.Bounds.Left && p.X < z.Bounds.Right && p.Y >= z.Bounds.Top && p.Y < z.Bounds.Bottom)
                .MinBy(z => (long)z.Bounds.Width * z.Bounds.Height);
            if (!Equals(hit?.Bounds, current?.Bounds))
            {
                current = hit;
                if (hit is not null)
                    _router.Fire(_config.WaveformFor(Trigger.SnapBarZone), $"Snap Layouts zone '{hit.Label}'");
            }
        }
    }

    /// <summary>Visible shell flyout window (not the dragged window) that looks like the Snap Layouts bar.</summary>
    private IntPtr FindBar(IntPtr dragged)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (h == dragged || !IsWindowVisible(h))
                return true;
            var cls = ClassName(h);
            if (!BarClassHints.Any(hint => cls.Contains(hint, StringComparison.OrdinalIgnoreCase)))
                return true;
            GetWindowRect(h, out var r);
            if (r.Width <= 1 || r.Height <= 1)
                return true;
            LogBarOnce(h, cls, r);
            result = h;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static List<Zone> LoadZones(IntPtr bar)
    {
        var zones = new List<Zone>();
        try
        {
            var root = AutomationElement.FromHandle(bar);
            CollectLeaves(TreeWalker.ControlViewWalker, root, zones, depth: 0);
        }
        catch (ElementNotAvailableException) { }
        catch (COMException) { }
        return zones;
    }

    private static void CollectLeaves(TreeWalker walker, AutomationElement element, List<Zone> zones, int depth)
    {
        if (depth > 12 || zones.Count > 200)
            return;
        var child = walker.GetFirstChild(element);
        if (child is null)
        {
            var b = element.Current.BoundingRectangle;
            if (!b.IsEmpty && b.Width >= 4 && b.Height >= 4)
            {
                var label = element.Current.Name;
                if (string.IsNullOrEmpty(label))
                    label = element.Current.AutomationId;
                zones.Add(new Zone(new Native.Rect { Left = (int)b.Left, Top = (int)b.Top, Right = (int)b.Right, Bottom = (int)b.Bottom },
                    $"{label} [{element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")}]"));
            }
            return;
        }
        for (; child is not null; child = walker.GetNextSibling(child))
            CollectLeaves(walker, child, zones, depth + 1);
    }

    /// <summary>Logs each distinct bar window once with its zones, so mismatches can be diagnosed from agent.log.</summary>
    private void LogBarOnce(IntPtr h, string cls, Native.Rect r)
    {
        GetWindowThreadProcessId(h, out var pid);
        string process;
        try { process = Process.GetProcessById((int)pid).ProcessName; } catch { process = pid.ToString(); }
        if (!_loggedBars.Add($"{cls}|{process}|{WindowText(h)}"))
            return;
        var zones = LoadZones(h);
        Log.Info($"Snap bar candidate: class={cls} title='{WindowText(h)}' process={process} rect={r} zones={zones.Count}");
        foreach (var z in zones.Take(40))
            Log.Info($"    zone {z.Bounds} {z.Label}");
    }

    private static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string WindowText(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        _disposed = true;
        _dragged = IntPtr.Zero;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
