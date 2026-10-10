namespace AmllTtmlTool.Core;

// Pure position-clock math: QPC/message-tick conversion and snapshot
// extrapolation. No threads, no UI, fully unit-testable.
public static class ClockMath
{
    // Converts a Win32 message time (ms since boot, 32-bit, wraps every
    // ~49.7 days) to a QPC timestamp, given the current readings of both
    // clocks. The unchecked subtraction is wrap-safe by two's complement.
    // Ticks in the future clamp to now (a key cannot happen after we ask).
    public static long QpcFromMessageTick(uint messageTickMs, ulong tickNowMs, long qpcNow, long qpcFrequency)
    {
        var deltaMs = unchecked((int)(messageTickMs - (uint)tickNowMs));
        if (deltaMs > 0) deltaMs = 0;
        return qpcNow + deltaMs * qpcFrequency / 1000;
    }

    // Source-time position at a QPC instant from a snapshot. Linear in the
    // snapshot's rate; callers freeze (rate 0 / paused state) by passing
    // the snapshot values through unchanged.
    public static double ExtrapolateSeconds(
        double baseSeconds, long baseQpcTicks, long qpcNow, long qpcFrequency, double rate) =>
        baseSeconds + (double)(qpcNow - baseQpcTicks) / qpcFrequency * rate;
}
