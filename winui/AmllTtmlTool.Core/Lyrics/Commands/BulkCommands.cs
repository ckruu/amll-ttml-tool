namespace AmllTtmlTool.Core.Lyrics.Commands;

// Document-wide commands. Each is ONE history step: bulk replace (every
// import path funnels through it), time shift (TimeShift dialog), and
// find/replace word text (ReplaceWordDialog).

public sealed class BulkReplaceCommand : LyricCommand
{
    private readonly List<EditLine> _lines;
    private readonly List<EditMetadata> _metadata;
    private List<EditLine>? _linesBefore;
    private List<EditMetadata>? _metadataBefore;

    // Takes ownership of the given lists (import paths build them fresh).
    public BulkReplaceCommand(List<EditLine> lines, List<EditMetadata> metadata)
    {
        _lines = lines;
        _metadata = metadata;
    }

    public override string Description => "Replace document";

    public override void Apply(EditDocument document)
    {
        _linesBefore = document.Lines.ToList();
        _metadataBefore = document.Metadata.ToList();
        document.Lines.Clear();
        foreach (var l in _lines) document.Lines.Add(l);
        document.Metadata.Clear();
        foreach (var m in _metadata) document.Metadata.Add(m);
    }

    public override void Revert(EditDocument document)
    {
        if (_linesBefore is null || _metadataBefore is null) return;
        document.Lines.Clear();
        foreach (var l in _linesBefore) document.Lines.Add(l);
        document.Metadata.Clear();
        foreach (var m in _metadataBefore) document.Metadata.Add(m);
    }
}

public sealed class TimeShiftCommand : LyricCommand
{
    private readonly List<string> _lineIds; // empty = all lines
    private readonly int _offsetMs;
    private readonly List<(EditWord Word, int Start, int End)> _words = new();
    private readonly List<(EditLine Line, int Start, int End)> _lines = new();
    private readonly List<(EditRubySegment Ruby, int Start, int End)> _ruby = new();

    public TimeShiftCommand(IEnumerable<string> lineIds, int offsetMs)
    {
        _lineIds = lineIds.ToList();
        _offsetMs = offsetMs;
    }

    public override string Description => "Shift times";

    public override void Apply(EditDocument document)
    {
        _words.Clear();
        _lines.Clear();
        _ruby.Clear();
        var wanted = new HashSet<string>(_lineIds);
        var targets = _lineIds.Count == 0
            ? document.Lines.ToList()
            : document.Lines.Where(l => wanted.Contains(l.Id)).ToList();
        foreach (var line in targets)
        {
            _lines.Add((line, line.StartTimeMs, line.EndTimeMs));
            line.StartTimeMs = Math.Max(0, line.StartTimeMs + _offsetMs);
            line.EndTimeMs = Math.Max(0, line.EndTimeMs + _offsetMs);
            foreach (var word in line.Words)
            {
                _words.Add((word, word.StartTimeMs, word.EndTimeMs));
                word.StartTimeMs = Math.Max(0, word.StartTimeMs + _offsetMs);
                word.EndTimeMs = Math.Max(0, word.EndTimeMs + _offsetMs);
                foreach (var r in word.Ruby)
                {
                    _ruby.Add((r, r.StartTimeMs, r.EndTimeMs));
                    r.StartTimeMs = Math.Max(0, r.StartTimeMs + _offsetMs);
                    r.EndTimeMs = Math.Max(0, r.EndTimeMs + _offsetMs);
                }
            }
        }
    }

    public override void Revert(EditDocument document)
    {
        foreach (var (line, start, end) in _lines)
        {
            line.StartTimeMs = start;
            line.EndTimeMs = end;
        }
        foreach (var (word, start, end) in _words)
        {
            word.StartTimeMs = start;
            word.EndTimeMs = end;
        }
        foreach (var (ruby, start, end) in _ruby)
        {
            ruby.StartTimeMs = start;
            ruby.EndTimeMs = end;
        }
    }
}

public sealed class ReplaceWordTextCommand : LyricCommand
{
    private readonly string _target;
    private readonly string _replacement;
    private readonly bool _ignoreCase;
    private readonly List<string>? _onlyWordIds; // null = apply to all
    private readonly List<(EditWord Word, string Before)> _changed = new();

    public ReplaceWordTextCommand(string target, string replacement,
        bool ignoreCase = false, IEnumerable<string>? onlyWordIds = null)
    {
        _target = target;
        _replacement = replacement;
        _ignoreCase = ignoreCase;
        _onlyWordIds = onlyWordIds?.ToList();
    }

    public override string Description => "Replace word text";

    public override void Apply(EditDocument document)
    {
        _changed.Clear();
        // Single-word mode targets by id with an EXACT match (dialog line
        // 61); apply-to-all honors ignoreCase (dialog lines 48-50).
        var comparison = _onlyWordIds is not null
            ? StringComparison.Ordinal
            : _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var line in document.Lines)
        {
            foreach (var word in line.Words)
            {
                // Single mode: only the word equal to target; all mode:
                // every word whose text matches (exact or lowercase).
                // Either way only word text is replaced (ReplaceWordDialog).
                if (_onlyWordIds is not null && !_onlyWordIds.Contains(word.Id))
                    continue;
                if (!string.Equals(word.Text, _target, comparison))
                    continue;
                _changed.Add((word, word.Text));
                word.Text = _replacement;
            }
        }
    }

    public override void Revert(EditDocument document)
    {
        foreach (var (word, before) in _changed) word.Text = before;
    }
}

// Whole-metadata replacement (audio-metadata merge). Lines untouched;
// revert restores the exact previous list.
public sealed class SetMetadataCommand : LyricCommand
{
    private readonly List<EditMetadata> _metadata;
    private List<EditMetadata>? _before;

    public SetMetadataCommand(List<EditMetadata> metadata) => _metadata = metadata;

    public override string Description => "Set metadata";

    public override void Apply(EditDocument document)
    {
        _before = document.Metadata.ToList();
        document.Metadata.Clear();
        foreach (var m in _metadata) document.Metadata.Add(m);
    }

    public override void Revert(EditDocument document)
    {
        if (_before is null) return;
        document.Metadata.Clear();
        foreach (var m in _before) document.Metadata.Add(m);
    }
}
