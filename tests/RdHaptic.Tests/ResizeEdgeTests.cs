using RdHaptic.Agent;
using static RdHaptic.Agent.Native;
using Edges = RdHaptic.Agent.WindowMoveHaptics.Edges;

namespace RdHaptic.Tests;

public class ResizeEdgeTests
{
    private static readonly Rect Window = new() { Left = 100, Top = 100, Right = 900, Bottom = 700 };

    private static Point P(int x, int y) => new() { X = x, Y = y };

    private static Rect R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Theory]
    [InlineData(103, 400, Edges.Left)]                 // on the left border
    [InlineData(95, 400, Edges.Left)]                  // just outside, cursor already moving out
    [InlineData(892, 400, Edges.Right)]                // just inside, cursor already moving in
    [InlineData(500, 705, Edges.Bottom)]
    [InlineData(500, 104, Edges.Top)]
    [InlineData(897, 698, Edges.Right | Edges.Bottom)] // corner
    [InlineData(500, 400, Edges.None)]                 // middle of the window
    [InlineData(500, 130, Edges.None)]                 // caption, well below the top border
    [InlineData(60, 400, Edges.None)]                  // too far outside
    public void EdgesNearCursor_FindsGrabbedBorder(int x, int y, Edges expected) =>
        Assert.Equal(expected, WindowMoveHaptics.EdgesNearCursor(Window, P(x, y), 10));

    [Fact]
    public void EdgesFromDelta_RightEdgeResize() =>
        Assert.Equal(Edges.Right, WindowMoveHaptics.EdgesFromDelta(Window, R(100, 100, 700, 700)));

    [Fact]
    public void EdgesFromDelta_TopLeftCornerResize() =>
        Assert.Equal(Edges.Top | Edges.Left, WindowMoveHaptics.EdgesFromDelta(Window, R(150, 50, 900, 700)));

    [Fact]
    public void EdgesFromDelta_MoveIsNotAResize() =>
        Assert.Equal(Edges.None, WindowMoveHaptics.EdgesFromDelta(Window, R(150, 150, 950, 750)));
}
