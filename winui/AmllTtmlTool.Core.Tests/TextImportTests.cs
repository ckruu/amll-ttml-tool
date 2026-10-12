using AmllTtmlTool.Core.Lyrics.Import;

namespace AmllTtmlTool.Core.Tests;

// Exact port of ImportFromText.tsx rules: modes, separators, prefixes,
// empty beats, swap, blank lines, \r retention, interleave overrun.
public sealed class TextImportTests
{
    [Fact]
    public void Lyric_One_Word_Per_Line()
    {
        var lines = TextImporter.Import("hello\nworld\n");
        Assert.Equal(3, lines.Count); // trailing \n yields a blank line
        Assert.Equal("hello", lines[0].Words[0].Text);
        Assert.Equal("world", lines[1].Words[0].Text);
        Assert.Equal("", lines[2].Words[0].Text);
        Assert.All(lines, l => Assert.Single(l.Words));
    }

    [Fact]
    public void Blank_Lines_Produce_Blank_Lines_Cr_Retained()
    {
        var lines = TextImporter.Import("a\r\n\nb");
        Assert.Equal(3, lines.Count);
        Assert.Equal("a\r", lines[0].Words[0].Text); // \n-only split
        Assert.Equal("", lines[1].Words[0].Text);
        Assert.Equal("b", lines[2].Words[0].Text);
    }

    [Fact]
    public void Interleaved_Trans_Roman()
    {
        var lines = TextImporter.Import("orig\ntrans\nroman\nnext\nt2\nr2\n",
            new TextImportOptions(Mode: TextImportMode.LyricTransRoman));
        // Trailing \n leaves a final "" group -> a third blank line.
        Assert.Equal(3, lines.Count);
        Assert.Equal("orig", lines[0].Words[0].Text);
        Assert.Equal("trans", lines[0].TranslatedLyric);
        Assert.Equal("roman", lines[0].RomanLyric);
        Assert.Equal("next", lines[1].Words[0].Text);
        Assert.Equal("", lines[2].Words[0].Text);
    }

    [Fact]
    public void Interleaved_Overrun_Yields_Empty_Subs()
    {
        var lines = TextImporter.Import("orig\ntrans",
            new TextImportOptions(Mode: TextImportMode.LyricTransRoman));
        Assert.Single(lines);
        Assert.Equal("orig", lines[0].Words[0].Text);
        Assert.Equal("trans", lines[0].TranslatedLyric);
        Assert.Equal("", lines[0].RomanLyric);
    }

    [Fact]
    public void SameLine_Separator_Missing_Parts_Empty()
    {
        var lines = TextImporter.Import("o|t|r\nbare\n",
            new TextImportOptions(
                Mode: TextImportMode.LyricTransRoman,
                SeparatorMode: TextLineSeparatorMode.SameLineSeparator));
        Assert.Equal(3, lines.Count);
        Assert.Equal("o", lines[0].Words[0].Text);
        Assert.Equal("t", lines[0].TranslatedLyric);
        Assert.Equal("r", lines[0].RomanLyric);
        Assert.Equal("bare", lines[1].Words[0].Text);
        Assert.Equal("", lines[1].TranslatedLyric);
        Assert.Equal("", lines[2].Words[0].Text); // trailing \n blank line
    }

    [Fact]
    public void Swap_Trans_And_Roman()
    {
        var lines = TextImporter.Import("o|t|r",
            new TextImportOptions(
                Mode: TextImportMode.LyricTransRoman,
                SeparatorMode: TextLineSeparatorMode.SameLineSeparator,
                SwapTransAndRoman: true));
        Assert.Equal("r", lines[0].TranslatedLyric);
        Assert.Equal("t", lines[0].RomanLyric);
    }

    [Fact]
    public void Prefixes_Stripped_Repeatedly_In_Any_Order()
    {
        var lines = TextImporter.Import("><bg\n<>x\nplain",
            new TextImportOptions(EnableSpecialPrefix: true));
        Assert.True(lines[0].IsBg);
        Assert.True(lines[0].IsDuet);
        Assert.Equal("bg", lines[0].Words[0].Text);
        Assert.True(lines[1].IsBg);
        Assert.True(lines[1].IsDuet); // "<" then ">" both strip
        Assert.Equal("x", lines[1].Words[0].Text);
        Assert.False(lines[2].IsBg);
    }

    [Fact]
    public void Prefixes_Disabled_By_Default()
    {
        var lines = TextImporter.Import("<bg");
        Assert.False(lines[0].IsBg);
        Assert.Equal("<bg", lines[0].Words[0].Text);
    }

    [Fact]
    public void Word_Separator_Splits()
    {
        var lines = TextImporter.Import("a\\b\\c",
            new TextImportOptions(WordSeparator: "\\"));
        Assert.Equal(new[] { "a", "b", "c" },
            lines[0].Words.Select(w => w.Text).ToArray());
    }

    [Fact]
    public void EmptyBeat_Suffix_Counts()
    {
        var lines = TextImporter.Import("la^^^\nx",
            new TextImportOptions(EnableEmptyBeat: true));
        Assert.Equal("la", lines[0].Words[0].Text);
        Assert.Equal(3, lines[0].Words[0].EmptyBeat);
        Assert.Equal(0, lines[1].Words[0].EmptyBeat);
    }

    [Fact]
    public void Metadata_Empty_Lines_Fresh_Ids()
    {
        var lines = TextImporter.Import("a\nb");
        Assert.Equal(2, lines.Count);
        Assert.NotEqual(lines[0].Id, lines[1].Id);
        Assert.NotEqual(lines[0].Words[0].Id, lines[1].Words[0].Id);
        Assert.False(lines[0].IgnoreSync);
    }
}
