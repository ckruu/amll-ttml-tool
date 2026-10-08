using System.Text.RegularExpressions;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core;

public static partial class TtmlNormalize
{
    private static readonly HashSet<string> IgnoredMetadataKeys = ["timingMode", "language"];

    private static readonly Dictionary<string, string> RenamedMetadataKeys = new()
    {
        ["songwriters"] = "songwriter",
        ["title"] = "musicName",
    };

    private static readonly Dictionary<string, string> ExportRenamedMetadataKeys = new()
    {
        ["songwriter"] = "songwriters",
    };

    public static List<LyricWord> SplitTrailingSpace(LyricWord word)
    {
        var m = TrailingWhitespace().Match(word.Word);
        if (m.Success && word.Word != m.Value)
        {
            var spaces = m.Value;
            return
            [
                word with { Word = word.Word[..^spaces.Length] },
                new LyricWord(Guid.NewGuid().ToString("N"), 0, 0, spaces, string.Empty, false, 0),
            ];
        }
        return [word];
    }

    public static TtmlLyric ApplyImportRenames(TtmlLyric lyric) => lyric with
    {
        LyricLines = lyric.LyricLines
            .Select(l => l with { Words = l.Words.SelectMany(SplitTrailingSpace).ToList() })
            .ToList(),
        Metadata = lyric.Metadata
            .Where(m => !IgnoredMetadataKeys.Contains(m.Key))
            .Select(m => m with { Key = RenamedMetadataKeys.GetValueOrDefault(m.Key, m.Key) })
            .ToList(),
    };

    public static TtmlLyric NormalizeForExport(TtmlLyric lyric) => lyric with
    {
        LyricLines = lyric.LyricLines.Select(l => l with
        {
            Words = l.Words.Select(w => new LyricWord(
                w.Id, w.StartTime, w.EndTime, w.Word, w.RomanWord,
                w.Obscene, w.EmptyBeat, w.RomanWarning,
                w.Ruby?.Select(r => new LyricWordBase(r.StartTime, r.EndTime, r.Word)).ToList())).ToList(),
        }).ToList(),
        Metadata = lyric.Metadata
            .Select(m => m with
            {
                Key = ExportRenamedMetadataKeys.GetValueOrDefault(m.Key, m.Key),
                Value = m.Value.ToList(),
            })
            .ToList(),
    };

    public static TtmlLyric PostProcess(TtmlLyric lyric) => lyric with
    {
        LyricLines = lyric.LyricLines.Select(l => l with
        {
            StartTime = Round(l.StartTime),
            EndTime = Round(l.EndTime),
            Words = l.Words.Select(w =>
            {
                var emptyBeat = w.EmptyBeat == 0 ? 0 : w.EmptyBeat;
                var roman = string.IsNullOrWhiteSpace(w.RomanWord) ? string.Empty : w.RomanWord;
                return w with
                {
                    StartTime = Round(w.StartTime),
                    EndTime = Round(w.EndTime),
                    RomanWord = roman,
                    EmptyBeat = emptyBeat,
                    Ruby = w.Ruby?.Select(r => r with
                    {
                        StartTime = Round(r.StartTime),
                        EndTime = Round(r.EndTime),
                    }).ToList(),
                };
            }).ToList(),
        }).ToList(),
    };

    public static bool IsLineTimingLyrics(TtmlLyric lyric)
    {
        var nonBg = lyric.LyricLines.Where(l => !l.IsBg).ToList();
        return nonBg.Count > 0 && nonBg.All(l => l.Words.Count == 1);
    }

    private static double Round(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

    [GeneratedRegex("(\\s+)$")]
    private static partial Regex TrailingWhitespace();
}
