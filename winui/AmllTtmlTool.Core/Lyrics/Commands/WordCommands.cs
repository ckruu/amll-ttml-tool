namespace AmllTtmlTool.Core.Lyrics.Commands;

// Word structure commands. Matches lyric-word-menu.tsx + lyric-line-view.tsx:
// merge (join "", span, fresh word at first index), split (char-weight
// distribution), delete (filter + normalize iff changed), move-to-new-line
// (inherit BG/duet, normalize both), following-words variant (whitespace-only
// untimed word deleted first; clicked word MOVES).

public sealed class AddWordCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly int _index;
    private EditWord? _word;

    public AddWordCommand(string lineId, int index, EditWord? word = null)
    {
        _lineId = lineId;
        _index = index;
        _word = word;
    }

    public override string Description => "Add word";

    public override void Apply(EditDocument document)
    {
        _word ??= EditWord.New();
        var line = document.RequireLine(_lineId);
        line.Words.Insert(ClampIndex(_index, line.Words.Count), _word);
    }

    public override void Revert(EditDocument document)
    {
        if (_word is null) return;
        document.RequireLine(_lineId).Words.Remove(_word);
    }
}

public sealed class RemoveWordsCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly List<string> _ids;
    private readonly List<(int Index, EditWord Word)> _removed = new();
    private int _lineStartBefore;
    private int _lineEndBefore;

    public RemoveWordsCommand(string lineId, IEnumerable<string> ids)
    {
        _lineId = lineId;
        _ids = ids.ToList();
    }

    public override string Description => "Remove words";

    public override void Apply(EditDocument document)
    {
        var line = document.RequireLine(_lineId);
        _lineStartBefore = line.StartTimeMs;
        _lineEndBefore = line.EndTimeMs;
        _removed.Clear();
        var wanted = new HashSet<string>(_ids);
        var before = line.Words.Count;
        for (var i = line.Words.Count - 1; i >= 0; i--)
        {
            if (wanted.Contains(line.Words[i].Id))
            {
                _removed.Add((i, line.Words[i]));
                line.Words.RemoveAt(i);
            }
        }
        _removed.Reverse();
        if (line.Words.Count != before) line.NormalizeTime();
    }

    public override void Revert(EditDocument document)
    {
        var line = document.RequireLine(_lineId);
        foreach (var (index, word) in _removed)
            line.Words.Insert(ClampIndex(index, line.Words.Count), word);
        line.StartTimeMs = _lineStartBefore;
        line.EndTimeMs = _lineEndBefore;
    }
}

public sealed class MergeWordsCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly List<string> _ids;
    private List<EditWord>? _before;
    private int _lineStartBefore;
    private int _lineEndBefore;

    public MergeWordsCommand(string lineId, IEnumerable<string> ids)
    {
        _lineId = lineId;
        _ids = ids.ToList();
    }

    public override string Description => "Merge words";

    public override void Apply(EditDocument document)
    {
        var line = document.RequireLine(_lineId);
        var selected = line.Words.Where(w => _ids.Contains(w.Id)).ToList();
        if (selected.Count <= 1)
            throw new InvalidOperationException("Merge needs > 1 word in one line.");
        _before = line.Words.Select(w => w.Clone()).ToList();
        _lineStartBefore = line.StartTimeMs;
        _lineEndBefore = line.EndTimeMs;
        // join("") — NO space. Fresh word: per-word fields discarded.
        var merged = EditWord.New();
        merged.Text = string.Concat(selected.Select(w => w.Text));
        merged.StartTimeMs = selected[0].StartTimeMs;
        merged.EndTimeMs = selected[^1].EndTimeMs;
        var firstIndex = line.Words.IndexOf(selected[0]);
        var doomed = new HashSet<string>(_ids);
        for (var i = line.Words.Count - 1; i >= 0; i--)
            if (doomed.Contains(line.Words[i].Id))
                line.Words.RemoveAt(i);
        line.Words.Insert(ClampIndex(firstIndex, line.Words.Count), merged);
    }

    public override void Revert(EditDocument document)
    {
        if (_before is null) return;
        var line = document.RequireLine(_lineId);
        line.Words.Clear();
        foreach (var w in _before) line.Words.Add(w);
        line.StartTimeMs = _lineStartBefore;
        line.EndTimeMs = _lineEndBefore;
    }
}

public sealed class SplitWordCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly string _wordId;
    private readonly List<int> _splitIndices;
    private readonly double _punctuationWeight;
    private List<EditWord>? _before;

    // splitIndices are UTF-16 code-unit offsets (JS slice() semantics).
    public SplitWordCommand(string lineId, string wordId,
        IEnumerable<int> splitIndices,
        double punctuationWeight = SplitWeights.DefaultPunctuationWeight)
    {
        _lineId = lineId;
        _wordId = wordId;
        _splitIndices = splitIndices.ToList();
        _punctuationWeight = punctuationWeight;
    }

    public override string Description => "Split word";

    public override void Apply(EditDocument document)
    {
        var line = document.RequireLine(_lineId);
        var word = line.Words.FirstOrDefault(w => w.Id == _wordId)
            ?? throw new InvalidOperationException($"Word not found: {_wordId}.");
        _before = line.Words.Select(w => w.Clone()).ToList();
        var bounds = new SortedSet<int> { 0, word.Text.Length };
        foreach (var i in _splitIndices)
            if (i > 0 && i < word.Text.Length)
                bounds.Add(i);
        var list = bounds.ToList();
        var segments = new List<string>();
        for (var i = 0; i + 1 < list.Count; i++)
            segments.Add(word.Text.Substring(list[i], list[i + 1] - list[i]));
        var replacements = SplitWeights.DistributeTime(word, segments, _punctuationWeight);
        var at = line.Words.IndexOf(word);
        line.Words.RemoveAt(at);
        foreach (var w in replacements)
            line.Words.Insert(at++, w);
    }

    public override void Revert(EditDocument document)
    {
        if (_before is null) return;
        var line = document.RequireLine(_lineId);
        line.Words.Clear();
        foreach (var w in _before) line.Words.Add(w);
    }
}

public sealed class MoveWordsToNewLineCommand : LyricCommand
{
    private readonly string _anchorLineId;
    private readonly List<string> _ids;
    private EditLine? _created;
    private readonly List<(EditLine Line, List<EditWord> Before, int StartBefore, int EndBefore)> _sources = new();

    // Faithful to selectedToNewLine: words are collected across ALL lines in
    // document order; the new line is inserted after the anchor (clicked)
    // line and inherits the anchor's flags.
    public MoveWordsToNewLineCommand(string anchorLineId, IEnumerable<string> ids)
    {
        _anchorLineId = anchorLineId;
        _ids = ids.ToList();
    }

    public override string Description => "Move words to new line";

    public override void Apply(EditDocument document)
    {
        var wanted = new HashSet<string>(_ids);
        var anchor = document.RequireLine(_anchorLineId);
        _sources.Clear();
        var moving = new List<EditWord>();
        foreach (var line in document.Lines)
        {
            if (line.Words.Count == 0) continue;
            var takes = line.Words.Where(w => wanted.Contains(w.Id)).ToList();
            if (takes.Count == 0) continue;
            _sources.Add((line,
                line.Words.Select(w => w.Clone()).ToList(),
                line.StartTimeMs, line.EndTimeMs));
            // deletedAtBounds: first or last word taken -> normalize.
            var atBounds = wanted.Contains(line.Words[0].Id)
                || wanted.Contains(line.Words[^1].Id);
            foreach (var w in takes)
            {
                line.Words.Remove(w);
                moving.Add(w);
            }
            if (atBounds) line.NormalizeTime();
        }
        if (moving.Count == 0)
        {
            _sources.Clear();
            throw new InvalidOperationException("No words selected.");
        }
        _created = EditLine.New();
        _created.IsBg = anchor.IsBg;
        _created.IsDuet = anchor.IsDuet;
        foreach (var w in moving) _created.Words.Add(w);
        _created.NormalizeTime();
        document.Lines.Insert(document.Lines.IndexOf(anchor) + 1, _created);
    }

    public override void Revert(EditDocument document)
    {
        if (_created is null) return;
        document.Lines.Remove(_created);
        foreach (var (line, before, start, end) in _sources)
        {
            line.Words.Clear();
            foreach (var w in before) line.Words.Add(w);
            line.StartTimeMs = start;
            line.EndTimeMs = end;
        }
    }
}

public sealed class MoveFollowingWordsToNewLineCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly string _wordId;
    private List<EditWord>? _sourceBefore;
    private int _sourceStartBefore;
    private int _sourceEndBefore;
    private EditLine? _created;

    public MoveFollowingWordsToNewLineCommand(string lineId, string wordId)
    {
        _lineId = lineId;
        _wordId = wordId;
    }

    public override string Description => "Move following words to new line";

    public override void Apply(EditDocument document)
    {
        var source = document.RequireLine(_lineId);
        var at = -1;
        for (var i = 0; i < source.Words.Count; i++)
        {
            if (source.Words[i].Id == _wordId) { at = i; break; }
        }
        if (at < 0) throw new InvalidOperationException($"Word not found: {_wordId}.");
        _sourceBefore = source.Words.Select(w => w.Clone()).ToList();
        _sourceStartBefore = source.StartTimeMs;
        _sourceEndBefore = source.EndTimeMs;
        var clicked = source.Words[at];
        // Whitespace-only untimed word is deleted; otherwise the clicked
        // word MOVES with the rest (splice(wordIndex) takes it too).
        if (string.IsNullOrWhiteSpace(clicked.Text)
            && clicked.StartTimeMs == 0 && clicked.EndTimeMs == 0)
            source.Words.RemoveAt(at);
        var moving = source.Words.Skip(at).ToList();
        foreach (var w in moving) source.Words.Remove(w);
        _created = EditLine.New();
        _created.IsBg = source.IsBg;
        _created.IsDuet = source.IsDuet;
        foreach (var w in moving) _created.Words.Add(w);
        _created.NormalizeTime();
        source.NormalizeTime();
        document.Lines.Insert(document.Lines.IndexOf(source) + 1, _created);
    }

    public override void Revert(EditDocument document)
    {
        if (_created is null || _sourceBefore is null) return;
        document.Lines.Remove(_created);
        var source = document.RequireLine(_lineId);
        source.Words.Clear();
        foreach (var w in _sourceBefore) source.Words.Add(w);
        source.StartTimeMs = _sourceStartBefore;
        source.EndTimeMs = _sourceEndBefore;
    }
}

// Drag move/copy of words (port of the word onDrop in lyric-word-view):
// the payload keeps document order (line-major); a move deletes from the
// sources, a copy clones with fresh ids and reselects the clones.
// normalizeLineTime runs ONLY at touched bounds (deleted/inserted at a
// bound), exactly like the original — timings are otherwise untouched.
// One command = one history step.
public sealed class MoveWordsCommand : LyricCommand
{
    private readonly List<string> _ids;
    private readonly string _targetLineId;
    private readonly string? _targetWordId;
    private readonly bool _insertAfter;
    private readonly bool _copy;
    private readonly List<(EditLine Line, List<EditWord> Before, int StartBefore, int EndBefore)> _sources = new();
    private EditLine? _target;
    private List<EditWord>? _targetBefore;
    private int _targetStartBefore;
    private int _targetEndBefore;
    private List<EditWord>? _inserted;
    // Clones are minted ONCE (first Apply) and reused by redo: re-running
    // the producer would mint fresh ids every time and break exact
    // undo/redo symmetry (the original re-applies the same patch).
    private List<EditWord>? _clones;

    // IDs of the moved words, or of the fresh clones for a copy (the UI
    // reselects these after the drop).
    public IReadOnlyList<string> ResultIds { get; private set; } = new List<string>();

    public MoveWordsCommand(
        IEnumerable<string> wordIds, string targetLineId,
        string? targetWordId, bool insertAfter, bool copy)
    {
        _ids = wordIds.ToList();
        _targetLineId = targetLineId;
        _targetWordId = targetWordId;
        _insertAfter = insertAfter;
        _copy = copy;
    }

    public override string Description => _copy ? "Copy words" : "Move words";

    public override void Apply(EditDocument document)
    {
        var wanted = new HashSet<string>(_ids);
        var target = document.RequireLine(_targetLineId);
        _sources.Clear();
        var moving = new List<EditWord>();
        foreach (var line in document.Lines)
        {
            if (line.Words.Count == 0) continue;
            var takes = line.Words.Where(w => wanted.Contains(w.Id)).ToList();
            if (takes.Count == 0) continue;
            _sources.Add((line,
                line.Words.Select(w => w.Clone()).ToList(),
                line.StartTimeMs, line.EndTimeMs));
            if (!_copy)
            {
                var atBounds = wanted.Contains(line.Words[0].Id)
                    || wanted.Contains(line.Words[^1].Id);
                foreach (var w in takes) line.Words.Remove(w);
                moving.AddRange(takes);
                if (atBounds) line.NormalizeTime();
            }
            else
            {
                moving.AddRange(takes);
            }
        }
        if (moving.Count == 0)
        {
            _sources.Clear();
            throw new InvalidOperationException("No words selected.");
        }
        List<EditWord> payload = _copy
            ? _clones ??= moving.Select(w => w.CloneWithNewId()).ToList()
            : moving;
        // Resolve the insert position AFTER removal (indices shifted).
        int pos;
        if (_targetWordId is null)
        {
            pos = target.Words.Count;
        }
        else
        {
            pos = target.Words.ToList().FindIndex(w => w.Id == _targetWordId);
            if (pos < 0)
                throw new InvalidOperationException($"Target word not found: {_targetWordId}.");
            if (_insertAfter) pos++;
        }
        _target = target;
        _targetBefore = target.Words.Select(w => w.Clone()).ToList();
        _targetStartBefore = target.StartTimeMs;
        _targetEndBefore = target.EndTimeMs;
        foreach (var w in payload) target.Words.Insert(pos++, w);
        _inserted = payload;
        ResultIds = payload.Select(w => w.Id).ToList();
        if (pos - payload.Count == 0 || pos == target.Words.Count)
            target.NormalizeTime();
    }

    public override void Revert(EditDocument document)
    {
        if (_inserted is null || _target is null || _targetBefore is null) return;
        foreach (var w in _inserted) _target.Words.Remove(w);
        _target.Words.Clear();
        foreach (var w in _targetBefore) _target.Words.Add(w);
        _target.StartTimeMs = _targetStartBefore;
        _target.EndTimeMs = _targetEndBefore;
        foreach (var (line, before, start, end) in _sources)
        {
            line.Words.Clear();
            foreach (var w in before) line.Words.Add(w);
            line.StartTimeMs = start;
            line.EndTimeMs = end;
        }
    }
}
