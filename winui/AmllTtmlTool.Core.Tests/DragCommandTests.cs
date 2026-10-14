using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Lyrics.Commands;

namespace AmllTtmlTool.Core.Tests;

// Drag move/copy commands (STEP5): one Execute = one history step, exact
// undo/redo, normalize only at touched bounds like the original drops.
public sealed class DragCommandTests
{
    private static EditDocument Doc(out EditLine l0, out EditLine l1)
    {
        var doc = new EditDocument();
        l0 = EditLine.New();
        l0.Words.Add(TestWords.W(0, 1000, "a"));
        l0.Words.Add(TestWords.W(1000, 2000, "b"));
        l0.Words.Add(TestWords.W(2000, 3000, "c"));
        l0.StartTimeMs = 0;
        l0.EndTimeMs = 3000;
        l1 = EditLine.New();
        l1.Words.Add(TestWords.W(4000, 5000, "x"));
        l1.Words.Add(TestWords.W(5000, 6000, "y"));
        l1.StartTimeMs = 4000;
        l1.EndTimeMs = 6000;
        doc.Lines.Add(l0);
        doc.Lines.Add(l1);
        return doc;
    }

    private static string Order(EditDocument doc) => string.Join("|",
        doc.Lines.Select(l => string.Join(",", l.Words.Select(w => w.Text))));

    [Fact]
    public void MoveWords_Within_Line_Reorders_And_Restores()
    {
        var doc = Doc(out var l0, out _);
        var cmd = new MoveWordsCommand(new[] { l0.Words[0].Id }, l0.Id, l0.Words[2].Id, true, false);
        doc.Execute(cmd);
        Assert.Equal("b,c,a", string.Join(",", l0.Words.Select(w => w.Text)));
        Assert.Equal(new[] { l0.Words[2].Id }, cmd.ResultIds);
        Assert.True(doc.Undo());
        Assert.Equal("a,b,c", string.Join(",", l0.Words.Select(w => w.Text)));
        Assert.Equal("a,b,c|x,y", Order(doc));
        Assert.True(doc.Redo());
        Assert.Equal("b,c,a", string.Join(",", l0.Words.Select(w => w.Text)));
    }

    [Fact]
    public void MoveWords_Cross_Line_Keeps_Order_Normalizes_Bounds()
    {
        var doc = Doc(out var l0, out var l1);
        // Move a,b (incl. first = bound) before y: l0 loses its head.
        var cmd = new MoveWordsCommand(
            new[] { l0.Words[1].Id, l0.Words[0].Id }, l1.Id, l1.Words[1].Id, false, false);
        doc.Execute(cmd);
        Assert.Equal("c", string.Join(",", l0.Words.Select(w => w.Text)));
        Assert.Equal("x,a,b,y", string.Join(",", l1.Words.Select(w => w.Text)));
        Assert.Equal(2000, l0.StartTimeMs); // normalized (deleted at bound)
        var fp = Order(doc);
        Assert.True(doc.Undo());
        Assert.Equal("a,b,c|x,y", Order(doc));
        Assert.Equal(0, l0.StartTimeMs);
        Assert.True(doc.Redo());
        Assert.Equal(fp, Order(doc));
    }

    [Fact]
    public void CopyWords_Clones_Fresh_Ids_Reselects_Clones()
    {
        var doc = Doc(out var l0, out var l1);
        var before = l1.Words.Select(w => w.Id).ToList();
        var cmd = new MoveWordsCommand(new[] { l0.Words[0].Id }, l1.Id, null, false, true);
        doc.Execute(cmd);
        Assert.Equal("a,b,c|x,y,a", Order(doc));
        Assert.Single(cmd.ResultIds);
        Assert.DoesNotContain(cmd.ResultIds[0], before);
        Assert.DoesNotContain(cmd.ResultIds[0], l0.Words.Select(w => w.Id));
        var cloneId = cmd.ResultIds[0];
        Assert.True(doc.Undo());
        Assert.Equal("a,b,c|x,y", Order(doc));
        Assert.True(doc.Redo());
        Assert.Equal("a,b,c|x,y,a", Order(doc));
        // Redo reuses the minted clones (same ids), like the original's
        // re-applied patch — never mints fresh ones per cycle.
        Assert.Equal(cloneId, cmd.ResultIds[0]);
        Assert.Equal(cloneId, l1.Words[^1].Id);
    }

    [Fact]
    public void MoveWords_Empty_Selection_Throws()
    {
        var doc = Doc(out var l0, out _);
        Assert.Throws<InvalidOperationException>(() =>
            new MoveWordsCommand(new[] { "missing" }, l0.Id, null, false, false).Apply(doc));
    }

    [Fact]
    public void ReorderLines_Move_Keeps_Order_And_Restores()
    {
        var doc = Doc(out var l0, out var l1);
        var l2 = EditLine.New();
        l2.Words.Add(TestWords.W(7000, 8000, "z"));
        doc.Lines.Add(l2);
        var cmd = new ReorderLinesCommand(new[] { l0.Id, l2.Id }, 3, false);
        doc.Execute(cmd);
        Assert.Equal(new[] { l1.Id, l0.Id, l2.Id }, doc.Lines.Select(l => l.Id));
        Assert.Equal(new[] { l0.Id, l2.Id }, cmd.ResultIds);
        Assert.True(doc.Undo());
        Assert.Equal(new[] { l0.Id, l1.Id, l2.Id }, doc.Lines.Select(l => l.Id));
        Assert.True(doc.Redo());
        Assert.Equal(new[] { l1.Id, l0.Id, l2.Id }, doc.Lines.Select(l => l.Id));
    }

    [Fact]
    public void ReorderLines_Copy_Clones_After_Target()
    {
        var doc = Doc(out var l0, out var l1);
        var cmd = new ReorderLinesCommand(new[] { l0.Id }, 1, true);
        doc.Execute(cmd);
        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal(l0.Id, doc.Lines[0].Id);
        Assert.Single(cmd.ResultIds);
        Assert.NotEqual(l0.Id, cmd.ResultIds[0]);
        Assert.Equal("a,b,c", string.Join(",", doc.Lines[1].Words.Select(w => w.Text)));
        var cloneLineId = cmd.ResultIds[0];
        Assert.True(doc.Undo());
        Assert.Equal(2, doc.Lines.Count);
        Assert.True(doc.Redo());
        Assert.Equal(3, doc.Lines.Count);
        Assert.Equal(cloneLineId, cmd.ResultIds[0]);
        Assert.Equal(cloneLineId, doc.Lines[1].Id);
    }

    [Fact]
    public void ReorderLines_Empty_Throws()
    {
        var doc = Doc(out _, out _);
        Assert.Throws<InvalidOperationException>(() =>
            new ReorderLinesCommand(new[] { "missing" }, 0, false).Apply(doc));
    }

    [Fact]
    public void ReorderLines_DropIndex_Clamps()
    {
        var doc = Doc(out var l0, out var l1);
        var cmd = new ReorderLinesCommand(new[] { l0.Id }, 99, false);
        doc.Execute(cmd);
        Assert.Equal(new[] { l1.Id, l0.Id }, doc.Lines.Select(l => l.Id));
        Assert.True(doc.Undo());
        Assert.Equal(new[] { l0.Id, l1.Id }, doc.Lines.Select(l => l.Id));
    }
}
