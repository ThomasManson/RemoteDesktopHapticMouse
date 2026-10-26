using RdHaptic.Agent;

namespace RdHaptic.Tests;

public class EdgeStallDetectorTests
{
    private const int CollidePx = 12;
    private const int StallMs = 250;
    private const int TickMs = 8;

    /// <summary>
    /// Simulates dragging the top edge upward at a steady speed for <paramref name="durationMs"/>. The app applies
    /// the cursor position to the window only every <paramref name="appFrameMs"/>, and the edge never goes past
    /// <paramref name="limit"/> (its min/max size). Returns the times (ms) at which Collide fired.
    /// </summary>
    private static List<long> Simulate(int durationMs, double pxPerMs, int appFrameMs, int limit = int.MinValue)
    {
        const int startY = 1000;
        var detector = new EdgeStallDetector(startY, 0);
        var fired = new List<long>();
        int edge = startY;
        for (long t = TickMs; t <= durationMs; t += TickMs)
        {
            var cursor = (int)(startY - pxPerMs * t);
            if (t % appFrameMs < TickMs)
                edge = Math.Max(cursor, limit); // app catches up, clamped by its min size
            if (detector.Update(edge, Math.Abs(cursor - edge), t, CollidePx, StallMs))
                fired.Add(t);
        }
        return fired;
    }

    [Fact]
    public void SlowApp_LaggingEdge_DoesNotFire()
    {
        // The case from the log: big window, app redraws every ~120 ms while the cursor moves 0.5 px/ms.
        Assert.Empty(Simulate(durationMs: 3000, pxPerMs: 0.5, appFrameMs: 120));
        Assert.Empty(Simulate(durationMs: 3000, pxPerMs: 0.5, appFrameMs: 200));
    }

    [Fact]
    public void FastApp_DoesNotFire()
    {
        Assert.Empty(Simulate(durationMs: 3000, pxPerMs: 1.0, appFrameMs: 16));
    }

    [Fact]
    public void HittingMinSize_FiresOnceAfterStall()
    {
        // Edge stops at y=700 (reached after 600 ms); the cursor keeps pushing for another 1.4 s.
        var fired = Simulate(durationMs: 2000, pxPerMs: 0.5, appFrameMs: 16, limit: 700);
        var single = Assert.Single(fired);
        Assert.InRange(single, 600 + StallMs, 600 + StallMs + 100);
    }

    [Fact]
    public void HittingMinSize_WithSlowApp_StillFires()
    {
        var fired = Simulate(durationMs: 2000, pxPerMs: 0.5, appFrameMs: 120, limit: 700);
        Assert.Single(fired);
    }

    [Fact]
    public void ReArms_WhenEdgeMovesAgain()
    {
        var d = new EdgeStallDetector(500, 0);
        Assert.True(d.Update(500, 30, 300, CollidePx, StallMs));   // stalled, cursor 30px past: hit
        Assert.False(d.Update(500, 60, 400, CollidePx, StallMs));  // same stall: only once
        Assert.False(d.Update(520, 5, 410, CollidePx, StallMs));   // edge moved: re-armed
        Assert.True(d.Update(520, 40, 700, CollidePx, StallMs));   // new stall, pushed past: hit again
    }

    [Fact]
    public void CursorStoppedAtEdge_DoesNotFire()
    {
        // User pauses mid-resize with the cursor slightly ahead of a laggy edge: no further push, no pulse.
        var d = new EdgeStallDetector(500, 0);
        Assert.False(d.Update(480, 14, 100, CollidePx, StallMs)); // edge moved, cursor 14px ahead
        Assert.False(d.Update(480, 14, 400, CollidePx, StallMs)); // long stall but drift didn't grow
    }
}
