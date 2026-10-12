using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Lyrics.Commands;
using AmllTtmlTool.Core.Models;

namespace AmllTtmlTool.Core.Tests;

// Behaviors preserved from the old records store (verified by reading it):
// extension sets, FileName default/flow, Changed/Notice events, New/Open/
// paste flows, merge no-op rule, MarkSaved silence, failure leaves the doc
// untouched. Deliberate changes under test: "Untitled" default, baseline-
// depth dirty tracking (undo back to baseline is clean), registry routing.
public sealed class LyricStoreTests
{
    private const string Ttml =
        "<tt xmlns=\"http://www.w3.org/ns/ttml\">" +
        "<head><metadata></metadata></head><body><div>" +
        "<p begin=\"00:00:00.000\" end=\"00:00:01.000\">" +
        "<span begin=\"00:00:00.000\" end=\"00:00:01.000\">hi</span></p>" +
        "</div></body></tt>";

    [Fact]
    public void Fresh_Store_Is_Empty_Untitled_Clean()
    {
        var store = new LyricStore();
        Assert.Empty(store.Document.Lines);
        Assert.Equal("Untitled", store.FileName);
        Assert.False(store.IsDirty);
        Assert.False(store.CanUndo);
        Assert.False(store.CanRedo);
        Assert.False(store.Undo());
        Assert.False(store.Redo());
    }

    [Fact]
    public void Extension_Sets()
    {
        Assert.True(LyricStore.IsLyricFile("a.ttml"));
        Assert.True(LyricStore.IsLyricFile("a.LRC"));
        Assert.True(LyricStore.IsLyricFile("a.txt"));
        Assert.False(LyricStore.IsLyricFile("a.qrc"));
        Assert.True(LyricStore.IsAudioFile("a.flac"));
        Assert.False(LyricStore.IsAudioFile("a.ttml"));
        Assert.Equal("ttml", LyricStore.ExtensionOf("a.b.ttml"));
        Assert.Equal("", LyricStore.ExtensionOf("noext"));
    }

    [Fact]
    public void NewDocument_Is_Undoable_But_Clean_One_Event()
    {
        var store = new LyricStore();
        store.OpenLyricText(Ttml, "a.ttml");
        var events = 0;
        store.Changed += () => events++;
        store.NewDocument();
        Assert.Empty(store.Document.Lines);
        Assert.Equal("Untitled", store.FileName);
        Assert.False(store.IsDirty);
        Assert.True(store.CanUndo);
        Assert.Equal(1, events);
        Assert.True(store.Undo());
        Assert.Single(store.Document.Lines); // previous doc restored
    }

    [Fact]
    public void Open_Ttml_Sets_Name_Clean_Notices_None()
    {        var store = new LyricStore();
        var notices = new List<string>();
        store.Notice += notices.Add;
        var events = 0;
        store.Changed += () => events++;
        var result = store.OpenLyricText(Ttml, "song.ttml");
        Assert.True(result.Success);
        Assert.Single(store.Document.Lines);
        Assert.Equal("hi", store.Document.Lines[0].Words[0].Text);
        Assert.Equal("song.ttml", store.FileName);
        Assert.False(store.IsDirty);
        Assert.True(store.CanUndo);
        Assert.Equal(1, events);
        Assert.Empty(notices);
    }

    [Fact]
    public void Open_With_DisplayName_Shows_It()
    {
        var store = new LyricStore();
        var result = store.OpenLyricText(Ttml, "pasted.ttml", "Untitled");
        Assert.True(result.Success);
        Assert.Equal("Untitled", store.FileName);
        Assert.Single(store.Document.Lines);
    }

    [Fact]
    public void Parse_Then_Apply_Split_Phases()
    {
        var store = new LyricStore();
        var importer = LyricStore.ImporterFor("a.lrc");
        var parsed = LyricStore.ParseLyricText(importer, "[00:01.00]hi\n", "a.lrc");
        Assert.Empty(store.Document.Lines); // parse touches nothing
        store.ApplyImportResult(parsed, "a.lrc");
        Assert.Equal("hi", store.Document.Lines[0].Words[0].Text);
        Assert.Equal("a.lrc", store.FileName);
        Assert.False(store.IsDirty);
    }

    [Fact]
    public void Open_Lrc_And_Txt()
    {
        var store = new LyricStore();
        Assert.True(store.OpenLyricText("[00:01.00]hello\n", "a.lrc").Success);
        Assert.Equal("hello", store.Document.Lines[0].Words[0].Text);
        Assert.Equal("a.lrc", store.FileName);
        Assert.True(store.OpenLyricText("plain\nlines\n", "b.txt").Success);
        Assert.Equal(3, store.Document.Lines.Count);
        Assert.Equal("b.txt", store.FileName);
        Assert.False(store.IsDirty);
    }

    [Fact]
    public void Open_Legacy_Ttml_Fires_Notices_First()
    {
        var store = new LyricStore();
        var notices = new List<string>();
        store.Notice += notices.Add;
        // Bare-seconds timestamps are the "short timestamp format" legacy
        // signal (verified against the original regex).
        var legacy = Ttml.Replace("00:00:00.000", "0.000").Replace("00:00:01.000", "1.000");
        var result = store.OpenLyricText(legacy, "old.ttml");
        Assert.True(result.Success);
        Assert.NotEmpty(notices);
        Assert.NotNull(result.FixNotes);
    }

    [Fact]
    public void Open_Failure_Leaves_Doc_Untouched_No_Event()
    {
        var store = new LyricStore();
        store.OpenLyricText(Ttml, "a.ttml");
        var before = store.Document;
        var events = 0;
        store.Changed += () => events++;
        var bad = store.OpenLyricText("<tt><body><div><p begin=", "b.ttml");
        Assert.False(bad.Success);
        Assert.NotNull(bad.ErrorKind);
        Assert.Same(before, store.Document);
        Assert.Equal("a.ttml", store.FileName);
        Assert.Equal(0, events);
        var unsupported = store.OpenLyricText("whatever", "c.qrc");
        Assert.False(unsupported.Success);
        Assert.Equal("UnsupportedFormat", unsupported.ErrorKind);
        Assert.Same(before, store.Document);
    }

    [Fact]
    public void Execute_Undo_Redo_Dirty_Flow()
    {
        var store = new LyricStore();
        store.OpenLyricText(Ttml, "a.ttml");
        var line = store.Document.Lines[0];
        Assert.False(store.IsDirty);
        store.Execute(new SetTranslationCommand(line.Id, "T"));
        Assert.True(store.IsDirty);
        Assert.True(store.CanUndo);
        Assert.True(store.Undo());
        Assert.Equal("", line.TranslatedLyric);
        Assert.False(store.IsDirty); // back at baseline: clean
        Assert.True(store.CanRedo);
        Assert.True(store.Redo());
        Assert.Equal("T", line.TranslatedLyric);
        Assert.True(store.IsDirty);
    }

    [Fact]
    public void Undo_Below_Baseline_Is_Dirty()
    {
        var store = new LyricStore();
        store.OpenLyricText(Ttml, "a.ttml"); // baseline depth 1
        Assert.True(store.Undo()); // back to the empty pre-open doc
        Assert.Empty(store.Document.Lines);
        Assert.True(store.IsDirty);
    }

    [Fact]
    public void MarkSaved_Cleans_Without_Event()
    {
        var store = new LyricStore();
        store.OpenLyricText(Ttml, "a.ttml");
        store.Execute(new SetTranslationCommand(store.Document.Lines[0].Id, "T"));
        Assert.True(store.IsDirty);
        var events = 0;
        store.Changed += () => events++;
        store.MarkSaved("a.ttml");
        Assert.False(store.IsDirty);
        Assert.Equal("a.ttml", store.FileName);
        Assert.Equal(0, events);
        store.Execute(new SetTranslationCommand(store.Document.Lines[0].Id, "T2"));
        Assert.True(store.IsDirty);
    }

    [Fact]
    public void MergeAudioMetadata_NoChange_No_Undo_No_Event()
    {
        var store = new LyricStore();
        store.OpenLyricText(Ttml, "a.ttml");
        var depth = store.Document.UndoDepth;
        var events = 0;
        store.Changed += () => events++;
        store.MergeAudioMetadata(new List<TtmlMetadata>());
        Assert.Equal(depth, store.Document.UndoDepth);
        Assert.Equal(0, events);
        Assert.False(store.IsDirty);
    }

    [Fact]
    public void MergeAudioMetadata_Change_Is_Undoable_Step()
    {
        var store = new LyricStore();
        store.OpenLyricText(Ttml, "a.ttml");
        store.MarkSaved("a.ttml");
        store.MergeAudioMetadata(new List<TtmlMetadata>
        {
            new("musicName", new List<string> { "Song" }),
        });
        Assert.True(store.IsDirty);
        var meta = Assert.Single(store.Document.Metadata);
        Assert.Equal("musicName", meta.Key);
        Assert.True(store.Undo());
        Assert.Empty(store.Document.Metadata);
        Assert.False(store.IsDirty);
    }

    [Fact]
    public void Changed_Fires_Once_Per_Mutation()
    {
        var store = new LyricStore();
        store.OpenLyricText(Ttml, "a.ttml");
        var events = 0;
        store.Changed += () => events++;
        store.Execute(new SetTranslationCommand(store.Document.Lines[0].Id, "x"));
        store.Undo();
        store.Redo();
        Assert.Equal(3, events);
    }

    [Fact]
    public void ToRecords_Snapshot_For_Export()
    {
        var store = new LyricStore();
        store.OpenLyricText(Ttml, "a.ttml");
        var records = store.ToRecords();
        Assert.Single(records.LyricLines);
        Assert.Equal("hi", records.LyricLines[0].Words[0].Word);
    }
}
