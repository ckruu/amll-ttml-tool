using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Lyrics.Commands;

namespace AmllTtmlTool.Core.Tests;

public sealed class LineCommandTests
{
    private static EditDocument Doc(out EditLine l1, out EditLine l2)
    {
        var doc = new EditDocument();
        l1 = EditLine.New();
        l1.Words.Add(TestWords.W(0, 1000, "Hello"));
        l1.StartTimeMs = 0;
        l1.EndTimeMs = 1000;
        l2 = EditLine.New();
        l2.Words.Add(TestWords.W(2000, 3000, "Again"));
        l2.StartTimeMs = 2000;
        l2.EndTimeMs = 3000;
        doc.Lines.Add(l1);
        doc.Lines.Add(l2);
        return doc;
    }

    [Fact]
    public void AddLine_Inserts_And_Undoes()
    {
        var doc = Doc(out _, out var l2);
        var line = EditLine.New();
        doc.Execute(new AddLineCommand(1, line));
        Assert.Equal(new[] { line.Id }, new[] { doc.Lines[1].Id });
        Assert.True(doc.Undo());
        Assert.Equal(2, doc.Lines.Count);
        Assert.DoesNotContain(doc.Lines, l => l.Id == line.Id);
        Assert.True(doc.Redo());
        Assert.Equal(line.Id, doc.Lines[1].Id);
        _ = l2;
    }

    [Fact]
    public void AddLine_Clamps_Index()
    {
        var doc = Doc(out _, out _);
        var line = EditLine.New();
        doc.Execute(new AddLineCommand(99, line));
        Assert.Equal(line.Id, doc.Lines[^1].Id);
    }

    [Fact]
    public void RemoveLines_Drops_With_Words_And_Restores()
    {
        var doc = Doc(out var l1, out _);
        doc.Execute(new RemoveLinesCommand(new[] { l1.Id }));
        Assert.Single(doc.Lines);
        Assert.True(doc.Undo());
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(l1.Id, doc.Lines[0].Id);
        Assert.Equal("Hello", doc.Lines[0].Words[0].Text);
        Assert.True(doc.Redo());
        Assert.Single(doc.Lines);
    }

    [Fact]
    public void MoveLine_Reorders_And_Restores()
    {
        var doc = Doc(out var l1, out var l2);
        doc.Execute(new MoveLineCommand(l1.Id, 1));
        Assert.Equal(l2.Id, doc.Lines[0].Id);
        Assert.Equal(l1.Id, doc.Lines[1].Id);
        Assert.True(doc.Undo());
        Assert.Equal(l1.Id, doc.Lines[0].Id);
        Assert.True(doc.Redo());
        Assert.Equal(l1.Id, doc.Lines[1].Id);
    }

    [Fact]
    public void CombineLines_Appends_And_Takes_Last_End()
    {
        var doc = Doc(out var l1, out var l2);
        l2.TranslatedLyric = "discarded";
        doc.Execute(new CombineLinesCommand(new[] { l1.Id, l2.Id }));
        Assert.Single(doc.Lines);
        Assert.Equal(new[] { "Hello", "Again" },
            doc.Lines[0].Words.Select(w => w.Text).ToArray());
        Assert.Equal(3000, doc.Lines[0].EndTimeMs);
        Assert.Equal(0, doc.Lines[0].StartTimeMs);
        Assert.True(doc.Undo());
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("discarded", doc.Lines[1].TranslatedLyric);
        Assert.Equal(1000, doc.Lines[0].EndTimeMs);
    }

    [Fact]
    public void CombineLines_Rejects_NonContiguous()
    {
        var doc = Doc(out var l1, out _);
        var l3 = EditLine.New();
        doc.Lines.Add(l3);
        Assert.Throws<InvalidOperationException>(() =>
            new CombineLinesCommand(new[] { l1.Id, l3.Id }).Apply(doc));
    }

    [Fact]
    public void CombineLines_Rejects_Single()
    {
        var doc = Doc(out var l1, out _);
        Assert.Throws<InvalidOperationException>(() =>
            new CombineLinesCommand(new[] { l1.Id }).Apply(doc));
    }

    [Fact]
    public void DuplicateLines_Clones_With_Fresh_Ids_After_Source()
    {
        var doc = Doc(out var l1, out _);
        doc.Execute(new DuplicateLinesCommand(new[] { l1.Id }));
        Assert.Equal(3, doc.Lines.Count);
        var clone = doc.Lines[1];
        Assert.NotEqual(l1.Id, clone.Id);
        Assert.Equal("Hello", clone.Words[0].Text);
        Assert.NotEqual(l1.Words[0].Id, clone.Words[0].Id);
        Assert.True(doc.Undo());
        Assert.Equal(2, doc.Lines.Count);
    }

    [Fact]
    public void SetLineTimes_No_Propagation()
    {
        var doc = Doc(out var l1, out var l2);
        doc.Execute(new SetLineTimesCommand(l1.Id, 100, 900));
        Assert.Equal(100, l1.StartTimeMs);
        Assert.Equal(900, l1.EndTimeMs);
        Assert.Equal(2000, l2.StartTimeMs); // untouched
        Assert.Equal(0, l1.Words[0].StartTimeMs); // words untouched
        Assert.True(doc.Undo());
        Assert.Equal(0, l1.StartTimeMs);
        Assert.Equal(1000, l1.EndTimeMs);
    }

    [Fact]
    public void SetLineFlags_Partial()
    {
        var doc = Doc(out var l1, out _);
        doc.Execute(new SetLineFlagsCommand(l1.Id, isBg: true));
        Assert.True(l1.IsBg);
        Assert.False(l1.IsDuet);
        doc.Execute(new SetLineFlagsCommand(l1.Id, isDuet: true, ignoreSync: true));
        Assert.True(l1.IsDuet);
        Assert.True(l1.IgnoreSync);
        Assert.True(doc.Undo());
        Assert.False(l1.IsDuet);
        Assert.False(l1.IgnoreSync);
        Assert.True(l1.IsBg);
    }

    [Fact]
    public void SetTranslation_And_Romanization()
    {
        var doc = Doc(out var l1, out _);
        doc.Execute(new SetTranslationCommand(l1.Id, "T"));
        doc.Execute(new SetRomanizationCommand(l1.Id, "R"));
        Assert.Equal("T", l1.TranslatedLyric);
        Assert.Equal("R", l1.RomanLyric);
        Assert.True(doc.Undo());
        Assert.Equal(string.Empty, l1.RomanLyric);
        Assert.Equal("T", l1.TranslatedLyric);
        Assert.True(doc.Undo());
        Assert.Equal(string.Empty, l1.TranslatedLyric);
    }

    [Fact]
    public void NormalizeTime_Zero_Bound_Falls_Back()
    {
        // Faithful || semantics: a 0 word bound keeps the line bound.
        var line = EditLine.New();
        line.StartTimeMs = 111;
        line.EndTimeMs = 222;
        line.Words.Add(TestWords.W(0, 0, "x"));
        line.NormalizeTime();
        Assert.Equal(111, line.StartTimeMs);
        Assert.Equal(222, line.EndTimeMs);
        line.Words[0].StartTimeMs = 50;
        line.Words[0].EndTimeMs = 60;
        line.NormalizeTime();
        Assert.Equal(50, line.StartTimeMs);
        Assert.Equal(60, line.EndTimeMs);
    }

    [Fact]
    public void Link_Unlink_Snap_And_Restore()
    {
        var doc = Doc(out var l1, out var l2);
        doc.Execute(new LinkEndTimeCommand(l1.Id));
        Assert.Equal(2000, l1.EndTimeMs);
        Assert.Equal(2000, l2.StartTimeMs);
        Assert.NotNull(l1.EndTimeLink);
        Assert.Equal(1000, l1.EndTimeLink!.OriginalEndTime);
        Assert.Equal(2000, l1.EndTimeLink.OriginalNextStartTime);
        // Chained follow: end edits re-snap next start.
        doc.Execute(new SetLineTimesCommand(l1.Id, 0, 2500));
        Assert.Equal(2500, l2.StartTimeMs);
        doc.Execute(new UnlinkEndTimeCommand(l1.Id));
        Assert.Equal(1000, l1.EndTimeMs);
        Assert.Equal(2000, l2.StartTimeMs);
        Assert.Null(l1.EndTimeLink);
    }

    [Fact]
    public void Link_Needs_Next_Line()
    {
        var doc = Doc(out _, out var l2);
        Assert.Throws<InvalidOperationException>(() =>
            new LinkEndTimeCommand(l2.Id).Apply(doc));
    }
}

internal static class TestWords
{
    internal static EditWord W(int s, int e, string t)
    {
        var w = EditWord.New();
        w.StartTimeMs = s;
        w.EndTimeMs = e;
        w.Text = t;
        return w;
    }
}
