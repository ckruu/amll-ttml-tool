using AmllTtmlTool.Core;
using Xunit;

namespace AmllTtmlTool.Core.Tests;

public sealed class SkewEstimatorTests
{
    // 250ms rebase cadence, device +233ppm fast (typical onboard audio).
    [Fact]
    public void Converges_ToConstantSkew()
    {
        var est = new SkewEstimator();
        for (var i = 0; i < 120; i++)
            est.Update(0.25 * 1.000233, 0.25, 1.0);
        Assert.InRange(est.Skew, 0.00020, 0.00027);
    }

    [Fact]
    public void IgnoresSeekJump()
    {
        var est = new SkewEstimator();
        for (var i = 0; i < 40; i++)
            est.Update(0.25, 0.25, 1.0);
        var before = est.Skew;
        est.Update(-9.5, 0.25, 1.0); // seek back 9.5s between rebases
        Assert.InRange(est.Skew - before, -0.00002, 0.00002);
    }

    [Fact]
    public void IgnoresShortAndLongGaps()
    {
        var est = new SkewEstimator();
        est.Update(0.01, 0.01, 1.0); // 10ms gap: pull jitter, not skew
        est.Update(5.0, 5.0, 1.0); // 5s gap: pause/resume, not skew
        Assert.Equal(0, est.Skew);
    }

    [Fact]
    public void IgnoresZeroRate()
    {
        var est = new SkewEstimator();
        est.Update(0.0, 0.25, 0.0); // flat startup-gate snapshot
        Assert.Equal(0, est.Skew);
    }

    [Fact]
    public void ClampsUsbStep()
    {
        var est = new SkewEstimator();
        est.Update(0.25 * 1.02, 0.25, 1.0); // sudden +2% USB feedback step
        Assert.InRange(est.Skew, -0.00003, 0.00003); // <=500ppm * 5% gain
    }

    [Fact]
    public void CapsAt2000ppm()
    {
        var est = new SkewEstimator();
        for (var i = 0; i < 300; i++)
            est.Update(0.25 * 1.01, 0.25, 1.0); // sustained +1% (beyond any real crystal)
        Assert.Equal(0.002, est.Skew);
    }

    [Fact]
    public void Reset_Clears()
    {
        var est = new SkewEstimator();
        for (var i = 0; i < 120; i++)
            est.Update(0.25 * 1.000233, 0.25, 1.0);
        est.Reset();
        Assert.Equal(0, est.Skew);
    }
}
