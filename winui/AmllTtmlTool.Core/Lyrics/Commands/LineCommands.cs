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
