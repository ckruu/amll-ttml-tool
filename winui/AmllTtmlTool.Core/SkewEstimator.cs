namespace AmllTtmlTool.Core;

// Bounded slow device-skew estimator for the position clock. The device
// renders at its own clock (observed -1800..+1000ppm swings on USB DACs
// across runs); over 30s that is tens of ms of endpoint drift even though
// 250ms rebases keep the display sub-ms. This learns the fractional skew
// (device rate vs wall) from snapshot deltas and folds it into the
// extrapolation slope. Whiplash-proof by construction: per-sample clamp
// (+-500ppm), small gain (10%, ~2.5s time constant at 250ms rebases),
// hard cap (+-2000ppm), and skips for seeks/pauses/rate-changes/gaps
// (anything over 5% error).
public sealed class SkewEstimator
{
    private double _skew; // fraction, e.g. 0.000233 = +233ppm

    public double Skew => _skew;

    public void Reset() => _skew = 0;

    public void Update(double deltaSrcSeconds, double deltaWallSeconds, double expectedRate)
    {
        if (deltaWallSeconds < 0.05 || deltaWallSeconds > 2.0) return;
        if (System.Math.Abs(expectedRate) < 0.05) return;
        var observed = deltaSrcSeconds / deltaWallSeconds;
        // Close the loop against the current estimate: when the estimate
        // is right the error is zero, so a constant device skew converges
        // instead of ramping into the cap.
        var expected = expectedRate * (1.0 + _skew);
        var err = (observed - expected) / expectedRate; // fraction
        if (System.Math.Abs(err) > 0.05) return; // seek/pause/restart, not skew
        _skew = System.Math.Max(-0.002, System.Math.Min(0.002,
            _skew + System.Math.Max(-0.0005, System.Math.Min(0.0005, err)) * 0.1));
    }
}
