namespace AmllTtmlTool.Core.Audio;

// Pure volume-state glyph resolution (no OS): slider level + mute state to
// a Segoe codepoint. Mapping VERIFIED BY RENDERING (glyph-chart screenshot,
// Segoe Fluent Icons with Segoe MDL2 Assets fallback): E74F renders speaker
// + X (mute), E993 one wave, E994 two waves, E995 three waves. Doc names
// alone are not trusted — see the chart. Fully unit-testable.
public static class VolumeGlyph
{
    public const string Mute = "\uE74F";
    public const string OneWave = "\uE993";
    public const string TwoWaves = "\uE994";
    public const string ThreeWaves = "\uE995";

    public static string ForLevel(double percent, bool muted)
    {
        if (muted || double.IsNaN(percent) || percent <= 0) return Mute;
        if (percent < 34) return OneWave;
        if (percent < 67) return TwoWaves;
        return ThreeWaves;
    }
}
