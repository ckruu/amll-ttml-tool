using System.Text.RegularExpressions;

namespace AmllTtmlTool.Core.Lyrics;

// Integer-millisecond time helper for the lyric edit model.
// Mirrors src/utils/timestamp.ts exactly (parseTimespan / msToTimestamp):
// parsing "[[hh:]mm:]ss[.mmm]" (frac = 1-3 digits, decimal, NOT padded
// milliseconds: "00:01.1" = 1100ms), formatting "mm:ss.mmm" (hours shown
// only when nonzero). Deviations forced by int ms: no NaN/Infinity inputs
// (documented, tested); overflow beyond int range throws FormatException.
public static partial class LyricTime
{
    // ^(((hour):)?(min):)?((sec)([.:](frac))?)$
    [GeneratedRegex(@"^(((?<hour>[0-9]+):)?(?<min>[0-9]+):)?((?<sec>[0-9]+)([.:](?<frac>[0-9]{1,3}))?)$")]
    private static partial Regex TimeRegex();

    public static int Parse(string timeSpan)
    {
        var m = TimeRegex().Match(timeSpan);
        if (!m.Success)
            throw new FormatException($"Invalid timestamp: '{timeSpan}'.");
        var hour = long.Parse(m.Groups["hour"].Value is { Length: > 0 } h ? h : "0");
        var min = long.Parse(m.Groups["min"].Value is { Length: > 0 } mi ? mi : "0");
        var sec = long.Parse(m.Groups["sec"].Value);
        var frac = m.Groups["frac"].Value is { Length: > 0 } f ? f.PadRight(3, '0') : "0";
        var total = checked((hour * 3600 + min * 60 + sec) * 1000 + long.Parse(frac));
        if (total > int.MaxValue)
            throw new FormatException($"Timestamp out of range: '{timeSpan}'.");
        return (int)total;
    }

    public static string Format(int ms, bool withMilliseconds = true)
    {
        if (ms < 0) ms = 0;
        var totalSec = ms / 1000;
        var rem = ms % 1000;
        var secs = totalSec % 60;
        var mins = (totalSec / 60) % 60;
        var hrs = totalSec / 3600;
        if (!withMilliseconds)
            return hrs > 0
                ? $"{hrs:D2}:{mins:D2}:{secs:D2}"
                : $"{mins:D2}:{secs:D2}";
        var s = $"{secs:D2}.{rem:D3}";
        return hrs > 0 ? $"{hrs:D2}:{mins:D2}:{s}" : $"{mins:D2}:{s}";
    }
}
