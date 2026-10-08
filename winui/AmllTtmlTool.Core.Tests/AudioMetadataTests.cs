using AmllTtmlTool.Core.Audio;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

public sealed class AudioMetadataTests
{
    [Fact]
    public void SplitMetadataValue_SplitsAndDedupes()
    {
        Assert.Equal(["A", "B", "C"],
            AudioMetadata.SplitMetadataValue("A, B;A / B，C；C、C|A\\A"));
        Assert.Empty(AudioMetadata.SplitMetadataValue(null));
        Assert.Empty(AudioMetadata.SplitMetadataValue("   "));
    }

    [Fact]
    public void ParseAudioTrackMetadata_MapsKeys()
    {
        var raw = new Dictionary<string, string>
        {
            ["TITLE"] = "Song",
            ["Artist"] = "A; B",
            ["albumTitle"] = "Alb",
            ["Composer"] = "C",
            ["ISRC"] = "X",
            ["genre"] = "Pop",
        };
        var meta = AudioMetadata.ParseAudioTrackMetadata(raw, "track01.mp3");
        Assert.Equal(["Song"], meta.Titles);
        Assert.Equal(["A", "B"], meta.Artists);
        Assert.Equal(["Alb"], meta.Albums);
        Assert.Equal(["C"], meta.Composers);
        Assert.Equal(["X"], meta.Isrcs);
        Assert.Equal("track01", meta.FileName);
    }

    [Fact]
    public void ToTtmlMetadata_MapsToLyricKeys()
    {
        var meta = AudioMetadata.ParseAudioTrackMetadata(
            new Dictionary<string, string> { ["title"] = "S", ["artist"] = "A" });
        var ttml = AudioMetadata.ToTtmlMetadata(meta);
        Assert.Equal(["musicName", "artists"], ttml.Select(m => m.Key).ToList());
    }

    [Fact]
    public void MergeExtractedMetadata_FillsEmptyOnly()
    {
        var current = new List<TtmlMetadata>
        {
            new("musicName", ["Keep"]),
            new("artists", ["  "]),
        };
        var changed = AudioMetadata.MergeExtractedMetadata(current,
            [new("musicName", ["New"]), new("artists", ["A"]), new("album", ["Al"])]);
        Assert.True(changed);
        Assert.Equal(["Keep"], current.First(m => m.Key == "musicName").Value);
        Assert.Equal(["A"], current.First(m => m.Key == "artists").Value);
        Assert.Equal(["Al"], current.First(m => m.Key == "album").Value);
    }

    [Fact]
    public void MergeExtractedMetadata_IgnoresBlankExtracts()
    {
        var current = new List<TtmlMetadata>();
        Assert.False(AudioMetadata.MergeExtractedMetadata(current, [new("album", ["  "])]));
        Assert.Empty(current);
    }
}
