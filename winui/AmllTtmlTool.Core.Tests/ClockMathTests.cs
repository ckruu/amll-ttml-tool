using AmllTtmlTool.Core;
using Xunit;

namespace AmllTtmlTool.Core.Tests;

public sealed class ClockMathTests
{
    private const long Freq = 10_000_000; // QPC ticks per second

    [Fact]
    public void MessageTick_NormalPast_Converts()
    {
        // 500ms ago.
        var qpc = ClockMath.QpcFromMessageTick(1000u, 1500ul, 15_000_000L, Freq);
        Assert.Equal(10_000_000L, qpc);
    }

    [Fact]
    public void MessageTick_Future_ClampsToNow()
    {
        var qpc = ClockMath.QpcFromMessageTick(2000u, 1500ul, 15_000_000L, Freq);
        Assert.Equal(15_000_000L, qpc);
    }

    [Fact]
    public void MessageTick_Wraparound_Forward()
    {
        // Counter wrapped: now is just past the wrap (small), event just
        // before it (near uint.MaxValue) => ~100ms ago.
        uint now = 50;
        uint evt = uint.MaxValue - 49; // 100 ticks earlier mod 2^32
        var qpc = ClockMath.QpcFromMessageTick(evt, now, 15_000_000L, Freq);
        Assert.Equal(14_000_000L, qpc);
    }

    [Fact]
    public void MessageTick_Wraparound_Backward_StaysClamped()
    {
        // Event tick numerically larger because of wrap but actually in the
        // future relative to now => clamp.
        uint now = uint.MaxValue - 10;
        uint evt = 50;
        var qpc = ClockMath.QpcFromMessageTick(evt, now, 15_000_000L, Freq);
        Assert.Equal(15_000_000L, qpc);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Extrapolate_Rates(double rate)
    {
        // 10.0s base, 1.5 QPC-seconds later.
        var pos = ClockMath.ExtrapolateSeconds(10.0, 100_000_000L, 115_000_000L, Freq, rate);
        Assert.Equal(10.0 + 1.5 * rate, pos, precision: 9);
    }

    [Fact]
    public void Extrapolate_PausedRateZero_Holds()
    {
        var pos = ClockMath.ExtrapolateSeconds(10.0, 100_000_000L, 999_000_000L, Freq, 0.0);
        Assert.Equal(10.0, pos, precision: 9);
    }

    [Fact]
    public void Extrapolate_EventBeforeSnapshot_LinearBackwards()
    {
        // Key pressed 200ms before the snapshot was taken (seek landed
        // between event and call): linear back-extrapolation still applies.
        var pos = ClockMath.ExtrapolateSeconds(10.0, 100_000_000L, 98_000_000L, Freq, 1.0);
        Assert.Equal(9.8, pos, precision: 9);
    }

    [Fact]
    public void Extrapolate_AtSnapshot_Exact()
    {
        var pos = ClockMath.ExtrapolateSeconds(3.25, 50_000_000L, 50_000_000L, Freq, 2.0);
        Assert.Equal(3.25, pos, precision: 9);
    }
}
