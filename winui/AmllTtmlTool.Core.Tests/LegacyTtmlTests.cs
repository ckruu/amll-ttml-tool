using AmllTtmlTool.Core;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

public sealed class LegacyTtmlTests
{
    [Fact]
    public void DetectLegacyTtml_FindsReasons()
    {
        var d = LegacyTtml.DetectLegacyTtml("<tt xmlns:amll=\"x\"><body dur=\"1\"><div begin=\"0\"></div></body></tt>");
        Assert.True(d.IsLegacy);
        Assert.Contains("xmlns:amll namespace", d.Reasons);
        Assert.Contains("body dur attribute", d.Reasons);
        Assert.Contains("begin/end on <div>", d.Reasons);
    }

    [Fact]
    public void DetectLegacyTtml_CleanAppleTtml()
    {
        var xml = AppleTtml.ToAppleTtml(new TtmlLyric([], []));
        var d = LegacyTtml.DetectLegacyTtml(xml);
        Assert.False(d.IsLegacy);
        Assert.Empty(d.Reasons);
    }

    [Fact]
    public void ExtractXmlLang_ReadsAttribute()
    {
        Assert.Equal("ja", LegacyTtml.ExtractXmlLang("<tt xml:lang=\"ja\">"));
        Assert.Equal("en", LegacyTtml.ExtractXmlLang("<tt xml:lang='en'>"));
        Assert.Null(LegacyTtml.ExtractXmlLang("<tt>"));
    }

    [Fact]
    public void RepairLegacyImport_RestoresBgParens()
    {
        LyricWord W(string word) =>
            new(Guid.NewGuid().ToString("N"), 1000, 2000, word, string.Empty, false, 0);
        var bg = new LyricLine(Guid.NewGuid().ToString("N"),
            [W("hey")], string.Empty, string.Empty, true, false, 1000, 2000, false);
        var (result, fixes) = LegacyTtml.RepairLegacyImport(new TtmlLyric([], [bg]), "<tt></tt>");
        Assert.Equal("(hey)", result.LyricLines[0].Words[0].Word);
        Assert.Contains(fixes, f => f.Contains("parentheses"));
    }

    [Fact]
    public void RepairLegacyImport_RestoresDuetFromAgents()
    {
        LyricWord W(string word) =>
            new(Guid.NewGuid().ToString("N"), 0, 500, word, string.Empty, false, 0);
        var a = new LyricLine(Guid.NewGuid().ToString("N"),
            [W("la")], string.Empty, string.Empty, false, false, 0, 500, false);
        var b = new LyricLine(Guid.NewGuid().ToString("N"),
            [W("lu")], string.Empty, string.Empty, false, false, 500, 1000, false);
        var raw = "<p ttm:agent=\"v1\"/><p ttm:agent=\"v2\"/>";
        var (result, fixes) = LegacyTtml.RepairLegacyImport(new TtmlLyric([], [a, b]), raw);
        Assert.False(result.LyricLines[0].IsDuet);
        Assert.True(result.LyricLines[1].IsDuet);
        Assert.Contains(fixes, f => f.Contains("duet"));
    }
}
