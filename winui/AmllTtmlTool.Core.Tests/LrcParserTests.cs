using AmllTtmlTool.Core;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

public sealed class LrcParserTests
{
    [Fact]
    public void ParseLrc_BasicLines()
    {
        var lines = LrcParser.ParseLrc("[00:01.00]Hello\n[00:02.00]World\n");
        Assert.Equal(2, lines.Count);
        Assert.Equal("Hello", lines[0].Words[0].Word);
        Assert.Equal(1000, lines[0].StartTime);
        Assert.Equal(2000, lines[0].EndTime);
        Assert.Equal(12000, lines[1].EndTime);
    }

    [Fact]
    public void ParseLrc_GroupsSameTimestamp()
    {
        var lines = LrcParser.ParseLrc("[00:01.00]Main\n[00:01.00]Trans\n[00:01.00]Roman\n[00:05.00]Next\n");
        Assert.Equal(2, lines.Count);
        Assert.Equal("Main", lines[0].Words[0].Word);
        Assert.Equal("Trans", lines[0].TranslatedLyric);
        Assert.Equal("Roman", lines[0].RomanLyric);
    }

    [Fact]
    public void ParseLrc_SortsByTime()
    {
        var lines = LrcParser.ParseLrc("[00:05.00]B\n[00:01.00]A\n");
        Assert.Equal("A", lines[0].Words[0].Word);
        Assert.Equal("B", lines[1].Words[0].Word);
    }

    [Fact]
    public void ParseLrc_SkipsEmptyText()
    {
        var lines = LrcParser.ParseLrc("[00:01.00]\n[00:02.00]Hi\n");
        Assert.Single(lines);
        Assert.Equal("Hi", lines[0].Words[0].Word);
    }
}
