using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

public sealed class LyricAdapterTests
{
    private static readonly string Fixtures =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static TtmlLyric ParseOk(string name)
    {
        var xml = File.ReadAllText(Path.Combine(Fixtures, name + ".xml"));
        var result = TtmlParser.Parse(xml);
        Assert.True(result.Success);
        return result.Data!;
    }

    public static IEnumerable<object[]> AllFixtures() => new[]
    {
        "gen-simple", "gen-rich", "gen-rich-pretty", "gen-duet-roman",
        "gen-empty-bgfirst", "gen-linetiming", "gen-apple-rules",
        "gen-itunes-meta", "gen-only-empty", "gen-main-then-empty",
        "gen-empty-then-main", "gen-blank-words", "gen-lead-bg",
        "gen-lead-duetbg",
    }.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Records_Edit_Records_Round_Trip_Equal(string fixture)
    {
        var records = ParseOk(fixture);
        var doc = LyricAdapters.FromRecords(records, "TTML");
        var back = LyricAdapters.ToRecords(doc);
        LyricAssert.EqualLyrics(records, back);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Adapters_Are_Invisible_To_Serialized_Output(string fixture)
    {
        var records = ParseOk(fixture);
        var direct = TtmlGenerator.Generate(records, new(), new(false, false, false));
        var viaEdit = TtmlGenerator.Generate(
            LyricAdapters.ToRecords(LyricAdapters.FromRecords(records, "TTML")),
            new(), new(false, false, false));
        Assert.True(direct.Success);
        Assert.True(viaEdit.Success);
        Assert.Equal(direct.Data, viaEdit.Data);
    }

    [Fact]
    public void Ids_And_Flags_Survive_The_Adapter()
    {
        var records = new TtmlLyric(
            [new TtmlMetadata("musicName", ["S"], true)],
            [new LyricLine("L1",
                [new LyricWord("W1", 100, 200, "hi", "hai", true, 2, true,
                    [new LyricWordBase(100, 200, "r")])],
                "T", "R", true, true, 100, 200, true)]);
        var doc = LyricAdapters.FromRecords(records, "TTML");
        Assert.Equal("L1", doc.Lines[0].Id);
        Assert.Equal("W1", doc.Lines[0].Words[0].Id);
        Assert.True(doc.Lines[0].IgnoreSync);
        Assert.True(doc.Lines[0].Words[0].RomanWarning);
        Assert.Equal(2, doc.Lines[0].Words[0].EmptyBeat);
        Assert.True(doc.Metadata[0].Error);
        Assert.Single(doc.Lines[0].Words[0].Ruby);
        var back = LyricAdapters.ToRecords(doc);
        LyricAssert.EqualLyrics(records, back);
    }

    [Fact]
    public void Fractional_Ms_Reported_With_Source_And_Count()
    {
        var records = new TtmlLyric([],
            [new LyricLine("L1",
                [new LyricWord("W1", 100.5, 200, "a", "", false, 0),
                 new LyricWord("W2", 200, 300.25, "b", "", false, 0)],
                "", "", false, false, 100.5, 300.25, false)]);
        var ex = Assert.Throws<LyricAdapterException>(() =>
            LyricAdapters.FromRecords(records, "YRC"));
        Assert.Contains("YRC", ex.Message);
        Assert.Contains("4 non-whole-millisecond", ex.Message);
        Assert.Contains("line[0].words[0].startTime=100.5", ex.Message);
    }

    [Fact]
    public void Float_Noise_Rounds_Without_Report()
    {
        // 0.1s-style doubles (100.00000000001) are float noise, not data.
        var records = new TtmlLyric([],
            [new LyricLine("L1", [new LyricWord("W1", 100.00000000001, 200, "a", "", false, 0)],
                "", "", false, false, 0, 200, false)]);
        var doc = LyricAdapters.FromRecords(records, "TTML");
        Assert.Equal(100, doc.Lines[0].Words[0].StartTimeMs);
    }

    [Fact]
    public void EndTimeLink_Has_No_Record_Counterpart()
    {
        var doc = new EditDocument();
        var line = EditLine.New();
        line.EndTimeLink = new EditEndTimeLink(100, 200);
        doc.Lines.Add(line);
        var back = LyricAdapters.ToRecords(doc);
        Assert.Null(LyricAdapters.FromRecords(back, "TTML").Lines[0].EndTimeLink);
    }
}
