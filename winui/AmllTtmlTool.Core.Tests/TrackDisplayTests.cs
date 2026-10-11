using AmllTtmlTool.Core.Audio;

namespace AmllTtmlTool.Core.Tests;

public sealed class TrackDisplayTests
{
    private static AudioTrackMetadata Meta(
        string[]? titles = null,
        string[]? artists = null,
        string fileName = "track01") =>
        new([.. titles ?? []], [.. artists ?? []], [], [], [], fileName);

    [Fact]
    public void Title_Prefers_Tag_Then_FileName()
    {
        Assert.Equal("Song", TrackDisplay.TitleFor(Meta(["Song"], ["A"])));
        Assert.Equal("track01", TrackDisplay.TitleFor(Meta([], ["A"])));
    }

    [Fact]
    public void Artist_Falls_Back_To_Unknown()
    {
        Assert.Equal("A", TrackDisplay.ArtistOrUnknown(Meta(["S"], ["A"])));
        Assert.Equal("Unknown", TrackDisplay.ArtistOrUnknown(Meta(["S"], [])));
    }

    [Theory]
    [InlineData("alac", "ALAC")]
    [InlineData("ALAC", "ALAC")]
    [InlineData("flac", "FLAC")]
    [InlineData("eac3", "E-AC-3")]
    [InlineData("ec3", "E-AC-3")]
    [InlineData("ac3", "AC-3")]
    [InlineData("truehd", "TrueHD")]
    [InlineData("aac", "AAC")]
    [InlineData("mp3", "MP3")]
    [InlineData("opus", "Opus")]
    [InlineData("vorbis", "Vorbis")]
    [InlineData("pcm_s16le", "PCM")]
    [InlineData("pcm_f32le", "PCM")]
    [InlineData("wmav2", "WMA")]
    [InlineData("dts", "DTS")]
    [InlineData("mlp", "MLP")]
    [InlineData(null, "?")]
    [InlineData("", "?")]
    public void CodecLabel_Maps_Friendly_Names(string? probe, string want) =>
        Assert.Equal(want, TrackDisplay.CodecLabel(probe));

    [Fact]
    public void Line2_Formats_Artist_Container_Codec()
    {
        Assert.Equal("Petit Rabbit's - M4A ALAC",
            TrackDisplay.Line2("Petit Rabbit's", "m4a", "alac"));
        Assert.Equal("Unknown - FLAC FLAC",
            TrackDisplay.Line2("Unknown", ".flac", "flac"));
    }
}
