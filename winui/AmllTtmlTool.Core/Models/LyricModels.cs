namespace AmllTtmlTool.Core.Models;

public sealed record TtmlMetadata(string Key, List<string> Value, bool Error = false);

public sealed record LyricWordBase(double StartTime, double EndTime, string Word);

public sealed record LyricWord(
    string Id,
    double StartTime,
    double EndTime,
    string Word,
    string RomanWord,
    bool Obscene,
    double EmptyBeat,
    bool RomanWarning = false,
    List<LyricWordBase>? Ruby = null)
{
    public static LyricWord New() => new(
        Guid.NewGuid().ToString("N"), 0, 0, string.Empty, string.Empty, false, 0);
}

public sealed record EndTimeLink(double OriginalEndTime, double? OriginalNextStartTime);

public sealed record LyricLine(
    string Id,
    List<LyricWord> Words,
    string TranslatedLyric,
    string RomanLyric,
    bool IsBg,
    bool IsDuet,
    double StartTime,
    double EndTime,
    bool IgnoreSync,
    EndTimeLink? EndTimeLink = null,
    // Raw ttm:agent attribute as parsed from the file (null when the <p>
    // carried none). Interchange-only display hint: the effective duet
    // presentation derives from this plus the document primary agent, while
    // IsDuet keeps exact WASM semantics for export. Null by default so all
    // hand-built records behave exactly as before.
    string? Agent = null)
{
    public static LyricLine New() => new(
        Guid.NewGuid().ToString("N"), [], string.Empty, string.Empty,
        false, false, 0, 0, false);
}

public sealed record TtmlLyric(List<TtmlMetadata> Metadata, List<LyricLine> LyricLines);
