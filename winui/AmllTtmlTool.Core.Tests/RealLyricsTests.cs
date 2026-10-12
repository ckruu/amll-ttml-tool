using System.Text.Json;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

// Real files from D:\Lyrics (READ-ONLY input, copied to temp first — the
// tests never touch the originals). Skipped where the folder is absent.
// Comparison target: the committed real-*.wasm.json goldens produced by
// Tools/generate-ttml-goldens.mjs from the same files.
public sealed class RealLyricsTests
{
    private static readonly string Fixtures =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static string? Stage(string fileName)
    {
        // No Skip API in xunit 2.5: absent library -> vacuous pass.
        if (!Directory.Exists("D:\\Lyrics")) return null;
        var src = Path.Combine("D:\\Lyrics", fileName);
        if (!File.Exists(src)) return null;
        var dir = Path.Combine(Path.GetTempPath(), "amll-lyric-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dst = Path.Combine(dir, fileName);
        File.Copy(src, dst);
        return dst;
    }

    private static void AssertMatchesWasmJson(TtmlLyric records, string goldenName)
    {
        using var wasm = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Fixtures, goldenName)));
        var root = wasm.RootElement;
        Assert.Empty(root.GetProperty("metadata").EnumerateArray().ToList());
        var wlines = root.GetProperty("lyricLines").EnumerateArray().ToList();
        Assert.Equal(wlines.Count, records.LyricLines.Count);
        for (var i = 0; i < wlines.Count; i++)
            WasmParityTests.AssertLine(wlines[i], records.LyricLines[i]);
    }

    [Fact]
    public void Apple_Ttml_Parses_And_Matches_Wasm()
    {
        var staged = Stage("apple_No Poi!.ttml");
        if (staged is null) return;
        var xml = File.ReadAllText(staged);
        var parsed = TtmlParser.Parse(xml);
        Assert.True(parsed.Success);
        var repaired = LegacyTtml.RepairLegacyImport(parsed.Data!, xml).Result;
        var records = TtmlNormalize.ApplyImportRenames(repaired);
        Assert.Equal(84, records.LyricLines.Count);
        AssertMatchesWasmJson(records, "real-apple.wasm.json");
    }

    [Fact]
    public void Broken_Ttml_Parses_Is_Legacy_And_Matches_Wasm()
    {
        var staged = Stage("broken_Down.ttml");
        if (staged is null) return;
        var xml = File.ReadAllText(staged);
        var legacy = LegacyTtml.DetectLegacyTtml(xml);
        Assert.True(legacy.IsLegacy);
        Assert.Contains("itunes:key attributes", legacy.Reasons);
        var parsed = TtmlParser.Parse(xml);
        Assert.True(parsed.Success);
        var repaired = LegacyTtml.RepairLegacyImport(parsed.Data!, xml).Result;
        var records = TtmlNormalize.ApplyImportRenames(repaired);
        Assert.Equal(88, records.LyricLines.Count);
        AssertMatchesWasmJson(records, "real-broken.wasm.json");
    }
}
