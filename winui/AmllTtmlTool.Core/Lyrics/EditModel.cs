using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmllTtmlTool.Core.Lyrics;

// Mutable int-millisecond editing model. This is the ONLY thing the app and
// UI use; the immutable Models records are import/export interchange only.
// Field list mirrors src/types/ttml.ts (plus Unmapped carry-bags, which hold
// everything the UI does not edit so export can write it back unchanged).
// Times are int ms; ids are Guid-N strings like the original's uid() ids.

public sealed record EditEndTimeLink(int OriginalEndTime, int? OriginalNextStartTime);

public sealed partial class EditRubySegment : ObservableObject
{
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private int _startTimeMs;
    [ObservableProperty] private int _endTimeMs;
    [ObservableProperty] private int _emptyBeat;

    public EditRubySegment Clone() => new()
    {
        Text = Text, StartTimeMs = StartTimeMs,
        EndTimeMs = EndTimeMs, EmptyBeat = EmptyBeat,
    };
}

public sealed partial class EditWord : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private int _startTimeMs;
    [ObservableProperty] private int _endTimeMs;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private string _romanWord = string.Empty;
    [ObservableProperty] private bool _obscene;
    [ObservableProperty] private int _emptyBeat;
    [ObservableProperty] private bool _romanWarning;
    [ObservableProperty] private ObservableCollection<EditRubySegment> _ruby = new();

    // Opaque carry: per-word data the UI never edits (survives only on
    // untouched words — split/merge build fresh words exactly like the
    // original builds fresh newLyricWord()s).
    public Dictionary<string, string> Unmapped { get; } = new();

    public static EditWord New() => new();

    public EditWord Clone()
    {
        var c = new EditWord
        {
            Id = Id, StartTimeMs = StartTimeMs, EndTimeMs = EndTimeMs,
            Text = Text, RomanWord = RomanWord, Obscene = Obscene,
            EmptyBeat = EmptyBeat, RomanWarning = RomanWarning,
            Ruby = new ObservableCollection<EditRubySegment>(Ruby.Select(r => r.Clone())),
        };
        foreach (var kv in Unmapped) c.Unmapped[kv.Key] = kv.Value;
        return c;
    }

    public EditWord CloneWithNewId()
    {
        var c = Clone();
        c.Id = Guid.NewGuid().ToString("N");
        return c;
    }
}

public sealed partial class EditLine : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString("N");
    [ObservableProperty] private ObservableCollection<EditWord> _words = new();
    [ObservableProperty] private string _translatedLyric = string.Empty;
    [ObservableProperty] private string _romanLyric = string.Empty;
    [ObservableProperty] private bool _isBg;
    [ObservableProperty] private bool _isDuet;
    [ObservableProperty] private int _startTimeMs;
    [ObservableProperty] private int _endTimeMs;
    [ObservableProperty] private bool _ignoreSync;
    [ObservableProperty] private EditEndTimeLink? _endTimeLink;

    public Dictionary<string, string> Unmapped { get; } = new();

    public static EditLine New() => new();

    public EditLine Clone()
    {
        var c = new EditLine
        {
            Id = Id,
            Words = new ObservableCollection<EditWord>(Words.Select(w => w.Clone())),
            TranslatedLyric = TranslatedLyric, RomanLyric = RomanLyric,
            IsBg = IsBg, IsDuet = IsDuet,
            StartTimeMs = StartTimeMs, EndTimeMs = EndTimeMs,
            IgnoreSync = IgnoreSync, EndTimeLink = EndTimeLink,
        };
        foreach (var kv in Unmapped) c.Unmapped[kv.Key] = kv.Value;
        return c;
    }

    public EditLine CloneWithNewIds()
    {
        var c = Clone();
        c.Id = Guid.NewGuid().ToString("N");
        foreach (var w in c.Words) w.Id = Guid.NewGuid().ToString("N");
        return c;
    }

    // Port of normalize-line-time.ts: empty words -> no-op; a zero word
    // bound falls back to the line bound (|| semantics, kept deliberately).
    public void NormalizeTime()
    {
        if (Words.Count == 0) return;
        if (Words[0].StartTimeMs != 0) StartTimeMs = Words[0].StartTimeMs;
        if (Words[^1].EndTimeMs != 0) EndTimeMs = Words[^1].EndTimeMs;
    }
}

public sealed partial class EditMetadata : ObservableObject
{
    [ObservableProperty] private string _key = string.Empty;
    [ObservableProperty] private List<string> _values = new();
    [ObservableProperty] private bool _error;

    public EditMetadata Clone() => new()
    {
        Key = Key, Values = new List<string>(Values), Error = Error,
    };
}

public sealed partial class EditDocument : ObservableObject
{
    // Mirrors withHistory(lyricLinesAtom, 100): one executed command is one
    // history step; import/new-file push a step too; history is never
    // cleared by the model (the original has no reset API).
    public const int MaxHistoryDepth = 100;

    [ObservableProperty] private ObservableCollection<EditLine> _lines = new();
    [ObservableProperty] private ObservableCollection<EditMetadata> _metadata = new();

    // Document-level carry for interchange data with no UI surface
    // (agents tables, platform ids, raw properties — populated by the
    // Step 3 adapters, written back on export).
    public Dictionary<string, string> Unmapped { get; } = new();

    private readonly List<ILyricCommand> _undo = new();
    private readonly List<ILyricCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // Mirrors isDirtyAtom = canUndo exactly.
    public bool IsDirty => _undo.Count > 0;

    public int UndoDepth => _undo.Count;

    public void Execute(ILyricCommand command)
    {
        command.Apply(this);
        _undo.Add(command);
        if (_undo.Count > MaxHistoryDepth)
            _undo.RemoveAt(0);
        _redo.Clear();
        RefreshHistoryState();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Revert(this);
        _redo.Add(command);
        RefreshHistoryState();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        command.Apply(this);
        _undo.Add(command);
        RefreshHistoryState();
        return true;
    }

    public EditLine RequireLine(string id) =>
        Lines.FirstOrDefault(l => l.Id == id)
        ?? throw new InvalidOperationException($"Line not found: {id}.");

    public void RefreshHistoryState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(UndoDepth));
    }
}
