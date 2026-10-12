using AmllTtmlTool.Core.Lyrics.Import;

namespace AmllTtmlTool.Core.Tests;

public sealed class RegistryTests
{
    private static LyricImporterRegistry Default() => LyricImporterRegistry.Default();

    [Fact]
    public void Extension_Routes_To_Importer()
    {
        Assert.IsType<TtmlImporter>(Default().ByExtension("song.ttml"));
        Assert.IsType<TtmlImporter>(Default().ByExtension(".TTML"));
        Assert.IsType<LrcImporter>(Default().ByExtension("song.lrc"));
        Assert.IsType<PlainTextImporter>(Default().ByExtension("notes.txt"));
    }

    [Fact]
    public void Unknown_Extension_Throws()
    {
        Assert.Throws<LyricImportException>(() => Default().ByExtension("song.qrc"));
    }

    [Fact]
    public void Sniff_Order_Ttml_Lrc_Text()
    {
        Assert.IsType<TtmlImporter>(Default().Sniff("<tt xmlns=\"http://www.w3.org/ns/ttml\">"));
        Assert.IsType<LrcImporter>(Default().Sniff("[00:01.00]hello\n"));
        Assert.IsType<PlainTextImporter>(Default().Sniff("just some words\n"));
    }

    [Fact]
    public void Ttml_Pipeline_End_To_End()
    {
        const string xml = "<tt xmlns=\"http://www.w3.org/ns/ttml\">" +
            "<head><metadata></metadata></head><body><div>" +
            "<p begin=\"0.000\" end=\"1.000\">" +
            "<span begin=\"0.000\" end=\"1.000\">hi</span></p>" +
            "</div></body></tt>";
        var result = Default().ImportFile("a.ttml", xml);
        Assert.Single(result.Document.Lines);
        Assert.Equal("hi", result.Document.Lines[0].Words[0].Text);
        Assert.Equal(1000, result.Document.Lines[0].Words[0].EndTimeMs);
        Assert.False(result.Document.IsDirty);
    }

    [Fact]
    public void Ttml_Failure_Throws_Without_Document()
    {
        Assert.Throws<LyricImportException>(() =>
            Default().ImportFile("a.ttml", "<tt><body><div><p begin="));
    }

    [Fact]
    public void Lrc_Pipeline_End_To_End()
    {
        var result = Default().ImportFile("a.lrc", "[00:01.00]hi\n");
        Assert.Single(result.Document.Lines);
        Assert.Equal("hi", result.Document.Lines[0].Words[0].Text);
        Assert.Empty(result.Document.Metadata);
    }

    [Fact]
    public void Text_Pipeline_End_To_End()
    {
        var result = Default().ImportFile("a.txt", "hello\nworld\n");
        Assert.Equal(3, result.Document.Lines.Count);
    }

    [Fact]
    public void Custom_Importer_Plugs_In_Later_Phases()
    {
        var registry = Default();
        var fake = new FakeYrcImporter();
        registry.Register(fake);
        Assert.Same(fake, registry.ByExtension("song.yrc"));
    }

    private sealed class FakeYrcImporter : ILyricImporter
    {
        public string FormatId => "yrc";
        public IReadOnlyList<string> Extensions => [".yrc"];
        public bool CanSniff(string text) => text.StartsWith("[1,");
        public LyricImportResult Import(string text, string sourceName) =>
            new(new AmllTtmlTool.Core.Lyrics.EditDocument(), []);
    }
}
