using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Lyrics.Commands;

namespace AmllTtmlTool.Core.Tests;

public sealed class WordCommandTests
{
    private static EditDocument Doc(out EditLine line)
    {
        var doc = new EditDocument();
        line = EditLine.New();
        line.Words.Add(TestWords.W(0, 1000, "Hello"));
        line.Words.Add(TestWords.W(1000, 2000, "world"));
        line.StartTimeMs = 0;
        line.EndTimeMs = 2000;
        doc.Lines.Add(line);
        return doc;
    }

    [Fact]
    public void AddWord_Inserts_And_Removes()
    {
        var doc = Doc(out var line);
        var word = TestWords.W(500, 600, "mid");
        doc.Execute(new AddWordCommand(line.Id, 1, word));
        Assert.Equal("mid", line.Words[1].Text);
        Assert.True(doc.Undo());
        Assert.Equal(2, line.Words.Count);
        Assert.True(doc.Redo());
        Assert.Equal("mid", line.Words[1].Text);
    }

    [Fact]
    public void RemoveWords_Filters_Normalizes_Restores()
    {
        var doc = Doc(out var line);
        var first = line.Words[0];
        doc.Execute(new RemoveWordsCommand(line.Id, new[] { first.Id }));
        Assert.Single(line.Words);
        Assert.Equal(1000, line.StartTimeMs); // normalized to survivor
        Assert.True(doc.Undo());
        Assert.Equal(2, line.Words.Count);
        Assert.Equal(first.Id, line.Words[0].Id);
        Assert.Equal(0, line.StartTimeMs);
    }

    [Fact]
    public void RemoveWords_Empty_Line_Keeps_Times()
    {
        var doc = Doc(out var line);
        doc.Execute(new RemoveWordsCommand(line.Id,
            line.Words.Select(w => w.Id)));
        Assert.Empty(line.Words);
        Assert.Equal(0, line.StartTimeMs);
        Assert.Equal(2000, line.EndTimeMs);
    }

    [Fact]
    public void MergeWords_Joins_Without_Space_Spans_Fresh()
    {
        var doc = Doc(out var line);
        var a = line.Words[0];
        a.Obscene = true;
        a.EmptyBeat = 3;
        doc.Execute(new MergeWordsCommand(line.Id,
            new[] { line.Words[0].Id, line.Words[1].Id }));
        Assert.Single(line.Words);
        var merged = line.Words[0];
        Assert.Equal("Helloworld", merged.Text);
        Assert.Equal(0, merged.StartTimeMs);
        Assert.Equal(2000, merged.EndTimeMs);
        Assert.NotEqual(a.Id, merged.Id);
        Assert.False(merged.Obscene); // fresh word drops per-word fields
        Assert.Equal(0, merged.EmptyBeat);
        Assert.True(doc.Undo());
        Assert.Equal(2, line.Words.Count);
        Assert.True(line.Words[0].Obscene);
        Assert.True(doc.Redo());
        Assert.Equal("Helloworld", line.Words[0].Text);
    }

    [Fact]
    public void MergeWords_Needs_Two()
    {
        var doc = Doc(out var line);
        Assert.Throws<InvalidOperationException>(() =>
            new MergeWordsCommand(line.Id, new[] { line.Words[0].Id }).Apply(doc));
    }

    [Fact]
    public void SplitWord_Distributes_And_Restores()
    {
        var doc = Doc(out var line);
        var id = line.Words[0].Id;
        doc.Execute(new SplitWordCommand(line.Id, id, new[] { 2 }));
        Assert.Equal(3, line.Words.Count);
        Assert.Equal("He", line.Words[0].Text);
        Assert.Equal("llo", line.Words[1].Text);
        Assert.Equal("world", line.Words[2].Text);
        Assert.Equal(0, line.Words[0].StartTimeMs);
        Assert.Equal(1000, line.Words[1].EndTimeMs);
        Assert.DoesNotContain(line.Words, w => w.Id == id);
        Assert.True(doc.Undo());
        Assert.Equal(2, line.Words.Count);
        Assert.Equal(id, line.Words[0].Id);
    }

    [Fact]
    public void SplitWord_Cjk_Weights()
    {
        var doc = Doc(out var line);
        line.Words.Clear();
        var w = TestWords.W(0, 400, "你a好");
        line.Words.Add(w);
        doc.Execute(new SplitWordCommand(line.Id, w.Id, new[] { 1, 2 }));
        Assert.Equal(new[] { "你", "a", "好" },
            line.Words.Select(x => x.Text).ToArray());
        Assert.Equal(0, line.Words[0].StartTimeMs);
        Assert.Equal(400, line.Words[2].EndTimeMs);
    }

    [Fact]
    public void MoveWordsToNewLine_Collects_In_Order_With_Anchor_Flags()
    {
        var doc = new EditDocument();
        var l1 = EditLine.New();
        var a = TestWords.W(0, 500, "a");
        var b = TestWords.W(500, 1000, "b");
        l1.Words.Add(a);
        l1.Words.Add(b);
        l1.StartTimeMs = 0;
        l1.EndTimeMs = 1000;
        var l2 = EditLine.New();
        var c = TestWords.W(2000, 2500, "c");
        l2.Words.Add(c);
        l2.IsBg = true;
        l2.StartTimeMs = 2000;
        l2.EndTimeMs = 2500;
        doc.Lines.Add(l1);
        doc.Lines.Add(l2);
        // Anchor = l2 (BG): new line goes after l2 with BG flag, words in
        // document order (b from l1, c from l2).
        doc.Execute(new MoveWordsToNewLineCommand(l2.Id, new[] { b.Id, c.Id }));
        Assert.Equal(3, doc.Lines.Count);
        var created = doc.Lines[2];
        Assert.True(created.IsBg);
        Assert.Equal(new[] { "b", "c" }, created.Words.Select(w => w.Text).ToArray());
        Assert.Single(l1.Words);
        Assert.Empty(l2.Words);
        Assert.True(doc.Undo());
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(2, l1.Words.Count);
        Assert.Single(l2.Words);
    }

    [Fact]
    public void MoveFollowing_Clicked_Moves_With_Rest()
    {
        var doc = Doc(out var line);
        var clicked = line.Words[0].Id;
        doc.Execute(new MoveFollowingWordsToNewLineCommand(line.Id, clicked));
        Assert.Empty(line.Words);
        var created = doc.Lines[1];
        Assert.Equal(new[] { "Hello", "world" },
            created.Words.Select(w => w.Text).ToArray());
        Assert.True(doc.Undo());
        Assert.Equal(2, line.Words.Count);
        Assert.Single(doc.Lines);
    }

    [Fact]
    public void MoveFollowing_Whitespace_Untimed_Deleted_First()
    {
        var doc = Doc(out var line);
        line.Words.Clear();
        var ws = TestWords.W(0, 0, "   ");
        var rest = TestWords.W(100, 200, "x");
        line.Words.Add(ws);
        line.Words.Add(rest);
        doc.Execute(new MoveFollowingWordsToNewLineCommand(line.Id, ws.Id));
        Assert.Empty(line.Words);
        Assert.Equal(new[] { "x" }, doc.Lines[1].Words.Select(w => w.Text).ToArray());
    }

    [Fact]
    public void SetWordText_Times_Flags_Roman()
    {
        var doc = Doc(out var line);
        var id = line.Words[0].Id;
        doc.Execute(new SetWordTextCommand(line.Id, id, "Hi"));
        doc.Execute(new SetWordTimesCommand(line.Id, id, 10, 20));
        doc.Execute(new SetWordFlagsCommand(line.Id, id, obscene: true, emptyBeat: 2));
        doc.Execute(new SetWordRomanCommand(line.Id, id, "hai"));
        var w = line.Words[0];
        Assert.Equal("Hi", w.Text);
        Assert.Equal(10, w.StartTimeMs);
        Assert.Equal(20, w.EndTimeMs);
        Assert.True(w.Obscene);
        Assert.Equal(2, w.EmptyBeat);
        Assert.Equal("hai", w.RomanWord);
        // Line untouched by word edits.
        Assert.Equal(0, line.StartTimeMs);
        for (var i = 0; i < 4; i++) Assert.True(doc.Undo());
        Assert.Equal("Hello", line.Words[0].Text);
        Assert.Equal(0, line.Words[0].StartTimeMs);
        Assert.False(line.Words[0].Obscene);
        Assert.False(doc.IsDirty);
    }
}
