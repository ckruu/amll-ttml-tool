using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

internal static class GoldenLyric
{
    private static LyricWord W(double s, double e, string word, string roman = "", bool obscene = false, double beats = 0, List<LyricWordBase>? ruby = null) =>
        new(Guid.NewGuid().ToString("N"), s, e, word, roman, obscene, beats, false, ruby);

    public static TtmlLyric Rich() => new(
        [new("musicName", ["Song"]), new("artists", ["A", "B"]), new("language", ["en"])],
        [
            new LyricLine(Guid.NewGuid().ToString("N"),
                [W(1000, 1500, "Hello", "he-ro", false, 0, [new(1000, 1500, "x")]),
                 W(1500, 1500, " "),
                 W(1600, 2200, "world", "", true, 2)],
                "你好世界", "ni hao", false, false, 1000, 2200, false),
            new LyricLine(Guid.NewGuid().ToString("N"),
                [W(2300, 2800, "hey")], "", "", true, true, 2300, 2800, false),
        ]);

    public static TtmlLyric EmptyBgFirst() => new([],
        [new LyricLine(Guid.NewGuid().ToString("N"), [], "", "", false, false, 0, 100, false),
         new LyricLine(Guid.NewGuid().ToString("N"), [W(200, 300, "bg")], "", "", true, false, 200, 300, false),
         new LyricLine(Guid.NewGuid().ToString("N"), [W(400, 500, "m")], "", "", false, false, 400, 500, false)]);
}
