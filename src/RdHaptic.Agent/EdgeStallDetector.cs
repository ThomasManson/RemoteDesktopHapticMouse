namespace RdHaptic.Agent;

/// <summary>
/// Decides when one dragged window edge has hit its min/max size during a live resize.
/// A hit needs all of: the cursor is more than collidePx past where it started relative to the edge, the edge has
/// not moved for stallMs, and the cursor has kept going since the edge stopped. Slow-to-redraw apps make the edge
/// lag the cursor and then catch up; requiring the stall separates that from a real limit. Fires once per stall
/// and re-arms when the edge moves again.
/// </summary>
public sealed class EdgeStallDetector(int startPos, long startTick)
{
    private int _lastPos = startPos;
    private long _lastMoveTick = startTick;
    private int _driftAtStall;
    private bool _fired;

    public bool Update(int edgePos, int drift, long tick, int collidePx, int stallMs)
    {
        if (Math.Abs(edgePos - _lastPos) > 1)
        {
            _lastPos = edgePos;
            _lastMoveTick = tick;
            _driftAtStall = drift;
            _fired = false;
            return false;
        }
        if (_fired || drift <= collidePx || tick - _lastMoveTick < stallMs || drift - _driftAtStall < collidePx / 2)
            return false;
        _fired = true;
        return true;
    }
}
