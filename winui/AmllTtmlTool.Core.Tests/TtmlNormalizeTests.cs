using AmllTtmlTool.Core;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

public sealed class TtmlNormalizeTests
{
    private static LyricWord W(string word) =>
        new(Guid.NewGuid().ToString("N"), 0, 100, word, string.Empty, false, 0);

    [Fact]
    public void SplitTrailingSpace_SplitsWord()
    {
        var parts = TtmlNormalize.SplitTrailingSpace(W("hello "));
        Assert.Equal(2, parts.Count);
        Assert.Equal("hello", parts[0].Word);
        Assert.Equal(" ", parts[1].Word);
    }

    [Fact]
    public void SplitTrailingSpace_KeepsWhitespaceOnly()
    {
        Assert.Single(TtmlNormalize.SplitTrailingSpace(W(" ")));
    }

    [Fact]
    public void ApplyImportRenames_RenamesAndDrops()
    {
        var lyric = new TtmlLyric(
            [
                new TtmlMetadata("title", ["Song"]),
                new TtmlMetadata("songwriters", ["A"]),
                new TtmlMetadata("language", ["en"]),
            ],
            []);
        var result = TtmlNormalize.ApplyImportRenames(lyric);
        Assert.Equal(["musicName", "songwriter"],
            result.Metadata.Select(m => m.Key).ToList());
    }

    [Fact]
    public void IsLineTimingLyrics_Detects()
    {
        LyricLine L(int words) => new(Guid.NewGuid().ToString("N"),
            Enumerable.Range(0, words).Select(_ => W("w")).ToList(),
            string.Empty, string.Empty, false, false, 0, 100, false);
        Assert.True(TtmlNormalize.IsLineTimingLyrics(new TtmlLyric([], [L(1), L(1)])));
        Assert.False(TtmlNormalize.IsLineTimingLyrics(new TtmlLyric([], [L(2)])));
    }

    [Fact]
    public void PostProcess_RoundsTimes()
    {
        var w = W("x") with { StartTime = 10.4, EndTime = 10.5 };
        var line = new LyricLine(Guid.NewGuid().ToString("N"), [w],
            string.Empty, string.Empty, false, false, 10.4, 10.5, false);
        var result = TtmlNormalize.PostProcess(new TtmlLyric([], [line]));
        Assert.Equal(10, result.LyricLines[0].StartTime);
        Assert.Equal(11, result.LyricLines[0].EndTime);
    }
}
