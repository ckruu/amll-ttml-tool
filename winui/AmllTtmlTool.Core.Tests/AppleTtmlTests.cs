using AmllTtmlTool.Core;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

public sealed class AppleTtmlTests
{
    private static LyricWord W(string word, double start, double end) =>
        new(Guid.NewGuid().ToString("N"), start, end, word, string.Empty, false, 0);

    private static LyricLine L(params LyricWord[] words) => new(
        Guid.NewGuid().ToString("N"), words.ToList(), string.Empty, string.Empty,
        false, false,
        words.Length == 0 ? 0 : words.Min(w => w.StartTime),
        words.Length == 0 ? 0 : words.Max(w => w.EndTime),
        false);

    [Theory]
    [InlineData(0, "00:00:00.000")]
    [InlineData(61000, "00:01:01.000")]
    [InlineData(3723004, "01:02:03.004")]
    [InlineData(1500.5, "00:00:01.501")]
    public void FormatTime_Formats(double ms, string expected) =>
        Assert.Equal(expected, AppleTtml.FormatTime(ms));

    [Fact]
    public void EscapeXml_Escapes()
    {
        Assert.Equal("a &amp; b &lt;c&gt;", AppleTtml.EscapeXml("a & b <c>"));
        Assert.Equal("\"q\"", AppleTtml.EscapeXml("\"q\""));
    }

    [Fact]
    public void ToAppleTtml_SimpleLine()
    {
        var lyric = new TtmlLyric([], [L(W("Hello", 1000, 1500), W("world", 1600, 2200))]);
        var xml = AppleTtml.ToAppleTtml(lyric);
        Assert.Contains("xml:lang=\"en\"", xml);
        Assert.Contains("<span begin=\"00:00:01.000\" end=\"00:00:01.500\">Hello</span>", xml);
        Assert.Contains("<span begin=\"00:00:01.600\" end=\"00:00:02.200\">world</span>", xml);
        Assert.Contains("<p begin=\"00:00:01.000\" end=\"00:00:02.200\">", xml);
    }

    [Fact]
    public void ToAppleTtml_BgLine_GroupedWithSpace()
    {
        var main = L(W("Hello", 1000, 1500));
        var bg = new LyricLine(Guid.NewGuid().ToString("N"),
            [W("hey", 1500, 2000)], string.Empty, string.Empty, true, false, 1500, 2000, false);
        var lyric = new TtmlLyric([], [main, bg]);
        var xml = AppleTtml.ToAppleTtml(lyric);
        Assert.Contains("ttm:role=\"x-bg\"", xml);
        Assert.Contains("(hey)", xml);
    }

    [Fact]
    public void ToAppleTtml_Duet_EmitsAgents()
    {
        var a = L(W("la", 0, 500)) with { IsDuet = false };
        var b = L(W("lu", 500, 1000)) with { IsDuet = true };
        var xml = AppleTtml.ToAppleTtml(new TtmlLyric([], [a, b]));
        Assert.Contains("<ttm:agent type=\"person\" xml:id=\"v1\"/>", xml);
        Assert.Contains("<ttm:agent type=\"person\" xml:id=\"v2\"/>", xml);
        Assert.Contains("ttm:agent=\"v2\"", xml);
    }

    [Fact]
    public void ToAppleTtml_ResolvesLangFromMetadata()
    {
        var lyric = new TtmlLyric(
            [new TtmlMetadata("language", ["ja"])],
            [L(W("a", 0, 100))]);
        Assert.Contains("xml:lang=\"ja\"", AppleTtml.ToAppleTtml(lyric));
        Assert.Contains("xml:lang=\"fr\"",
            AppleTtml.ToAppleTtml(lyric, new AppleTtmlOptions { Lang = "fr" }));
    }

    [Fact]
    public void GetAppleTtmlWarnings_FlagsWholeLineSpan()
    {
        var line = L(W("all", 0, 5000)) with { StartTime = 0, EndTime = 5000 };
        var warnings = AppleTtml.GetAppleTtmlWarnings(new TtmlLyric([], [line]));
        Assert.Single(warnings);
        Assert.DoesNotContain("BG", warnings[0]);
    }

    [Fact]
    public void GetAppleTtmlWarnings_SkipsBg()
    {
        var bg = L(W("x", 0, 100)) with { IsBg = true, StartTime = 0, EndTime = 100 };
        Assert.Empty(AppleTtml.GetAppleTtmlWarnings(new TtmlLyric([], [bg])));
    }
}
