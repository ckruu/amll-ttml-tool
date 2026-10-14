namespace AmllTtmlTool.Core.Lyrics.Commands;

// Line structure commands. Matches: Ribbon New (push), menus insert
// above/below/duplicate, drag reorder (move; Ctrl = copy with fresh ids),
// combine (contiguous only), global delete-selection.

public sealed class AddLineCommand : LyricCommand
{
    private readonly int _index;
    private EditLine? _line;

    public AddLineCommand(int index, EditLine? line = null)
    {
        _index = index;
        _line = line;
    }

    public override string Description => "Add line";

    public override void Apply(EditDocument document)
    {
        _line ??= EditLine.New();
        document.Lines.Insert(ClampIndex(_index, document.Lines.Count), _line);
    }

    public override void Revert(EditDocument document)
    {
        if (_line is not null) document.Lines.Remove(_line);
    }
}

public sealed class RemoveLinesCommand : LyricCommand
{
    private readonly List<string> _ids;
    private readonly List<(int Index, EditLine Line)> _removed = new();

    public RemoveLinesCommand(IEnumerable<string> ids) => _ids = ids.ToList();

    public override string Description => "Remove lines";

    public override void Apply(EditDocument document)
    {
        _removed.Clear();
        var wanted = new HashSet<string>(_ids);
        for (var i = document.Lines.Count - 1; i >= 0; i--)
        {
            if (wanted.Contains(document.Lines[i].Id))
            {
                _removed.Add((i, document.Lines[i]));
                document.Lines.RemoveAt(i);
            }
        }
        _removed.Reverse();
    }

    public override void Revert(EditDocument document)
    {
        foreach (var (index, line) in _removed)
            document.Lines.Insert(ClampIndex(index, document.Lines.Count), line);
    }
}

public sealed class MoveLineCommand : LyricCommand
{
    private readonly string _id;
    private readonly int _newIndex;
    private int _oldIndex = -1;

    public MoveLineCommand(string id, int newIndex)
    {
        _id = id;
        _newIndex = newIndex;
    }

    public override string Description => "Move line";

    public override void Apply(EditDocument document)
    {
        var line = document.RequireLine(_id);
        _oldIndex = document.Lines.IndexOf(line);
        document.Lines.RemoveAt(_oldIndex);
        document.Lines.Insert(ClampIndex(_newIndex, document.Lines.Count), line);
    }

    public override void Revert(EditDocument document)
    {
        var line = document.RequireLine(_id);
        document.Lines.Remove(line);
        document.Lines.Insert(ClampIndex(_oldIndex, document.Lines.Count), line);
    }
}

public sealed class CombineLinesCommand : LyricCommand
{
    private readonly List<string> _ids;
    private EditLine? _target;
    private List<EditWord>? _targetWordsBefore;
    private int _targetEndBefore;
    private readonly List<(int Index, EditLine Line)> _absorbed = new();

    public CombineLinesCommand(IEnumerable<string> ids) => _ids = ids.ToList();

    public override string Description => "Combine lines";

    public override void Apply(EditDocument document)
    {
        // combineEnabled: >= 2 selected AND contiguous (lyric-line-menu).
        var ordered = _ids.Select(id => document.Lines.IndexOf(document.RequireLine(id))).ToList();
        if (ordered.Count < 2 || ordered.Max() - ordered.Min() + 1 != ordered.Count)
            throw new InvalidOperationException("Combine needs >= 2 contiguous lines.");
        var min = ordered.Min();
        _target = document.Lines[min];
        _targetWordsBefore = _target.Words.Select(w => w.Clone()).ToList();
        _targetEndBefore = _target.EndTimeMs;
        _absorbed.Clear();
        for (var i = document.Lines.Count - 1; i >= 0; i--)
        {
            if (i != min && ordered.Contains(i))
            {
                _absorbed.Add((i, document.Lines[i]));
                document.Lines.RemoveAt(i);
            }
        }
        _absorbed.Reverse();
        foreach (var (_, line) in _absorbed)
            foreach (var w in line.Words)
                _target.Words.Add(w);
        // NOTE: target keeps its own start/translated/roman/carry; absorbed
        // lines' translations and bags are discarded, like the original
        // (only words move; target.endTime = last line's end).
        var lastAbsorbed = _absorbed[^1].Line;
        _target.EndTimeMs = lastAbsorbed.EndTimeMs;
    }

    public override void Revert(EditDocument document)
    {
        if (_target is null || _targetWordsBefore is null) return;
        _target.Words.Clear();
        foreach (var w in _targetWordsBefore) _target.Words.Add(w);
        _target.EndTimeMs = _targetEndBefore;
        foreach (var (index, line) in _absorbed)
            document.Lines.Insert(ClampIndex(index, document.Lines.Count), line);
    }
}

public sealed class DuplicateLinesCommand : LyricCommand
{
    private readonly List<string> _ids;
    private readonly List<EditLine> _clones = new();

    public DuplicateLinesCommand(IEnumerable<string> ids) => _ids = ids.ToList();

    public override string Description => "Duplicate lines";

    public override void Apply(EditDocument document)
    {
        _clones.Clear();
        // copyLines: clone with fresh ids inserted right after each source.
        foreach (var id in _ids)
        {
            var source = document.RequireLine(id);
            var clone = source.CloneWithNewIds();
            document.Lines.Insert(document.Lines.IndexOf(source) + 1, clone);
            _clones.Add(clone);
        }
    }

    public override void Revert(EditDocument document)
    {
        foreach (var clone in _clones) document.Lines.Remove(clone);
    }
}

// Drag reorder/copy (port of drag-reorder.ts reorderOrCopyLyricLines):
// the dragged block keeps its relative order; a move removes first with
// the drop index adjusted for dragged lines before it; a copy clones with
// fresh ids. One command = one history step. Timings never change here.
public sealed class ReorderLinesCommand : LyricCommand
{
    private readonly List<string> _ids;
    private readonly int _dropIndex;
    private readonly bool _copy;
    private readonly List<(int Index, EditLine Line)> _before = new();
    private readonly List<EditLine> _inserted = new();
    // Copy clones are minted once and reused by redo (same exactness rule
    // as MoveWordsCommand).
    private List<EditLine>? _clones;

    // IDs of the moved lines, or of the fresh clones for a copy (the UI
    // reselects these after the drop).
    public IReadOnlyList<string> ResultIds { get; private set; } = new List<string>();

    public ReorderLinesCommand(IEnumerable<string> lineIds, int dropIndex, bool copy)
    {
        _ids = lineIds.ToList();
        _dropIndex = dropIndex;
        _copy = copy;
    }

    public override string Description => _copy ? "Copy lines" : "Move lines";

    public override void Apply(EditDocument document)
    {
        var wanted = new HashSet<string>(_ids);
        var toMove = document.Lines.Where(l => wanted.Contains(l.Id)).ToList();
        if (toMove.Count == 0)
            throw new InvalidOperationException("No lines selected.");
        _before.Clear();
        for (var i = 0; i < document.Lines.Count; i++)
            _before.Add((i, document.Lines[i]));
        _inserted.Clear();
        List<EditLine> payload;
        List<EditLine> remaining = document.Lines.ToList();
        if (_copy)
        {
            payload = _clones ??= toMove.Select(l => l.CloneWithNewIds()).ToList();
        }
        else
        {
            remaining = document.Lines.Where(l => !wanted.Contains(l.Id)).ToList();
            payload = toMove;
        }
        var adjusted = _copy ? _dropIndex : _dropIndex - CountBefore(remaining);
        var safe = Math.Max(0, Math.Min(adjusted, remaining.Count));
        if (_copy)
        {
            // Plain inserts (no Clear: unrealized containers and scroll
            // position survive a copy).
            for (var i = 0; i < payload.Count; i++)
                document.Lines.Insert(safe + i, payload[i]);
        }
        else
        {
            // Single Clear + re-add for a move (one layout pass for the
            // watching panels instead of per-line churn).
            document.Lines.Clear();
            for (var i = 0; i < safe; i++) document.Lines.Add(remaining[i]);
            foreach (var l in payload) document.Lines.Add(l);
            for (var i = safe; i < remaining.Count; i++) document.Lines.Add(remaining[i]);
        }
        _inserted.AddRange(payload);
        ResultIds = payload.Select(l => l.Id).ToList();
    }

    private int CountBefore(List<EditLine> remaining)
    {
        // Dragged lines before the drop index (original coordinates).
        var remainingIds = new HashSet<string>(remaining.Select(l => l.Id));
        var count = 0;
        for (var i = 0; i < _dropIndex && i < _before.Count; i++)
            if (!remainingIds.Contains(_before[i].Line.Id)) count++;
        return count;
    }

    public override void Revert(EditDocument document)
    {
        if (_copy)
        {
            foreach (var l in _inserted) document.Lines.Remove(l);
            return;
        }
        // Restore the exact original order.
        document.Lines.Clear();
        foreach (var (_, line) in _before.OrderBy(t => t.Index)) document.Lines.Add(line);
    }
}
