using AmllTtmlTool.Core;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

public static class LyricAssert
{
    public static void EqualLyrics(TtmlLyric expected, TtmlLyric actual)
    {
        Assert.Equal(expected.Metadata.Count, actual.Metadata.Count);
        for (var i = 0; i < expected.Metadata.Count; i++)
        {
            Assert.Equal(expected.Metadata[i].Key, actual.Metadata[i].Key);
            Assert.Equal(expected.Metadata[i].Value, actual.Metadata[i].Value);
        }
        Assert.Equal(expected.LyricLines.Count, actual.LyricLines.Count);
        for (var i = 0; i < expected.LyricLines.Count; i++)
            EqualLine(expected.LyricLines[i], actual.LyricLines[i]);
    }

    public static void EqualLine(LyricLine expected, LyricLine actual)
    {
        Assert.Equal(expected.StartTime, actual.StartTime);
        Assert.Equal(expected.EndTime, actual.EndTime);
        Assert.Equal(expected.TranslatedLyric, actual.TranslatedLyric);
        Assert.Equal(expected.RomanLyric, actual.RomanLyric);
        Assert.Equal(expected.IsBg, actual.IsBg);
        Assert.Equal(expected.IsDuet, actual.IsDuet);
        Assert.Equal(expected.Words.Count, actual.Words.Count);
        for (var i = 0; i < expected.Words.Count; i++)
            EqualWord(expected.Words[i], actual.Words[i]);
    }

    public static void EqualWord(LyricWord expected, LyricWord actual)
    {
        Assert.Equal(expected.StartTime, actual.StartTime);
        Assert.Equal(expected.EndTime, actual.EndTime);
        Assert.Equal(expected.Word, actual.Word);
        Assert.Equal(expected.RomanWord, actual.RomanWord);
        Assert.Equal(expected.Obscene, actual.Obscene);
        Assert.Equal(expected.EmptyBeat, actual.EmptyBeat);
        Assert.Equal(expected.Ruby?.Count ?? 0, actual.Ruby?.Count ?? 0);
        for (var i = 0; i < (expected.Ruby?.Count ?? 0); i++)
        {
            Assert.Equal(expected.Ruby![i].StartTime, actual.Ruby![i].StartTime);
            Assert.Equal(expected.Ruby[i].EndTime, actual.Ruby[i].EndTime);
            Assert.Equal(expected.Ruby[i].Word, actual.Ruby[i].Word);
        }
    }
}
