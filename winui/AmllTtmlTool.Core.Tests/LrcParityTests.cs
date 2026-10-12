using System.Text.Json;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

// Parity against the ORIGINAL local parser (src/utils/parse-lrc.ts) run in
// node over the same inputs (Tools/generate-lrc-goldens.mjs). Goldens carry
// no ids (uid-counter vs Guids differ by construction); everything else
// must match field-for-field, including +10s last-line ends, translation /
// roman / extra-line splitting, and ignored offset/metadata tags.
public sealed class LrcParityTests
{
    private static readonly string Fixtures =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static void AssertMatchesGolden(List<LyricLine> lines, string goldenName)
    {
        using var json = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Fixtures, goldenName)));
        var expected = json.RootElement.EnumerateArray().ToList();
        Assert.Equal(expected.Count, lines.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var line = lines[i];
            Assert.Equal(e.GetProperty("startTime").GetDouble(), line.StartTime);
            Assert.Equal(e.GetProperty("endTime").GetDouble(), line.EndTime);
            Assert.Single(line.Words);
            Assert.Equal(e.GetProperty("word").GetString(), line.Words[0].Word);
            Assert.Equal(e.GetProperty("translatedLyric").GetString(), line.TranslatedLyric);
            Assert.Equal(e.GetProperty("romanLyric").GetString(), line.RomanLyric);
            Assert.False(line.IsBg);
            Assert.False(line.IsDuet);
        }
    }

    [Fact]
    public void Multi_Matches_Node_Local_Parser()
    {
        var text = File.ReadAllText(Path.Combine(Fixtures, "lrc-multi.lrc"));
        AssertMatchesGolden(LrcParser.ParseLrc(text), "lrc-multi.lrc.json");
    }

    [Fact]
    public void Real_Lrc_Matches_Node_Local_Parser()
    {
        const string lib = "D:\\Lyrics\\lrc_Wakamonono Subete.lrc";
        if (!File.Exists(lib)) return;
        var dir = Path.Combine(Path.GetTempPath(), "amll-lyric-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var staged = Path.Combine(dir, "real.lrc");
        File.Copy(lib, staged);
        AssertMatchesGolden(LrcParser.ParseLrc(File.ReadAllText(staged)), "lrc-real.lrc.json");
    }

    [Fact]
    public void Offset_And_Metadata_Tags_Ignored()
    {
        var lines = LrcParser.ParseLrc("[offset:+500]\n[ar:Artist]\n[00:01.00]hi\n");
        Assert.Single(lines);
        Assert.Equal("hi", lines[0].Words[0].Word);
        Assert.Equal(1000, lines[0].StartTime);
    }

    [Fact]
    public void Unicode_Digit_Tags_Ignored_Not_Crash()
    {
        // .NET \d matches non-ASCII digits where the original's [0-9]
        // finds no tag; the line must be ignored, never throw.
        var lines = LrcParser.ParseLrc("[١٢:٣٤]hi\n[00:01.00]ok\n");
        Assert.Single(lines);
        Assert.Equal("ok", lines[0].Words[0].Word);
    }

    [Fact]
    public void Fraction_Digits_Are_Decimal()
    {
        var lines = LrcParser.ParseLrc("[00:01.1]a\n[00:02.12]b\n[00:03.123]c\n");
        Assert.Equal(1100, lines[0].StartTime);
        Assert.Equal(2120, lines[1].StartTime);
        Assert.Equal(3123, lines[2].StartTime);
    }
}
