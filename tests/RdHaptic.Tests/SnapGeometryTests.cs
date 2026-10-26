using RdHaptic.Agent;
using static RdHaptic.Agent.Native;

namespace RdHaptic.Tests;

public class SnapGeometryTests
{
    // 1920x1200 monitor with a 48px taskbar at the bottom.
    private static readonly Rect Work = R(0, 0, 1920, 1152);

    private static Rect R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    [Theory]
    [InlineData(0, 0, 960, 1152)]      // left half
    [InlineData(960, 0, 1920, 1152)]   // right half
    [InlineData(0, 0, 960, 576)]       // top-left quarter
    [InlineData(960, 576, 1920, 1152)] // bottom-right quarter
    [InlineData(0, 0, 640, 1152)]      // left third (Snap Layouts)
    [InlineData(640, 0, 1920, 1152)]   // right two-thirds
    [InlineData(1, 0, 961, 1150)]      // within tolerance
    public void SnappedLayouts_AreRecognised(int l, int t, int r, int b) =>
        Assert.True(WindowMoveHaptics.IsSnapRect(R(l, t, r, b), Work));

    [Theory]
    [InlineData(100, 100, 900, 700)]  // floating window
    [InlineData(0, 0, 1920, 1152)]    // fills work area but not maximized (not a snap)
    [InlineData(0, 0, 900, 1152)]     // touches edges but right side off-grid
    [InlineData(480, 288, 1440, 864)] // on grid lines but touches no edge
    public void FloatingWindows_AreNotSnapped(int l, int t, int r, int b) =>
        Assert.False(WindowMoveHaptics.IsSnapRect(R(l, t, r, b), Work));
}
