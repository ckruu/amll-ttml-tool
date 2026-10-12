using System.Diagnostics;

namespace AmllTtmlTool.Core.Tests;

// Error and robustness tests: malformed XML, wrong namespace, empty input,
// BOM, and a 10k-line stress (speed + memory). WASM reference behaviors
// were probed live (probe-err.mjs): wrong/no namespace -> OK (namespace
// blind, like C#); empty/whitespace/non-XML -> OK empty; truncated ->
// WASM UnexpectedEof (C# is lenient on open-tags-only input by design,
// locked by Parse_GarbageSucceedsEmpty); BOM -> OK.
public sealed class TtmlErrorTests
{
    private const string Ns =
        "xmlns=\"http://www.w3.org/ns/ttml\" " +
        "xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" " +
        "xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\"";

    private static string Doc(string inner) =>
        $"<tt {Ns}><head><metadata></metadata></head><body><div>{inner}</div></body></tt>";

    private static string Good() => Doc(
        "<p begin=\"00:00:01.000\" end=\"00:00:02.000\">" +
        "<span begin=\"00:00:01.000\" end=\"00:00:02.000\">x</span></p>");

    [Fact]
    public void Wrong_Namespace_Parses_Namespace_Blind()
    {
        var xml = Good().Replace("http://www.w3.org/ns/ttml", "http://wrong.example/ns");
        var result = TtmlParser.Parse(xml);
        Assert.True(result.Success);
        Assert.Single(result.Data!.LyricLines);
    }

    [Fact]
    public void Missing_Namespace_Parses()
    {
        var result = TtmlParser.Parse(
            "<tt><body><div><p begin=\"0.000\" end=\"1.000\">" +
            "<span begin=\"0.000\" end=\"1.000\">x</span></p></div></body></tt>");
        Assert.True(result.Success);
        Assert.Single(result.Data!.LyricLines);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    public void Empty_Or_NonXml_Yields_Empty_Document(string input)
    {
        var result = TtmlParser.Parse(input);
        Assert.True(result.Success);
        Assert.Empty(result.Data!.LyricLines);
        Assert.Empty(result.Data.Metadata);
    }

    [Fact]
    public void Leading_Bom_Is_Tolerated()
    {
        var result = TtmlParser.Parse("﻿" + Good());
        Assert.True(result.Success);
        Assert.Single(result.Data!.LyricLines);
        Assert.Equal("x", result.Data.LyricLines[0].Words[0].Word);
    }

    [Fact]
    public void Huge_Document_Parses_And_Generates_Within_Budget()
    {
        const int lines = 10000;
        var sb = new System.Text.StringBuilder();
        sb.Append($"<tt {Ns}><head><metadata></metadata></head><body><div>");
        for (var i = 0; i < lines; i++)
        {
            sb.Append($"<p begin=\"{i}.000\" end=\"{i + 1}.000\" itunes:key=\"L{i + 1}\">");
            sb.Append($"<span begin=\"{i}.000\" end=\"{i}.250\">a</span>");
            sb.Append($"<span begin=\"{i}.250\" end=\"{i}.500\">b</span>");
            sb.Append($"<span begin=\"{i}.500\" end=\"{i + 1}.000\">c</span></p>");
        }
        sb.Append("</div></body></tt>");
        var xml = sb.ToString();

        GC.Collect();
        var memBefore = GC.GetTotalMemory(true);
        var sw = Stopwatch.StartNew();
        var parsed = TtmlParser.Parse(xml);
        var parseMs = sw.ElapsedMilliseconds;
        Assert.True(parsed.Success);
        Assert.Equal(lines, parsed.Data!.LyricLines.Count);
        var gen = TtmlGenerator.Generate(parsed.Data, new(), new(false, false, false));
        var totalMs = sw.ElapsedMilliseconds;
        Assert.True(gen.Success);
        var back = TtmlParser.Parse(gen.Data!);
        Assert.True(back.Success);
        Assert.Equal(lines, back.Data!.LyricLines.Count);
        var memAfter = GC.GetTotalMemory(false);
        var memMB = (memAfter - memBefore) / (1024.0 * 1024.0);
        Assert.True(parseMs < 15000, $"parse took {parseMs}ms");
        Assert.True(totalMs < 30000, $"parse+generate took {totalMs}ms");
        Assert.True(memMB < 300, $"memory delta {memMB:F1}MB");
    }
}
