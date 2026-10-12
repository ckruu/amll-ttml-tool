using AmllTtmlTool.Core;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

public sealed class TtmlParserTests
{
    private const string Ns =
        "xmlns=\"http://www.w3.org/ns/ttml\" " +
        "xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" " +
        "xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" " +
        "xmlns:tts=\"http://www.w3.org/ns/ttml#styling\" " +
        "xmlns:amll=\"http://www.example.com/ns/amll\"";

    private static string Doc(string inner, string head = "") =>
        $"<tt {Ns} xml:lang=\"en\"><head><metadata>{head}</metadata></head><body><div>{inner}</div></body></tt>";

    private static TtmlLyric ParseOk(string xml, TtmlParseOptions? opts = null)
    {
        var result = TtmlParser.Parse(xml, opts);
        Assert.True(result.Success);
        return result.Data!;
    }

    [Fact]
    public void Parse_SimpleLine()
    {
        var lyric = ParseOk(Doc(
            "<p begin=\"00:00:01.000\" end=\"00:00:02.000\">" +
            "<span begin=\"00:00:01.000\" end=\"00:00:01.500\">Hello</span>" +
            "<span begin=\"00:00:01.600\" end=\"00:00:02.000\">world</span></p>"));
        Assert.Single(lyric.LyricLines);
        var line = lyric.LyricLines[0];
        Assert.Equal(1000, line.StartTime);
        Assert.Equal(2000, line.EndTime);
        Assert.False(line.IsBg);
        Assert.False(line.IsDuet);
        Assert.Equal(["Hello", "world"], line.Words.Select(w => w.Word).ToList());
        Assert.Equal([1000, 1600], line.Words.Select(w => w.StartTime).ToList());
    }

    [Fact]
    public void Parse_TimestampForms()
    {
        var lyric = ParseOk(Doc(
            "<p begin=\"90.5\" end=\"1:02:03.004\">" +
            "<span begin=\"1000\" end=\"1:02.500\">x</span></p>"));
        Assert.Equal(90500, lyric.LyricLines[0].StartTime);
        Assert.Equal(3723004, lyric.LyricLines[0].EndTime);
        Assert.Equal(1000000, lyric.LyricLines[0].Words[0].StartTime);
        Assert.Equal(62500, lyric.LyricLines[0].Words[0].EndTime);
    }

    [Fact]
    public void Parse_MetadataMappingAndOrder()
    {
        var lyric = ParseOk(Doc(
            "<p begin=\"0.000\" end=\"1.000\"><span begin=\"0.000\" end=\"1.000\">x</span></p>",
            "<amll:meta key=\"musicName\" value=\"S\"/>" +
            "<amll:meta key=\"artists\" value=\"A\"/>" +
            "<amll:meta key=\"artists\" value=\"B\"/>" +
            "<amll:meta key=\"songwriters\" value=\"W\"/>" +
            "<amll:meta key=\"isrc\" value=\"X\"/>" +
            "<amll:meta key=\"ncmMusicId\" value=\"123\"/>"));
        var keys = lyric.Metadata.Select(m => m.Key).ToList();
        Assert.Equal(["musicName", "artists", "isrc", "ncmMusicId", "songwriter"], keys);
        Assert.Equal(["A", "B"],
            lyric.Metadata.First(m => m.Key == "artists").Value);
    }

    [Fact]
    public void Parse_BgSplitAndParenStrip()
    {
        var lyric = ParseOk(Doc(
            "<p begin=\"1.000\" end=\"3.000\" itunes:key=\"L1\" ttm:agent=\"v1\">" +
            "<span begin=\"1.000\" end=\"2.000\">main</span>" +
            "<span ttm:role=\"x-bg\" begin=\"2.000\" end=\"3.000\">" +
            "<span begin=\"2.000\" end=\"3.000\">(bg)</span></span>" +
            "<span ttm:role=\"x-roman\">ro</span></p>"));
        Assert.Equal(2, lyric.LyricLines.Count);
        Assert.Equal("ro", lyric.LyricLines[0].RomanLyric);
        var bg = lyric.LyricLines[1];
        Assert.True(bg.IsBg);
        Assert.Equal("bg", bg.Words[0].Word);
        Assert.Equal(2000, bg.StartTime);
        Assert.Equal(3000, bg.EndTime);
    }

    [Fact]
    public void Parse_RubyObsceneEmptyBeat()
    {
        var lyric = ParseOk(Doc(
            "<p begin=\"1.000\" end=\"3.000\">" +
            "<span tts:ruby=\"container\">" +
            "<span tts:ruby=\"base\">Kan</span>" +
            "<span tts:ruby=\"textContainer\">" +
            "<span tts:ruby=\"text\" begin=\"1.000\" end=\"2.000\">ka</span>" +
            "</span></span>" +
            "<span begin=\"2.000\" end=\"3.000\" amll:empty-beat=\"1\" amll:obscene=\"true\">ji</span></p>"));
        var words = lyric.LyricLines[0].Words;
        Assert.Equal("Kan", words[0].Word);
        Assert.Single(words[0].Ruby!);
        Assert.Equal("ka", words[0].Ruby![0].Word);
        Assert.True(words[1].Obscene);
        Assert.Equal(1, words[1].EmptyBeat);
    }

    [Fact]
    public void Parse_Translations_SelectByOptionElseFirst()
    {
        const string inner =
            "<p begin=\"0.000\" end=\"1.000\"><span begin=\"0.000\" end=\"1.000\">a</span>" +
            "<span ttm:role=\"x-translation\" xml:lang=\"fr\">F</span>" +
            "<span ttm:role=\"x-translation\" xml:lang=\"de\">G</span></p>";
        Assert.Equal("F", ParseOk(Doc(inner)).LyricLines[0].TranslatedLyric);
        Assert.Equal("G", ParseOk(Doc(inner), new("de", null)).LyricLines[0].TranslatedLyric);
        Assert.Equal("F", ParseOk(Doc(inner), new("JA", null)).LyricLines[0].TranslatedLyric);
    }

    // Duet baseline (WASM-probed): a line is duet iff its agent differs
    // from the FIRST <p>'s agent in document order — not "== v2".
    // Single-line docs therefore never parse as duet (repair restores the
    // first-line flag on import when its agent is non-v1).
    [Fact]
    public void Parse_DuetBaselineIsFirstLineAgent()
    {
        string Line(string agentAttr, string word) =>
            $"<p begin=\"0.000\" end=\"1.000\"{agentAttr}>" +
            $"<span begin=\"0.000\" end=\"1.000\">{word}</span></p>";
        // Single line: own agent is the baseline -> never duet.
        foreach (var agent in new[] { "v2", "v1", "V2", "v3", "" })
        {
            var lyric = ParseOk(Doc(Line($" ttm:agent=\"{agent}\"", "a")));
            Assert.False(lyric.LyricLines[0].IsDuet);
        }
        var missing = ParseOk(Doc(Line("", "a")));
        Assert.False(missing.LyricLines[0].IsDuet);
        // First line sets the baseline; later lines differ -> duet.
        var two = ParseOk(Doc(
            Line(" ttm:agent=\"v2\"", "a") + Line(" ttm:agent=\"v1\"", "b")));
        Assert.False(two.LyricLines[0].IsDuet);
        Assert.True(two.LyricLines[1].IsDuet);
        var custom = ParseOk(Doc(
            Line(" ttm:agent=\"v3\"", "a") + Line(" ttm:agent=\"v1\"", "b")));
        Assert.False(custom.LyricLines[0].IsDuet);
        Assert.True(custom.LyricLines[1].IsDuet);
        // A missing agent defaults to "v1" (WASM-probed); explicitly empty
        // stays distinct.
        var missingFirst = ParseOk(Doc(Line("", "a") + Line(" ttm:agent=\"v2\"", "b")));
        Assert.False(missingFirst.LyricLines[0].IsDuet);
        Assert.True(missingFirst.LyricLines[1].IsDuet);
        var missingSecond = ParseOk(Doc(Line(" ttm:agent=\"v1\"", "a") + Line("", "b")));
        Assert.False(missingSecond.LyricLines[0].IsDuet);
        Assert.False(missingSecond.LyricLines[1].IsDuet);
        var emptyFirst = ParseOk(Doc(Line(" ttm:agent=\"\"", "a") + Line("", "b")));
        Assert.False(emptyFirst.LyricLines[0].IsDuet);
        Assert.True(emptyFirst.LyricLines[1].IsDuet);
    }

    [Fact]
    public void Parse_TextNodeRules()
    {
        var lyric = ParseOk(Doc(
            "<p begin=\"0.000\" end=\"2.000\">hey " +
            "<span begin=\"0.000\" end=\"1.000\">A</span> tail " +
            "<span begin=\"1.000\" end=\"2.000\">B</span> end</p>"));
        Assert.Equal(["A", " ", "B"], lyric.LyricLines[0].Words.Select(w => w.Word).ToList());
    }

    [Fact]
    public void Parse_EmptyParagraphYieldsEmptyWord()
    {
        var lyric = ParseOk(Doc("<p begin=\"5.000\" end=\"7.000\"/>"));
        Assert.Single(lyric.LyricLines);
        Assert.Single(lyric.LyricLines[0].Words);
        Assert.Equal(string.Empty, lyric.LyricLines[0].Words[0].Word);
    }

    [Fact]
    public void Parse_GarbageSucceedsEmpty()
    {
        foreach (var input in new[] { "hello world", "<tt><oops>", string.Empty, "   " })
        {
            var result = TtmlParser.Parse(input);
            Assert.True(result.Success);
            Assert.Empty(result.Data!.LyricLines);
        }
    }

    [Fact]
    public void Parse_MalformedFails()
    {
        foreach (var input in new[] { "<tt><oops", "<tt></div>", "a < b" })
        {
            var result = TtmlParser.Parse(input);
            Assert.False(result.Success);
            Assert.Equal("XmlError", result.Error!.Kind);
        }
    }

    [Fact]
    public void Parse_MissingBeginFails()
    {
        var result = TtmlParser.Parse(Doc("<p><span>x</span></p>"));
        Assert.False(result.Success);
        Assert.Equal("MissingAttribute", result.Error!.Kind);
        Assert.Equal(["tt", "body", "div", "p"], result.Error.TagStack);
    }

    [Fact]
    public void Parse_InvalidTimestampFails()
    {
        var result = TtmlParser.Parse(Doc(
            "<p begin=\"abc\" end=\"1.000\"><span begin=\"0.000\" end=\"1.000\">x</span></p>"));
        Assert.False(result.Success);
        Assert.Equal("InvalidTimestamp", result.Error!.Kind);
        Assert.Equal("begin", result.Error.CurrentAttribute);
        Assert.Equal("abc", result.Error.OffendingString);
    }

    [Fact]
    public void Parse_MalformedXmlFails()
    {
        var result = TtmlParser.Parse("<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=");
        Assert.False(result.Success);
        Assert.Equal("XmlError", result.Error!.Kind);
    }
}
