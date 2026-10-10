using AmllTtmlTool.Core;
using Xunit;

namespace AmllTtmlTool.Core.Tests;

public sealed class DropFilesTests
{
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
        { "mp3", "flac", "m4a", "ec3", "opus", "wav" };

    private static readonly HashSet<string> Lyrics = new(StringComparer.OrdinalIgnoreCase)
        { "ttml", "lrc" };

    [Fact]
    public void ClassifyEmpty() =>
        Assert.Equal(DropKind.None, DropFiles.Classify([], Audio, Lyrics));

    [Theory]
    [InlineData(new[] { "song.ttml" }, DropKind.Lyrics)]
    [InlineData(new[] { "song.LRC" }, DropKind.Lyrics)]
    [InlineData(new[] { "a.flac", "b.mp3" }, DropKind.Audio)]
    [InlineData(new[] { "a.m4a", "b.lrc" }, DropKind.Both)]
    [InlineData(new[] { "notes.txt" }, DropKind.Unsupported)]
    [InlineData(new[] { "noext" }, DropKind.Unsupported)]
    [InlineData(new[] { "a.mp3", "readme.txt" }, DropKind.Audio)]
    [InlineData(new[] { "a.ttml", "readme.txt" }, DropKind.Lyrics)]
    [InlineData(new[] { "a.mp3", "b.lrc", "c.xyz" }, DropKind.Both)]
    [InlineData(new[] { "track.EC3" }, DropKind.Audio)]
    public void Classify(string[] names, DropKind expected) =>
        Assert.Equal(expected, DropFiles.Classify(names, Audio, Lyrics));

    [Theory]
    [InlineData(DropKind.Lyrics, "Drop to open lyrics")]
    [InlineData(DropKind.Audio, "Drop to open audio")]
    [InlineData(DropKind.Both, "Drop to open lyrics + audio")]
    [InlineData(DropKind.Unsupported, "Unsupported file")]
    [InlineData(DropKind.None, "Drop to open")]
    public void Caption(DropKind kind, string expected) =>
        Assert.Equal(expected, DropFiles.Caption(kind));
}
