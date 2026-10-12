using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Lyrics.Commands;

namespace AmllTtmlTool.Core.Tests;

public sealed class BulkCommandTests
{
    private static EditDocument Doc(out EditLine l1, out EditLine l2)
    {
        var doc = new EditDocument();
        l1 = EditLine.New();
        var w1 = TestWords.W(100, 200, "Hello");
        w1.Ruby.Add(new EditRubySegment { Text = "r", StartTimeMs = 100, EndTimeMs = 200 });
        l1.Words.Add(w1);
        l1.StartTimeMs = 100;
        l1.EndTimeMs = 200;
        l2 = EditLine.New();
        l2.Words.Add(TestWords.W(300, 400, "world"));
        l2.StartTimeMs = 300;
        l2.EndTimeMs = 400;
        doc.Lines.Add(l1);
        doc.Lines.Add(l2);
        doc.Metadata.Add(new EditMetadata { Key = "musicName", Values = new List<string> { "S" } });
        return doc;
    }

    [Fact]
    public void BulkReplace_Swaps_All_And_Restores()
    {
        var doc = Doc(out _, out _);
        var fresh = new List<EditLine> { EditLine.New() };
        doc.Execute(new BulkReplaceCommand(fresh, new List<EditMetadata>()));
        Assert.Single(doc.Lines);
        Assert.Empty(doc.Metadata);
        Assert.True(doc.IsDirty);
        Assert.True(doc.Undo());
        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("Hello", doc.Lines[0].Words[0].Text);
        Assert.Equal("S", doc.Metadata[0].Values[0]);
        Assert.True(doc.Redo());
        Assert.Single(doc.Lines);
    }

    [Fact]
    public void TimeShift_Moves_Line_Word_Ruby_With_Clamp()
    {
        var doc = Doc(out var l1, out var l2);
        doc.Execute(new TimeShiftCommand(new[] { l1.Id }, 50));
        Assert.Equal(150, l1.StartTimeMs);
        Assert.Equal(150, l1.Words[0].StartTimeMs);
        Assert.Equal(150, l1.Words[0].Ruby[0].StartTimeMs);
        Assert.Equal(300, l2.StartTimeMs); // unscoped line untouched
        doc.Execute(new TimeShiftCommand(new[] { l1.Id }, -1000));
        Assert.Equal(0, l1.StartTimeMs); // clamped, not negative
        Assert.Equal(0, l1.Words[0].StartTimeMs);
        Assert.Equal(0, l1.Words[0].Ruby[0].EndTimeMs);
        Assert.True(doc.Undo());
        Assert.Equal(150, l1.StartTimeMs);
        Assert.True(doc.Undo());
        Assert.Equal(100, l1.StartTimeMs);
    }

    [Fact]
    public void TimeShift_Empty_Scope_Shifts_All()
    {
        var doc = Doc(out var l1, out var l2);
        doc.Execute(new TimeShiftCommand(Array.Empty<string>(), 10));
        Assert.Equal(110, l1.StartTimeMs);
        Assert.Equal(310, l2.StartTimeMs);
    }

    [Fact]
    public void Replace_All_Exact_And_IgnoreCase()
    {
        var doc = Doc(out var l1, out var l2);
        l2.Words[0].Text = "hello";
        doc.Execute(new ReplaceWordTextCommand("Hello", "Hi"));
        Assert.Equal("Hi", l1.Words[0].Text);
        Assert.Equal("hello", l2.Words[0].Text); // exact: case differs
        Assert.True(doc.Undo());
        doc.Execute(new ReplaceWordTextCommand("Hello", "Hi", ignoreCase: true));
        Assert.Equal("Hi", l1.Words[0].Text);
        Assert.Equal("Hi", l2.Words[0].Text);
        Assert.True(doc.Undo());
        Assert.Equal("hello", l2.Words[0].Text);
    }

    [Fact]
    public void Replace_Single_Id_Exact_Only()
    {
        var doc = Doc(out var l1, out var l2);
        l2.Words[0].Text = "HELLO";
        doc.Execute(new ReplaceWordTextCommand("Hello", "Hi",
            ignoreCase: true, onlyWordIds: new[] { l2.Words[0].Id }));
        // Single mode ignores ignoreCase and only touches the id.
        Assert.Equal("Hello", l1.Words[0].Text);
        Assert.Equal("HELLO", l2.Words[0].Text);
        Assert.True(doc.Undo());
    }

    [Fact]
    public void Unmapped_Carry_Survives_Untouched_Words()
    {
        var doc = Doc(out var l1, out _);
        l1.Unmapped["agent"] = "v2";
        l1.Words[0].Unmapped["x"] = "y";
        doc.Execute(new SetTranslationCommand(l1.Id, "T"));
        Assert.Equal("v2", l1.Unmapped["agent"]);
        Assert.Equal("y", l1.Words[0].Unmapped["x"]);
        // Structural rebuilds keep surviving items' bags.
        doc.Execute(new RemoveWordsCommand(l1.Id, new[] { "nope" }));
        Assert.Equal("y", l1.Words[0].Unmapped["x"]);
    }
}
