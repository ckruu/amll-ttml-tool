using AmllTtmlTool.Core.Audio;
using Xunit;

namespace AmllTtmlTool.Core.Tests;

public sealed class SmtcDisplayTests
{
    private static AudioTrackMetadata Meta(
        string[]? titles = null, string[]? artists = null,
        string[]? albums = null, string fileName = "track01") =>
        new([.. titles ?? []], [.. artists ?? []], [.. albums ?? []],
            [], [], fileName);

    [Fact]
    public void TagTitle_WinsOverFileName()
    {
        var (title, artist, album) = SmtcDisplay.ForTrack(
            Meta(["Song"], ["Singer"], ["Record"], "file"));
        Assert.Equal("Song", title);
        Assert.Equal("Singer", artist);
        Assert.Equal("Record", album);
    }

    [Fact]
    public void BlankTitle_FallsBackToFileName()
    {
        var (title, _, _) = SmtcDisplay.ForTrack(Meta(["  "], fileName: "alac"));
        Assert.Equal("alac", title);
    }

    [Fact]
    public void NoTitleNoName_GivesUnknownTrack()
    {
        var (title, _, _) = SmtcDisplay.ForTrack(Meta(fileName: ""));
        Assert.Equal("Unknown track", title);
    }

    [Fact]
    public void MultipleArtists_Joined()
    {
        var (_, artist, _) = SmtcDisplay.ForTrack(Meta(artists: ["A", "B"]));
        Assert.Equal("A, B", artist);
    }

    [Fact]
    public void NoArtistNoAlbum_EmptyStrings()
    {
        var (_, artist, album) = SmtcDisplay.ForTrack(Meta(["T"]));
        Assert.Equal("", artist);
        Assert.Equal("", album);
    }
}
