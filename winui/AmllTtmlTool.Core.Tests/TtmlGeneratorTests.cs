using AmllTtmlTool.Core;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

public sealed class TtmlGeneratorTests
{
    private static readonly string Fixtures =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(Fixtures, name + ".xml"));

    private static LyricWord W(double s, double e, string word, string roman = "", bool obscene = false, double beats = 0, List<LyricWordBase>? ruby = null) =>
        new(Guid.NewGuid().ToString("N"), s, e, word, roman, obscene, beats, false, ruby);

    private static LyricLine L(List<LyricWord> words, string trans = "", string roman = "", bool bg = false, bool duet = false, double start = 0, double end = 0) =>
        new(Guid.NewGuid().ToString("N"), words, trans, roman, bg, duet,
            words.Count > 0 ? start : start, words.Count > 0 ? end : end, false);

    private static readonly GeneratorConfig Compact = new(false, false, false);

    [Fact]
    public void Generate_Simple_MatchesGolden()
    {
        var lyric = new TtmlLyric(
            [new("musicName", ["Song"]), new("artists", ["A", "B"]), new("language", ["en"])],
            [new LyricLine(Guid.NewGuid().ToString("N"),
                [W(1000, 1500, "Hello"), W(1500, 1500, " "), W(1600, 2200, "world")],
                "", "", false, false, 1000, 2200, false)]);
        Assert.Equal(Golden("gen-simple"), TtmlGenerator.Generate(lyric, new(), Compact).Data);
    }

    [Fact]
    public void Generate_Rich_MatchesGolden()
    {
        var lyric = new TtmlLyric(
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
        Assert.Equal(Golden("gen-rich"), TtmlGenerator.Generate(lyric, new(), Compact).Data);
    }

    [Fact]
    public void Generate_RichPretty_MatchesGolden()
    {
        var lyric = new TtmlLyric(
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
        Assert.Equal(Golden("gen-rich-pretty"),
            TtmlGenerator.Generate(lyric, new(), new(false, true, false)).Data);
    }

    [Fact]
    public void Generate_DuetRoman_MatchesGolden()
    {
        var lyric = new TtmlLyric([new("musicName", ["S"])],
            [L([W(0, 500, "la")], start: 0, end: 500),
             L([W(500, 1000, "lu")], "", "lu-lu", false, true, 500, 1000)]);
        var opts = new TtmlGenerateOptions("ja", null);
        Assert.Equal(Golden("gen-duet-roman"), TtmlGenerator.Generate(lyric, opts, Compact).Data);
    }

    [Fact]
    public void Generate_EmptyBgFirst_MatchesGolden()
    {
        var lyric = new TtmlLyric([],
            [new LyricLine(Guid.NewGuid().ToString("N"), [], "", "", false, false, 0, 100, false),
             L([W(200, 300, "bg")], "", "", true, false, 200, 300),
             L([W(400, 500, "m")], "", "", false, false, 400, 500)]);
        Assert.Equal(Golden("gen-empty-bgfirst"), TtmlGenerator.Generate(lyric, new(), Compact).Data);
    }

    [Fact]
    public void Generate_LineTiming_MatchesGolden()
    {
        var lyric = new TtmlLyric([],
            [L([W(0, 500, "la")], start: 0, end: 500)]);
        Assert.Equal(Golden("gen-linetiming"),
            TtmlGenerator.Generate(lyric, new(), new(false, false, true)).Data);
    }

    [Fact]
    public void Generate_AppleRules_MatchesGolden()
    {
        var lyric = new TtmlLyric([new("musicName", ["S"])],
            [L([W(0, 500, "a")], "T", "", false, false, 0, 500)]);
        Assert.Equal(Golden("gen-apple-rules"),
            TtmlGenerator.Generate(lyric, new(), new(true, false, false)).Data);
    }

    [Fact]
    public void Generate_ITunesMeta_MatchesGolden()
    {
        var lyric = new TtmlLyric([new("songwriters", ["W"])],
            [L([W(0, 500, "a", "r")], "T", "", false, false, 0, 500)]);
        Assert.Equal(Golden("gen-itunes-meta"), TtmlGenerator.Generate(lyric, new(), Compact).Data);
    }

    private static LyricLine EmptyLine(double start, double end) =>
        new(Guid.NewGuid().ToString("N"), [], "", "", false, false, start, end, false);

    [Fact]
    public void Generate_OnlyEmpty_MatchesGolden()
    {
        var lyric = new TtmlLyric([], [EmptyLine(0, 100)]);
        Assert.Equal(Golden("gen-only-empty"), TtmlGenerator.Generate(lyric, new(), Compact).Data);
    }

    [Fact]
    public void Generate_MainThenEmpty_MatchesGolden()
    {
        var lyric = new TtmlLyric([],
            [L([W(0, 500, "m")], start: 0, end: 500), EmptyLine(600, 700)]);
        Assert.Equal(Golden("gen-main-then-empty"), TtmlGenerator.Generate(lyric, new(), Compact).Data);
    }

    [Fact]
    public void Generate_EmptyThenMain_MatchesGolden()
    {
        var lyric = new TtmlLyric([],
            [EmptyLine(0, 100), L([W(200, 300, "m")], start: 200, end: 300)]);
        Assert.Equal(Golden("gen-empty-then-main"), TtmlGenerator.Generate(lyric, new(), Compact).Data);
    }

    [Fact]
    public void Generate_BlankWords_MatchesGolden()
    {
        var lyric = new TtmlLyric([],
            [L([W(0, 100, "")], start: 0, end: 100)]);
        Assert.Equal(Golden("gen-blank-words"), TtmlGenerator.Generate(lyric, new(), Compact).Data);
    }

    [Fact]
    public void Generate_LeadBg_MatchesGolden()
    {
        var lyric = new TtmlLyric([],
            [L([W(200, 300, "bg")], "", "", true, false, 0, 900)]);
        Assert.Equal(Golden("gen-lead-bg"), TtmlGenerator.Generate(lyric, new(), Compact).Data);
    }

    [Fact]
    public void Generate_LeadDuetBg_MatchesGolden()
    {
        var lyric = new TtmlLyric([],
            [L([W(200, 300, "bg")], "", "", true, true, 0, 900)]);
        Assert.Equal(Golden("gen-lead-duetbg"), TtmlGenerator.Generate(lyric, new(), Compact).Data);
    }

    [Theory]
    [InlineData(0, "0.000")]
    [InlineData(500, "0.500")]
    [InlineData(90500, "1:30.500")]
    [InlineData(125000, "2:05.000")]
    [InlineData(3723004, "1:02:03.004")]
    [InlineData(1500.5, "1.501")]
    public void Fmt_Formats(double ms, string expected) =>
        Assert.Equal(expected, TtmlGenerator.Fmt(ms));

    [Fact]
    public void Escape_Escapes() =>
        Assert.Equal("a &amp;b &lt;c&gt; &quot;q&quot;", TtmlGenerator.Escape("a &b <c> \"q\""));
}
