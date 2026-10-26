using RdHaptic.Protocol;
using static RdHaptic.Agent.Native;

namespace RdHaptic.Agent;

/// <summary>
/// Recreates Windows 11's window-management haptics from the outside:
/// <list type="bullet">
/// <item>Hover when a dragged window's cursor enters a snap zone (edge, corner, top), mirroring the snap preview.</item>
/// <item>Align when the window is released into a snap zone, or otherwise ends up snapped (e.g. via the Snap Layouts bar).</item>
/// <item>Collide when a resize hits the window's minimum/maximum size and the cursor runs ahead of the edge.</item>
/// <item>Step (opt-in) when the drag crosses onto another monitor.</item>
/// </list>
/// Runs its own thread with a message loop: out-of-context WinEvent hooks and timers are delivered through it.
/// </summary>
public sealed class WindowMoveHaptics : IDisposable
{
    private const int HtCaption = 2;
    private const int HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13, HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;
    private const uint WmQuit = 0x0012;
    private const int SnapSettleMs = 350;
    private const int SnapTolerancePx = 6;
    private const int ResizeBorderGuessPx = 10;

    private readonly HapticRouter _router;
    private readonly AgentConfig _config;
    private readonly Thread _thread;
    private readonly WinEventProc _winEventProc;
    private readonly TimerProc _pollProc;
    private readonly TimerProc _settleProc;
    private readonly ManualResetEventSlim _started = new();
    private uint _threadId;
    private IntPtr _hook;
    private UIntPtr _pollTimer;
    private UIntPtr _settleTimer;
    private Drag? _drag;
    private (IntPtr Hwnd, bool WasSnapped)? _settling;

    private enum Kind { Unknown, Move, Size }

    [Flags]
    public enum Edges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    /// <summary>Snap zones, named after where Windows would place the window.</summary>
    public enum Zone { None, Left, Right, Top, TopLeft, TopRight, BottomLeft, BottomRight, VerticalMax }

    private sealed class Drag
    {
        public required IntPtr Hwnd;
        public required Kind Kind;
        public required Edges SizeEdges;
        public required Rect StartRect;
        public required Point StartCursor;
        public required bool WasSnapped;
        public Zone Zone;
        public IntPtr Monitor;
        public readonly Dictionary<Edges, EdgeStallDetector> EdgeDetectors = new();
    }

    private readonly SnapBarTracker _snapBar;

    public WindowMoveHaptics(HapticRouter router, AgentConfig config)
    {
        _router = router;
        _config = config;
        _snapBar = new SnapBarTracker(router, config);
        _winEventProc = OnWinEvent;
        _pollProc = (_, _, _, _) => Poll();
        _settleProc = (_, _, _, _) => Settle();
        _thread = new Thread(Run) { IsBackground = true, Name = "winevents" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Wait();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWinEventHook(EventSystemMoveSizeStart, EventSystemMoveSizeEnd, IntPtr.Zero, _winEventProc, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        Log.Info(_hook != IntPtr.Zero ? "Window move/size hook installed" : "SetWinEventHook failed");
        _started.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        if (_hook != IntPtr.Zero)
            UnhookWinEvent(_hook);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != ObjIdWindow || hwnd == IntPtr.Zero)
            return;
        try
        {
            if (eventType == EventSystemMoveSizeStart)
                Begin(hwnd);
            else if (eventType == EventSystemMoveSizeEnd)
                End(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("Window event handling failed", ex);
        }
    }

    private void Begin(IntPtr hwnd)
    {
        GetCursorPos(out var cursor);
        var rect = WindowRect(hwnd);
        var (kind, edges) = Classify(hwnd, cursor);
        var how = "hit-test";
        if (kind == Kind.Unknown)
        {
            // By the time the event arrives (especially over RDP) the cursor has often left the thin resize border,
            // so the hit-test says HTNOWHERE/HTCLIENT. Fall back to where the cursor sits relative to the frame.
            edges = EdgesNearCursor(rect, cursor, ResizeBorderGuessPx);
            kind = edges != Edges.None ? Kind.Size : Kind.Unknown;
            how = edges != Edges.None ? "cursor position" : "undetermined";
        }
        _drag = new Drag
        {
            Hwnd = hwnd,
            Kind = kind,
            SizeEdges = edges,
            StartRect = rect,
            StartCursor = cursor,
            WasSnapped = IsSnapped(hwnd),
            Monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest),
        };
        Log.Info($"Drag start 0x{hwnd:X} {kind} {edges} (by {how}) rect {rect} cursor ({cursor.X},{cursor.Y}) " +
                 $"snapped={_drag.WasSnapped} liveResize={SystemFlag(SpiGetDragFullWindows)}");
        if (kind != Kind.Size)
            _snapBar.DragStarted(hwnd);
        CancelSettle();
        if (_pollTimer != UIntPtr.Zero)
            KillTimer(IntPtr.Zero, _pollTimer); // missed MOVESIZEEND for a previous window
        _pollTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, (uint)Math.Max(1, _config.PollMs), _pollProc);
    }

    private void End(IntPtr hwnd)
    {
        if (_pollTimer != UIntPtr.Zero)
            KillTimer(IntPtr.Zero, _pollTimer);
        _pollTimer = UIntPtr.Zero;
        _snapBar.DragEnded();
        var drag = _drag;
        _drag = null;
        if (drag is null || drag.Hwnd != hwnd)
        {
            Log.Info($"Drag end 0x{hwnd:X} without matching start");
            return;
        }

        Poll(drag); // catch a zone entered between the last tick and release
        Log.Info($"Drag end 0x{hwnd:X} {drag.Kind} {drag.SizeEdges} zone={drag.Zone} rect {WindowRect(hwnd)}");
        if (drag.Kind == Kind.Size && _config.ResizeCollide && !SystemFlag(SpiGetDragFullWindows))
        {
            // Outline resizing: the window only changes on release, so the best we can do is compare the final
            // edge with where the cursor ended up. The outline itself stops at the min/max size.
            GetCursorPos(out var cursor);
            if (drag.SizeEdges == Edges.None)
                drag.SizeEdges = EdgesFromDelta(drag.StartRect, WindowRect(hwnd));
            CheckCollide(drag, cursor, atRelease: true);
        }
        if (drag.Zone != Zone.None)
        {
            if (_config.SnapAlign)
                _router.Fire(_config.WaveformFor(Trigger.SnapRelease), $"released in {drag.Zone} zone");
            return;
        }
        // Not released in an edge zone; it may still snap (Snap Layouts bar, keyboard). Check once it settles.
        if (_config.SnapAlign && drag.Kind != Kind.Size && !drag.WasSnapped)
        {
            _settling = (hwnd, drag.WasSnapped);
            _settleTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, SnapSettleMs, _settleProc);
        }
    }

    private void Settle()
    {
        var settling = _settling;
        CancelSettle();
        if (settling is { } s && !s.WasSnapped && IsSnapped(s.Hwnd))
            _router.Fire(_config.WaveformFor(Trigger.SnapRelease), "window snapped after drop");
    }

    private void CancelSettle()
    {
        if (_settleTimer != UIntPtr.Zero)
            KillTimer(IntPtr.Zero, _settleTimer);
        _settleTimer = UIntPtr.Zero;
        _settling = null;
    }

    private void Poll()
    {
        if (_drag is { } drag)
        {
            try { Poll(drag); }
            catch (Exception ex) { Log.Error("Poll failed", ex); }
        }
    }

    private void Poll(Drag drag)
    {
        GetCursorPos(out var cursor);

        // Correct the start-time guess from geometry: a size change means a resize, and the edges that moved are
        // the ones being dragged.
        var r = WindowRect(drag.Hwnd);
        if (r.Width != drag.StartRect.Width || r.Height != drag.StartRect.Height)
        {
            if (drag.Kind != Kind.Size)
            {
                drag.Kind = Kind.Size;
                _snapBar.DragEnded();
            }
            if (drag.SizeEdges == Edges.None)
            {
                drag.SizeEdges = EdgesFromDelta(drag.StartRect, r);
                Log.Info($"Resize edges inferred from geometry: {drag.SizeEdges}");
            }
        }
        else if (r.Left != drag.StartRect.Left || r.Top != drag.StartRect.Top)
        {
            // Same size, new position: a move (also undoes a cursor-position guess that wrongly said "resize").
            if (drag.Kind != Kind.Move)
            {
                drag.Kind = Kind.Move;
                drag.SizeEdges = Edges.None;
                _snapBar.DragStarted(drag.Hwnd);
            }
        }
        else if (drag.Kind == Kind.Unknown && (cursor.X != drag.StartCursor.X || cursor.Y != drag.StartCursor.Y))
        {
            drag.Kind = Kind.Move;
        }

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        if (monitor != drag.Monitor)
        {
            drag.Monitor = monitor;
            if (_config.MonitorCross && drag.Kind == Kind.Move)
                _router.Fire(_config.WaveformFor(Trigger.MonitorCross), "crossed to another monitor");
        }

        var zone = drag.Kind switch
        {
            Kind.Move when SystemFlag(SpiGetWinArranging) => SnapZone(cursor),
            Kind.Size when (drag.SizeEdges is Edges.Top or Edges.Bottom) && SystemFlag(SpiGetWinArranging) => VerticalMaxZone(cursor, drag.SizeEdges),
            _ => Zone.None,
        };
        if (zone != drag.Zone)
        {
            drag.Zone = zone;
            if (zone != Zone.None && _config.EdgeHover)
                _router.Fire(_config.WaveformFor(zone is Zone.Top or Zone.VerticalMax ? Trigger.TopEdgeEnter : Trigger.EdgeEnter), $"entered {zone} zone");
        }

        if (drag.Kind == Kind.Size && _config.ResizeCollide && drag.SizeEdges != Edges.None && SystemFlag(SpiGetDragFullWindows))
            CheckCollide(drag, cursor);
    }

    /// <summary>
    /// Fires Collide when a dragged edge has hit the window's min/max size. During a live resize, slow apps let the
    /// edge lag the cursor, so "cursor ahead of the edge" alone is not enough: the edge must also have stopped
    /// (see <see cref="EdgeStallDetector"/>). For outline resizing (<paramref name="atRelease"/>) only the final
    /// overshoot is available.
    /// </summary>
    private void CheckCollide(Drag drag, Point cursor, bool atRelease = false)
    {
        var rect = WindowRect(drag.Hwnd);
        var tick = Environment.TickCount64;
        foreach (var edge in new[] { Edges.Left, Edges.Top, Edges.Right, Edges.Bottom })
        {
            if (!drag.SizeEdges.HasFlag(edge))
                continue;
            var (pos, now, start) = edge switch
            {
                Edges.Left => (rect.Left, cursor.X - rect.Left, drag.StartCursor.X - drag.StartRect.Left),
                Edges.Right => (rect.Right, cursor.X - rect.Right, drag.StartCursor.X - drag.StartRect.Right),
                Edges.Top => (rect.Top, cursor.Y - rect.Top, drag.StartCursor.Y - drag.StartRect.Top),
                _ => (rect.Bottom, cursor.Y - rect.Bottom, drag.StartCursor.Y - drag.StartRect.Bottom),
            };
            var drift = Math.Abs(now - start);
            bool hit;
            if (atRelease)
            {
                hit = drift > _config.CollidePx;
            }
            else
            {
                if (!drag.EdgeDetectors.TryGetValue(edge, out var detector))
                    drag.EdgeDetectors[edge] = detector = new EdgeStallDetector(pos, tick);
                hit = detector.Update(pos, drift, tick, _config.CollidePx, _config.CollideStallMs);
            }
            if (hit)
                _router.Fire(_config.WaveformFor(Trigger.ResizeLimit), $"resize hit limit on {edge} edge (cursor {drift}px past it)");
        }
    }

    public Zone SnapZone(Point p)
    {
        var mon = MonitorAt(p).Monitor;
        int edge = _config.EdgePx, corner = _config.CornerPx;
        bool left = p.X <= mon.Left + edge && !MonitorExists(mon.Left - 1, p.Y);
        bool right = p.X >= mon.Right - 1 - edge && !MonitorExists(mon.Right, p.Y);
        bool top = p.Y <= mon.Top + edge && !MonitorExists(p.X, mon.Top - 1);
        bool bottom = p.Y >= mon.Bottom - 1 - edge && !MonitorExists(p.X, mon.Bottom);
        bool nearTop = p.Y < mon.Top + corner, nearBottom = p.Y >= mon.Bottom - corner;
        bool nearLeft = p.X < mon.Left + corner, nearRight = p.X >= mon.Right - corner;

        if (left)
            return nearTop ? Zone.TopLeft : nearBottom ? Zone.BottomLeft : Zone.Left;
        if (right)
            return nearTop ? Zone.TopRight : nearBottom ? Zone.BottomRight : Zone.Right;
        if (top)
            return nearLeft ? Zone.TopLeft : nearRight ? Zone.TopRight : Zone.Top;
        if (bottom)
            return nearLeft ? Zone.BottomLeft : nearRight ? Zone.BottomRight : Zone.None;
        return Zone.None;
    }

    private Zone VerticalMaxZone(Point p, Edges edges)
    {
        var mon = MonitorAt(p).Monitor;
        return edges == Edges.Top && p.Y <= mon.Top + _config.EdgePx && !MonitorExists(p.X, mon.Top - 1)
            || edges == Edges.Bottom && p.Y >= mon.Bottom - 1 - _config.EdgePx && !MonitorExists(p.X, mon.Bottom)
            ? Zone.VerticalMax
            : Zone.None;
    }

    /// <summary>
    /// Edges of the window rect (which includes the invisible resize borders) the cursor is within
    /// <paramref name="tolerance"/> px of. Points well outside the rect match nothing.
    /// </summary>
    public static Edges EdgesNearCursor(Rect rect, Point p, int tolerance)
    {
        if (p.X < rect.Left - tolerance || p.X > rect.Right + tolerance || p.Y < rect.Top - tolerance || p.Y > rect.Bottom + tolerance)
            return Edges.None;
        var edges = Edges.None;
        if (Math.Abs(p.X - rect.Left) <= tolerance) edges |= Edges.Left;
        else if (Math.Abs(p.X - rect.Right) <= tolerance) edges |= Edges.Right;
        if (Math.Abs(p.Y - rect.Top) <= tolerance) edges |= Edges.Top;
        else if (Math.Abs(p.Y - rect.Bottom) <= tolerance) edges |= Edges.Bottom;
        return edges;
    }

    /// <summary>Which edges moved between two rects (the ones being dragged in a resize).</summary>
    public static Edges EdgesFromDelta(Rect start, Rect now)
    {
        var edges = Edges.None;
        if (now.Left != start.Left) edges |= Edges.Left;
        if (now.Right != start.Right) edges |= Edges.Right;
        if (now.Top != start.Top) edges |= Edges.Top;
        if (now.Bottom != start.Bottom) edges |= Edges.Bottom;
        // Both sides moving means the window moved, not resized along that axis.
        if ((edges & (Edges.Left | Edges.Right)) == (Edges.Left | Edges.Right)) edges &= ~(Edges.Left | Edges.Right);
        if ((edges & (Edges.Top | Edges.Bottom)) == (Edges.Top | Edges.Bottom)) edges &= ~(Edges.Top | Edges.Bottom);
        return edges;
    }

    private static bool MonitorExists(int x, int y) => MonitorFromPoint(new Point { X = x, Y = y }, MonitorDefaultToNull) != IntPtr.Zero;

    private static (Kind, Edges) Classify(IntPtr hwnd, Point cursor)
    {
        var lparam = (IntPtr)(((cursor.Y & 0xFFFF) << 16) | (cursor.X & 0xFFFF));
        if (SendMessageTimeout(hwnd, WmNcHitTest, UIntPtr.Zero, lparam, SmtoAbortIfHung, 50, out var result) == IntPtr.Zero)
            return (Kind.Unknown, Edges.None);
        return (int)result switch
        {
            HtCaption => (Kind.Move, Edges.None),
            HtLeft => (Kind.Size, Edges.Left),
            HtRight => (Kind.Size, Edges.Right),
            HtTop => (Kind.Size, Edges.Top),
            HtBottom => (Kind.Size, Edges.Bottom),
            HtTopLeft => (Kind.Size, Edges.Top | Edges.Left),
            HtTopRight => (Kind.Size, Edges.Top | Edges.Right),
            HtBottomLeft => (Kind.Size, Edges.Bottom | Edges.Left),
            HtBottomRight => (Kind.Size, Edges.Bottom | Edges.Right),
            _ => (Kind.Unknown, Edges.None),
        };
    }

    private static Rect WindowRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return r;
    }

    /// <summary>True if the window is maximized or its visible frame sits on a snap grid of its monitor's work area.</summary>
    public static bool IsSnapped(IntPtr hwnd)
    {
        if (IsZoomed(hwnd))
            return true;
        var frame = FrameBounds(hwnd);
        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info);
        return IsSnapRect(frame, info.Work);
    }

    private static readonly double[] SnapFractions = [0, 1 / 4.0, 1 / 3.0, 1 / 2.0, 2 / 3.0, 3 / 4.0, 1];

    public static bool IsSnapRect(Rect frame, Rect work)
    {
        bool OnGrid(int value, int start, int length) =>
            SnapFractions.Any(f => Math.Abs(value - (start + f * length)) <= SnapTolerancePx);

        bool full = Math.Abs(frame.Left - work.Left) <= SnapTolerancePx && Math.Abs(frame.Right - work.Right) <= SnapTolerancePx
                    && Math.Abs(frame.Top - work.Top) <= SnapTolerancePx && Math.Abs(frame.Bottom - work.Bottom) <= SnapTolerancePx;
        bool touchesH = Math.Abs(frame.Left - work.Left) <= SnapTolerancePx || Math.Abs(frame.Right - work.Right) <= SnapTolerancePx;
        bool touchesV = Math.Abs(frame.Top - work.Top) <= SnapTolerancePx || Math.Abs(frame.Bottom - work.Bottom) <= SnapTolerancePx;
        return !full && touchesH && touchesV
            && OnGrid(frame.Left, work.Left, work.Width) && OnGrid(frame.Right, work.Left, work.Width)
            && OnGrid(frame.Top, work.Top, work.Height) && OnGrid(frame.Bottom, work.Top, work.Height);
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _snapBar.Dispose();
    }
}
