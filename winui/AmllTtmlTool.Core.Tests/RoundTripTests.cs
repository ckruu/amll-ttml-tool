using AmllTtmlTool.Core;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

public sealed class RoundTripTests
{
    private static readonly string Fixtures =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(Fixtures, name + ".xml"));

    [Theory]
    [InlineData("gen-simple")]
    [InlineData("gen-rich")]
    [InlineData("gen-rich-pretty")]
    [InlineData("gen-duet-roman")]
    [InlineData("gen-empty-bgfirst")]
    [InlineData("gen-linetiming")]
    [InlineData("gen-apple-rules")]
    [InlineData("gen-itunes-meta")]
    [InlineData("gen-only-empty")]
    [InlineData("gen-main-then-empty")]
    [InlineData("gen-empty-then-main")]
    [InlineData("gen-blank-words")]
    [InlineData("gen-lead-bg")]
    [InlineData("gen-lead-duetbg")]
    public void RoundTrip_ParseGenerateParse_Stable(string fixture)
    {
        var xml = Golden(fixture);
        var first = TtmlParser.Parse(xml);
        Assert.True(first.Success);
        var regen = TtmlGenerator.Generate(first.Data!, new(), new(false, false, false));
        Assert.True(regen.Success);
        var second = TtmlParser.Parse(regen.Data!);
        Assert.True(second.Success);
        LyricAssert.EqualLyrics(first.Data!, second.Data!);
    }

    [Fact]
    public void RoundTrip_GeneratedXml_ReparsesToSameWordCount()
    {
        var lyric = new TtmlLyric([],
            [new LyricLine(Guid.NewGuid().ToString("N"),
                [new LyricWord(Guid.NewGuid().ToString("N"), 0, 900, " Hello ", "", false, 0)],
                "", "", false, false, 0, 900, false)]);
        var gen = TtmlGenerator.Generate(lyric, new(), new(false, false, false));
        Assert.True(gen.Success);
        var back = TtmlParser.Parse(gen.Data!);
        Assert.True(back.Success);
        Assert.Equal(2, back.Data!.LyricLines[0].Words.Count);
        Assert.Equal(" Hello", back.Data.LyricLines[0].Words[0].Word);
        Assert.Equal(" ", back.Data.LyricLines[0].Words[1].Word);
    }
}
