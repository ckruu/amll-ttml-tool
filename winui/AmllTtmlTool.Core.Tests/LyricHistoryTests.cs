using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Lyrics.Commands;

namespace AmllTtmlTool.Core.Tests;

public sealed class LyricHistoryTests
{
    private static EditDocument Doc()
    {
        var doc = new EditDocument();
        doc.Lines.Add(EditLine.New());
        return doc;
    }

    [Fact]
    public void New_Document_Is_Clean_And_Nothing_To_Undo()
    {
        var doc = Doc();
        Assert.False(doc.IsDirty);
        Assert.False(doc.CanUndo);
        Assert.False(doc.CanRedo);
        Assert.False(doc.Undo());
        Assert.False(doc.Redo());
    }

    [Fact]
    public void Execute_Undo_Redo_Flip_Dirty()
    {
        var doc = Doc();
        var line = doc.Lines[0];
        doc.Execute(new SetTranslationCommand(line.Id, "T"));
        Assert.True(doc.IsDirty);
        Assert.True(doc.CanUndo);
        Assert.False(doc.CanRedo);
        Assert.True(doc.Undo());
        Assert.Equal(string.Empty, line.TranslatedLyric);
        Assert.False(doc.IsDirty);
        Assert.True(doc.CanRedo);
        Assert.True(doc.Redo());
        Assert.Equal("T", line.TranslatedLyric);
        Assert.True(doc.IsDirty);
    }

    [Fact]
    public void New_Command_Clears_Redo()
    {
        var doc = Doc();
        var line = doc.Lines[0];
        doc.Execute(new SetTranslationCommand(line.Id, "A"));
        doc.Undo();
        Assert.True(doc.CanRedo);
        doc.Execute(new SetTranslationCommand(line.Id, "B"));
        Assert.False(doc.CanRedo);
        Assert.False(doc.Redo());
        Assert.Equal("B", line.TranslatedLyric);
    }

    [Fact]
    public void Depth_Capped_At_MaxHistoryDepth()
    {
        Assert.Equal(100, EditDocument.MaxHistoryDepth);
        var doc = Doc();
        var line = doc.Lines[0];
        for (var i = 0; i < 105; i++)
            doc.Execute(new SetTranslationCommand(line.Id, "v" + i));
        Assert.Equal(100, doc.UndoDepth);
        for (var i = 0; i < 100; i++) Assert.True(doc.Undo());
        Assert.False(doc.Undo());
        // Oldest 5 steps were dropped: undoing 100 restores v4, not "".
        Assert.Equal("v4", line.TranslatedLyric);
    }

    [Fact]
    public void Composite_Is_One_Step()
    {
        var doc = Doc();
        var line = doc.Lines[0];
        doc.Execute(new CompositeCommand("two fields", new ILyricCommand[]
        {
            new SetTranslationCommand(line.Id, "T"),
            new SetRomanizationCommand(line.Id, "R"),
        }));
        Assert.Equal(1, doc.UndoDepth);
        doc.Undo();
        Assert.Equal(string.Empty, line.TranslatedLyric);
        Assert.Equal(string.Empty, line.RomanLyric);
        Assert.False(doc.IsDirty);
        doc.Redo();
        Assert.Equal("T", line.TranslatedLyric);
        Assert.Equal("R", line.RomanLyric);
    }

    [Fact]
    public void Bulk_Replace_Is_One_Undoable_Step()
    {
        var doc = Doc();
        doc.Execute(new BulkReplaceCommand(
            new List<EditLine> { EditLine.New(), EditLine.New() },
            new List<EditMetadata>()));
        Assert.Equal(2, doc.Lines.Count);
        Assert.True(doc.IsDirty);
        doc.Undo();
        Assert.Single(doc.Lines);
        doc.Redo();
        Assert.Equal(2, doc.Lines.Count);
    }
}
