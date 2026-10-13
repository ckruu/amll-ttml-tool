using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Lyrics.Commands;
using AmllTtmlTool.Core.Lyrics.Import;

namespace AmllTtmlTool.Core.Tests;

// Phase 3 Step 6 (command level, no UI): EVERY command applies, undoes and
// redoes correctly on real fixtures (TTML + LRC through the real importer
// registry). Fingerprints exclude ids (redo of split/merge mints fresh ids
// by design, like the original's fresh newLyricWord()s).
public sealed class EditCommandFixtureTests
{
    private static readonly string Fixtures =
        Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static EditDocument LoadTtml()
    {
        var text = File.ReadAllText(Path.Combine(Fixtures, "gen-rich.xml"));
        var doc = LyricImporterRegistry.Default().ByExtension("x.ttml").Import(text, "x.ttml");
        Assert.True(doc.Document.Lines.Count >= 2);
        return doc.Document;
    }

    private static EditDocument LoadLrc()
    {
        var text = File.ReadAllText(Path.Combine(Fixtures, "lrc-multi.lrc"));
        var doc = LyricImporterRegistry.Default().ByExtension("x.lrc").Import(text, "x.lrc");
        Assert.True(doc.Document.Lines.Count >= 2);
        return doc.Document;
    }

    private static string Fingerprint(EditDocument doc)
    {
        var parts = new List<string>();
        foreach (var l in doc.Lines)
        {
            parts.Add($"L[{l.StartTimeMs},{l.EndTimeMs},{l.IsBg},{l.IsDuet},{l.IgnoreSync},{l.TranslatedLyric},{l.RomanLyric}]");
            foreach (var w in l.Words)
                parts.Add($"W[{w.StartTimeMs},{w.EndTimeMs},{w.Text},{w.RomanWord},{w.Obscene},{w.EmptyBeat}]");
            parts.Add("/");
        }
        return string.Join("|", parts);
    }

    private static void Cycle(EditDocument doc, ILyricCommand cmd)
    {
        var before = Fingerprint(doc);
        doc.Execute(cmd);
        var after = Fingerprint(doc);
        Assert.True(doc.Undo());
        Assert.Equal(before, Fingerprint(doc));
        Assert.True(doc.Redo());
        Assert.Equal(after, Fingerprint(doc));
    }

    private static (EditLine Line, EditWord Word) FirstWord(EditDocument doc)
    {
        var line = doc.Lines.First(l => l.Words.Count > 0);
        return (line, line.Words[0]);
    }

    [Fact]
    public void FieldSetters_Cycle_OnTtml()
    {
        var doc = LoadTtml();
        var (line, word) = FirstWord(doc);
        Cycle(doc, new SetWordTextCommand(line.Id, word.Id, "edited"));
        Cycle(doc, new SetWordRomanCommand(line.Id, word.Id, "rom"));
        Cycle(doc, new SetWordTimesCommand(line.Id, word.Id, 10, 20));
        Cycle(doc, new SetLineTimesCommand(line.Id, 10, 30));
        Cycle(doc, new SetLineFlagsCommand(line.Id, isBg: true, isDuet: true, ignoreSync: true));
        Cycle(doc, new SetWordFlagsCommand(line.Id, word.Id, obscene: true, emptyBeat: 2));
        Cycle(doc, new SetTranslationCommand(line.Id, "trans"));
        Cycle(doc, new SetRomanizationCommand(line.Id, "roman"));
    }

    [Fact]
    public void FieldSetters_Cycle_OnLrc()
    {
        var doc = LoadLrc();
        var (line, word) = FirstWord(doc);
        Cycle(doc, new SetWordTextCommand(line.Id, word.Id, "edited"));
        Cycle(doc, new SetLineTimesCommand(line.Id, 5, 5000));
        Cycle(doc, new SetTranslationCommand(line.Id, "trans"));
    }

    [Fact]
    public void LinkUnlink_Cycle()
    {
        var doc = LoadTtml();
        var first = doc.Lines[0].Id;
        Cycle(doc, new LinkEndTimeCommand(first));
        Cycle(doc, new UnlinkEndTimeCommand(first));
    }

    [Fact]
    public void WordStructure_Cycle()
    {
        var doc = LoadTtml();
        var (line, word) = FirstWord(doc);
        var added = EditWord.New();
        added.Text = "extra";
        Cycle(doc, new AddWordCommand(line.Id, line.Words.Count, added));
        Cycle(doc, new RemoveWordsCommand(line.Id, new[] { added.Id }));
        var twoWordLine = doc.Lines.FirstOrDefault(l => l.Words.Count >= 2);
        if (twoWordLine is not null)
            Cycle(doc, new MergeWordsCommand(twoWordLine.Id,
                twoWordLine.Words.Take(2).Select(w => w.Id)));
        var longWord = doc.Lines.SelectMany(l => l.Words.Select(w => (l, w)))
            .FirstOrDefault(t => t.w.Text.Length >= 3);
        if (longWord != default)
            Cycle(doc, new SplitWordCommand(longWord.l.Id, longWord.w.Id, new[] { 1 }));
    }

    [Fact]
    public void LineStructure_Cycle()
    {
        var doc = LoadTtml();
        var added = EditLine.New();
        Cycle(doc, new AddLineCommand(1, added));
        Cycle(doc, new RemoveLinesCommand(new[] { added.Id }));
        var firstId = doc.Lines[0].Id;
        Cycle(doc, new MoveLineCommand(firstId, doc.Lines.Count - 1));
        Cycle(doc, new DuplicateLinesCommand(new[] { doc.Lines[0].Id }));
        Cycle(doc, new CombineLinesCommand(
            new[] { doc.Lines[0].Id, doc.Lines[1].Id }));
    }

    [Fact]
    public void MoveWordsToNewLine_Cycle()
    {
        var doc = LoadTtml();
        var (line, word) = FirstWord(doc);
        Cycle(doc, new MoveWordsToNewLineCommand(line.Id, new[] { word.Id }));
        var twoWord = doc.Lines.FirstOrDefault(l => l.Words.Count >= 2);
        if (twoWord is not null)
            Cycle(doc, new MoveFollowingWordsToNewLineCommand(
                twoWord.Id, twoWord.Words[0].Id));
    }

    [Fact]
    public void BulkAndComposite_Cycle()
    {
        var doc = LoadTtml();
        var (line, word) = FirstWord(doc);
        Cycle(doc, new TimeShiftCommand(Array.Empty<string>(), 100));
        Cycle(doc, new ReplaceWordTextCommand(word.Text, "zzz"));
        Cycle(doc, new CompositeCommand("two sets", new ILyricCommand[]
        {
            new SetWordTextCommand(line.Id, word.Id, "a"),
            new SetWordRomanCommand(line.Id, word.Id, "b"),
        }));
        Cycle(doc, new BulkReplaceCommand(new List<EditLine>(), new List<EditMetadata>()));
    }
}
