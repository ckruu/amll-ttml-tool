using AmllTtmlTool.Core.Lyrics;

namespace AmllTtmlTool.Core.Tests;

public sealed class SplitWeightsTests
{
    [Theory]
    [InlineData("a", LyricCharType.Latin)]
    [InlineData("Hello", LyricCharType.Latin)]
    [InlineData("é", LyricCharType.Latin)]
    [InlineData("д", LyricCharType.Latin)] // Cyrillic counts as Latin (RE_LATIN)
    [InlineData("ω", LyricCharType.Latin)] // Greek counts as Latin (RE_LATIN)
    [InlineData("5", LyricCharType.Numeric)]
    [InlineData("中", LyricCharType.Cjk)]
    [InlineData("あ", LyricCharType.Cjk)]
    [InlineData("한", LyricCharType.Cjk)]
    [InlineData(" ", LyricCharType.Whitespace)]
    [InlineData("", LyricCharType.Whitespace)]
    [InlineData(",", LyricCharType.Other)]
    [InlineData(".", LyricCharType.Other)]
    [InlineData("(", LyricCharType.Other)]
    public void Char_Types(string token, LyricCharType want) =>
        Assert.Equal(want, SplitWeights.GetCharType(token));

    [Theory]
    [InlineData("Hello", 5)]
    [InlineData("你好", 2)]
    [InlineData("42", 2)]
    [InlineData(",", 0.2)]
    [InlineData(" ", 0)]
    [InlineData("", 0)]
    public void Weights(string token, double want) =>
        Assert.Equal(want, SplitWeights.CalculateWeight(token, 0.2));

    private static EditWord Word(int s, int e, string t)
    {
        var w = EditWord.New();
        w.StartTimeMs = s;
        w.EndTimeMs = e;
        w.Text = t;
        return w;
    }

    [Fact]
    public void Even_Split()
    {
        var got = SplitWeights.DistributeTime(Word(0, 2000, "HelloWorld"),
            new[] { "Hello", "World" }, 0.2);
        Assert.Equal(new[] { 0, 1000 }, new[] { got[0].StartTimeMs, got[0].EndTimeMs });
        Assert.Equal(new[] { 1000, 2000 }, new[] { got[1].StartTimeMs, got[1].EndTimeMs });
    }

    [Fact]
    public void Half_Rounds_Up_Like_Js()
    {
        // 5ms over weights 1:1 -> 2.5 each; JS Math.round -> 3.
        var got = SplitWeights.DistributeTime(Word(0, 5, "ab"),
            new[] { "a", "b" }, 0.2);
        Assert.Equal(3, got[0].EndTimeMs);
        Assert.Equal(3, got[1].StartTimeMs);
        Assert.Equal(5, got[1].EndTimeMs);
    }

    [Fact]
    public void Last_Token_Snaps_To_Original_End()
    {
        var got = SplitWeights.DistributeTime(Word(0, 100, "abcde"),
            new[] { "abc", "de" }, 0.2);
        Assert.Equal(60, got[0].EndTimeMs);
        Assert.Equal(100, got[1].EndTimeMs);
    }

    [Fact]
    public void Single_Segment_Keeps_Id_And_Fields()
    {
        var w = Word(100, 200, "hi");
        w.Obscene = true;
        w.RomanWord = "r";
        var got = SplitWeights.DistributeTime(w, new[] { "hi" }, 0.2);
        Assert.Single(got);
        Assert.Equal(w.Id, got[0].Id);
        Assert.True(got[0].Obscene);
        Assert.Equal("r", got[0].RomanWord);
    }

    [Fact]
    public void Zero_Duration_Shares_Span_With_Fresh_Ids()
    {
        var w = Word(500, 500, "ab");
        var got = SplitWeights.DistributeTime(w, new[] { "a", "b" }, 0.2);
        Assert.Equal(2, got.Count);
        Assert.All(got, x => { Assert.Equal(500, x.StartTimeMs); Assert.Equal(500, x.EndTimeMs); });
        Assert.DoesNotContain(got, x => x.Id == w.Id);
    }

    [Fact]
    public void Zero_Weight_Yields_No_Words()
    {
        var got = SplitWeights.DistributeTime(Word(0, 100, "  "),
            new[] { " ", " " }, 0.2);
        Assert.Empty(got);
    }

    [Fact]
    public void Fresh_Words_Lose_Extra_Fields()
    {
        var w = Word(0, 1000, "ab");
        w.Obscene = true;
        w.EmptyBeat = 2;
        var got = SplitWeights.DistributeTime(w, new[] { "a", "b" }, 0.2);
        Assert.All(got, x => { Assert.False(x.Obscene); Assert.Equal(0, x.EmptyBeat); });
    }
}
