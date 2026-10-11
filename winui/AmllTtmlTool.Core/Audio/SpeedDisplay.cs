using System.Globalization;

namespace AmllTtmlTool.Core.Audio;

// Pure playback-speed label + preset logic for the speed tip (no OS).
// Fully unit-testable.
public static class SpeedDisplay
{
    // Preset row order on the tip.
    public static readonly double[] Presets = [0.25, 0.5, 1.0, 1.5, 1.25];

    // Right-click cycle order, read from 1x with wraparound.
    public static readonly double[] CycleOrder = [1.0, 1.5, 1.25, 0.25, 0.5];

    public const double Tolerance = 0.01;

    // Simplified button text: 0.5 -> ".5x", 1 -> "1x", 1.25 -> "1.25x".
    // Trailing zeros are stripped, as is the leading zero below 1.
    public static string Format(double rate)
    {
        var digits = rate.ToString("0.##", CultureInfo.InvariantCulture);
        if (digits.StartsWith("0.", StringComparison.Ordinal))
            digits = digits[1..];
        return digits + "x";
    }

    // Index into Presets, or -1 when the rate matches no preset.
    public static int PresetIndex(double rate)
    {
        for (var i = 0; i < Presets.Length; i++)
        {
            if (Math.Abs(Presets[i] - rate) <= Tolerance) return i;
        }
        return -1;
    }

    // Right-click target: the next preset in cycle order, or 1x when the
    // current rate is not a preset.
    public static double CycleFrom(double rate)
    {
        for (var i = 0; i < CycleOrder.Length; i++)
        {
            if (Math.Abs(CycleOrder[i] - rate) <= Tolerance)
                return CycleOrder[(i + 1) % CycleOrder.Length];
        }
        return 1.0;
    }
}
