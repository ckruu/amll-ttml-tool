using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AmllTtmlTool.WinUI.Services;

public enum PlaybackClockState
{
    Empty,
    Playing,
    Paused,
    Seeking,
}

// Immutable snapshot published by the engine and read by the UI.
// Swapped by reference (atomic); readers never block and never tear.
public sealed class PlaybackSnapshot
{
    // Source-audio seconds already compensated for output latency, i.e.
    // the time of the audio coming out of the speakers at QpcTicks.
    public double SourceSeconds { get; init; }
    public long QpcTicks { get; init; }
    public double Rate { get; init; }
    public PlaybackClockState State { get; init; }

    public static readonly PlaybackSnapshot Empty = new()
    {
        SourceSeconds = 0,
        QpcTicks = 0,
        Rate = 1.0,
        State = PlaybackClockState.Empty,
    };
}

// The one clock every time consumer uses. The engine publishes snapshots
// (play/pause/seek/rate-change/250ms-rebase, flat while the startup gate
// is armed); the UI extrapolates between them with QPC on every rendered
// frame. Same UI thread publishes and reads, so ordering is strict and
// monotonicity holds by construction.
public static class PositionClock
{
    private static PlaybackSnapshot _latest = PlaybackSnapshot.Empty;
    private static long _publishCount;
    private static readonly AmllTtmlTool.Core.SkewEstimator _skew = new();

    public static PlaybackSnapshot Latest => Volatile.Read(ref _latest);

    // Monotonic count of snapshots published (rebase health signal).
    public static long PublishCount => Volatile.Read(ref _publishCount);

    // Learned device-vs-wall skew in fractional units (diagnostics).
    public static double SkewPpm => _skew.Skew * 1_000_000.0;

    public static void Publish(PlaybackSnapshot snapshot)
    {
        var prev = Volatile.Read(ref _latest);
        // Feed consecutive playing snapshots at the same rate into the
        // skew learner: seeks/pauses/rate-changes/gate-flats fail the
        // state/rate guards, gaps and jumps fail the estimator's own
        // range guards.
        if (prev.State == PlaybackClockState.Playing
            && snapshot.State == PlaybackClockState.Playing
            && prev.Rate > 0.05
            && System.Math.Abs(prev.Rate - snapshot.Rate) < 0.001
            && prev.QpcTicks > 0 && snapshot.QpcTicks > prev.QpcTicks)
        {
            var wallDt = (snapshot.QpcTicks - prev.QpcTicks) / (double)Stopwatch.Frequency;
            if (wallDt > 0)
                _skew.Update(snapshot.SourceSeconds - prev.SourceSeconds, wallDt, prev.Rate);
        }
        if (snapshot.State == PlaybackClockState.Empty) _skew.Reset();
        Volatile.Write(ref _latest, snapshot);
        Interlocked.Increment(ref _publishCount);
    }

    public static TimeSpan ExtrapolateNow() => Extrapolate(Stopwatch.GetTimestamp());

    // Rate slope corrected by the learned device skew: snapshots land at
    // least every engine tick (250ms), so the slope only has to carry
    // sub-rebase intervals; the learner keeps 30s endpoint drift small on
    // devices hundreds of ppm off QPC without any per-sample whiplash.
    public static TimeSpan Extrapolate(long qpcNow)
    {
        var s = Latest;
        if (s.State != PlaybackClockState.Playing)
            return Clamp(TimeSpan.FromSeconds(s.SourceSeconds));
        var slope = s.Rate * (1.0 + _skew.Skew);
        var pos = AmllTtmlTool.Core.ClockMath.ExtrapolateSeconds(
            s.SourceSeconds, s.QpcTicks, qpcNow, Stopwatch.Frequency, slope);
        return Clamp(TimeSpan.FromSeconds(pos));
    }

    public static TimeSpan GetPositionNow() => ExtrapolateNow();

    // Key-event timestamp path. WinUI 3 key events carry no high-resolution
    // timestamp: the only event time available for a key press is the Win32
    // message time (GetMessageTime-style milliseconds, ~10-16ms coarse),
    // while handler-entry QPC additionally includes dispatcher queue delay.
    // Pointer input is different: PointerPoint.Timestamp is microseconds, so
    // pointer stamping should convert that directly, not go through here.
    public static TimeSpan GetPositionAtMessageTime(uint messageTickMs)
    {
        var qpc = AmllTtmlTool.Core.ClockMath.QpcFromMessageTick(
            messageTickMs, GetTickCount64(), Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        return Extrapolate(qpc);
    }

    private static TimeSpan Clamp(TimeSpan t) =>
        t < TimeSpan.Zero ? TimeSpan.Zero : t;

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}

// Always-on, negligible-cost counters for the --clock-test harness.
internal static class RenderStats
{
    public static long Frames;
    public static long HandlerTicks;
    public static long TextUpdates;
}
