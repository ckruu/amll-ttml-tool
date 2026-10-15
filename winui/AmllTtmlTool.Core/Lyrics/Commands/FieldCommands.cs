namespace AmllTtmlTool.Core.Lyrics.Commands;

// Field-set commands. One committed field edit is one history step (the
// original commits on Enter/blur with no coalescing). Multi-select in the
// UI executes one command per field across the selection — still one step
// per commit because the UI batches them into a single composite... NOTE:
// the UI layer (Phase 2) must wrap multi-target edits in CompositeCommand;
// each command class below edits a single target.

// Runs several commands as ONE history step.
public sealed class CompositeCommand : LyricCommand
{
    private readonly string _description;
    private readonly List<ILyricCommand> _commands;

    public CompositeCommand(string description, IEnumerable<ILyricCommand> commands)
    {
        _description = description;
        _commands = commands.ToList();
    }

    public override string Description => _description;

    public override void Apply(EditDocument document)
    {
        foreach (var c in _commands) c.Apply(document);
    }

    public override void Revert(EditDocument document)
    {
        for (var i = _commands.Count - 1; i >= 0; i--) _commands[i].Revert(document);
    }
}

public sealed class SetWordTextCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly string _wordId;
    private readonly string _text;
    private string? _before;

    public SetWordTextCommand(string lineId, string wordId, string text)
    {
        _lineId = lineId;
        _wordId = wordId;
        _text = text;
    }

    public override string Description => "Set word text";

    public override void Apply(EditDocument document)
    {
        var word = document.RequireLine(_lineId).Words.FirstOrDefault(w => w.Id == _wordId)
            ?? throw new InvalidOperationException($"Word not found: {_wordId}.");
        _before = word.Text;
        word.Text = _text;
    }

    public override void Revert(EditDocument document)
    {
        if (_before is null) return;
        var word = document.RequireLine(_lineId).Words.FirstOrDefault(w => w.Id == _wordId)
            ?? throw new InvalidOperationException($"Word not found: {_wordId}.");
        word.Text = _before;
    }
}

public sealed class SetWordRomanCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly string _wordId;
    private readonly string _roman;
    private string? _before;

    public SetWordRomanCommand(string lineId, string wordId, string roman)
    {
        _lineId = lineId;
        _wordId = wordId;
        _roman = roman;
    }

    public override string Description => "Set word romanization";

    private EditWord Target(EditDocument document) =>
        document.RequireLine(_lineId).Words.FirstOrDefault(w => w.Id == _wordId)
        ?? throw new InvalidOperationException($"Word not found: {_wordId}.");

    public override void Apply(EditDocument document)
    {
        var word = Target(document);
        _before = word.RomanWord;
        word.RomanWord = _roman;
    }

    public override void Revert(EditDocument document) => Target(document).RomanWord = _before ?? string.Empty;
}

public sealed class SetWordTimesCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly string _wordId;
    private readonly int _start;
    private readonly int _end;
    private int _startBefore;
    private int _endBefore;
    private bool _applied;

    public SetWordTimesCommand(string lineId, string wordId, int startTimeMs, int endTimeMs)
    {
        _lineId = lineId;
        _wordId = wordId;
        _start = startTimeMs;
        _end = endTimeMs;
    }

    public override string Description => "Set word times";

    private EditWord Target(EditDocument document) =>
        document.RequireLine(_lineId).Words.FirstOrDefault(w => w.Id == _wordId)
        ?? throw new InvalidOperationException($"Word not found: {_wordId}.");

    // Plain time edits propagate NOTHING between words and lines.
    public override void Apply(EditDocument document)
    {
        var word = Target(document);
        _startBefore = word.StartTimeMs;
        _endBefore = word.EndTimeMs;
        _applied = true;
        word.StartTimeMs = _start;
        word.EndTimeMs = _end;
    }

    public override void Revert(EditDocument document)
    {
        if (!_applied) return;
        var word = Target(document);
        word.StartTimeMs = _startBefore;
        word.EndTimeMs = _endBefore;
    }
}

public sealed class SetLineTimesCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly int _start;
    private readonly int _end;
    private int _startBefore;
    private int _endBefore;
    private int? _nextStartBefore;
    private bool _applied;

    public SetLineTimesCommand(string lineId, int startTimeMs, int endTimeMs)
    {
        _lineId = lineId;
        _start = startTimeMs;
        _end = endTimeMs;
    }

    public override string Description => "Set line times";

    public override void Apply(EditDocument document)
    {
        var line = document.RequireLine(_lineId);
        _startBefore = line.StartTimeMs;
        _endBefore = line.EndTimeMs;
        _applied = true;
        line.StartTimeMs = _start;
        line.EndTimeMs = _end;
        // Linked chaining (lyric-line-view effect): every end change
        // re-snaps the next line's start while the link lives.
        if (line.EndTimeLink is not null)
        {
            var at = document.Lines.IndexOf(line);
            if (at >= 0 && at + 1 < document.Lines.Count)
            {
                var next = document.Lines[at + 1];
                _nextStartBefore = next.StartTimeMs;
                next.StartTimeMs = _end;
            }
        }
    }

    public override void Revert(EditDocument document)
    {
        if (!_applied) return;
        var line = document.RequireLine(_lineId);
        line.StartTimeMs = _startBefore;
        line.EndTimeMs = _endBefore;
        if (line.EndTimeLink is not null && _nextStartBefore is int nsb)
        {
            var at = document.Lines.IndexOf(line);
            if (at >= 0 && at + 1 < document.Lines.Count)
                document.Lines[at + 1].StartTimeMs = nsb;
        }
    }
}

public sealed class SetLineFlagsCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly bool? _isBg;
    private readonly bool? _isDuet;
    private readonly bool? _ignoreSync;
    private bool _bgBefore;
    private bool _duetBefore;
    private bool _ignoreBefore;
    private string? _agentBefore;
    private bool _applied;

    public SetLineFlagsCommand(string lineId, bool? isBg = null, bool? isDuet = null, bool? ignoreSync = null)
    {
        _lineId = lineId;
        _isBg = isBg;
        _isDuet = isDuet;
        _ignoreSync = ignoreSync;
    }

    public override string Description => "Set line flags";

    public override void Apply(EditDocument document)
    {
        var line = document.RequireLine(_lineId);
        _bgBefore = line.IsBg;
        _duetBefore = line.IsDuet;
        _ignoreBefore = line.IgnoreSync;
        _agentBefore = line.Unmapped.TryGetValue(EditLine.AgentKey, out var a) ? a : null;
        _applied = true;
        if (_isBg.HasValue) line.IsBg = _isBg.Value;
        if (_isDuet.HasValue) line.IsDuet = _isDuet.Value;
        if (_ignoreSync.HasValue) line.IgnoreSync = _ignoreSync.Value;
        // Toggling V2 or BG retires the imported raw agent: from here on the
        // line exports the standard form derived from its flags. (IgnoreSync
        // is sync-only and never touches the agent.)
        if (_isBg.HasValue || _isDuet.HasValue) line.Unmapped.Remove(EditLine.AgentKey);
    }

    public override void Revert(EditDocument document)
    {
        if (!_applied) return;
        var line = document.RequireLine(_lineId);
        line.IsBg = _bgBefore;
        line.IsDuet = _duetBefore;
        line.IgnoreSync = _ignoreBefore;
        if (_agentBefore is not null) line.Unmapped[EditLine.AgentKey] = _agentBefore;
        else line.Unmapped.Remove(EditLine.AgentKey);
    }
}

public sealed class SetWordFlagsCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly string _wordId;
    private readonly bool? _obscene;
    private readonly int? _emptyBeat;
    private bool _obsceneBefore;
    private int _emptyBeatBefore;
    private bool _applied;

    public SetWordFlagsCommand(string lineId, string wordId, bool? obscene = null, int? emptyBeat = null)
    {
        _lineId = lineId;
        _wordId = wordId;
        _obscene = obscene;
        _emptyBeat = emptyBeat;
    }

    public override string Description => "Set word flags";

    private EditWord Target(EditDocument document) =>
        document.RequireLine(_lineId).Words.FirstOrDefault(w => w.Id == _wordId)
        ?? throw new InvalidOperationException($"Word not found: {_wordId}.");

    public override void Apply(EditDocument document)
    {
        var word = Target(document);
        _obsceneBefore = word.Obscene;
        _emptyBeatBefore = word.EmptyBeat;
        _applied = true;
        if (_obscene.HasValue) word.Obscene = _obscene.Value;
        if (_emptyBeat.HasValue) word.EmptyBeat = _emptyBeat.Value;
    }

    public override void Revert(EditDocument document)
    {
        if (!_applied) return;
        var word = Target(document);
        word.Obscene = _obsceneBefore;
        word.EmptyBeat = _emptyBeatBefore;
    }
}

public sealed class SetTranslationCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly string _text;
    private string? _before;

    public SetTranslationCommand(string lineId, string text)
    {
        _lineId = lineId;
        _text = text;
    }

    public override string Description => "Set translation";

    public override void Apply(EditDocument document)
    {
        var line = document.RequireLine(_lineId);
        _before = line.TranslatedLyric;
        line.TranslatedLyric = _text;
    }

    public override void Revert(EditDocument document)
    {
        if (_before is not null) document.RequireLine(_lineId).TranslatedLyric = _before;
    }
}

public sealed class SetRomanizationCommand : LyricCommand
{
    private readonly string _lineId;
    private readonly string _text;
    private string? _before;

    public SetRomanizationCommand(string lineId, string text)
    {
        _lineId = lineId;
        _text = text;
    }

    public override string Description => "Set romanization";

    public override void Apply(EditDocument document)
    {
        var line = document.RequireLine(_lineId);
        _before = line.RomanLyric;
        line.RomanLyric = _text;
    }

    public override void Revert(EditDocument document)
    {
        if (_before is not null) document.RequireLine(_lineId).RomanLyric = _before;
    }
}

public sealed class LinkEndTimeCommand : LyricCommand
{
    private readonly string _lineId;

    public LinkEndTimeCommand(string lineId) => _lineId = lineId;

    public override string Description => "Link end time";

    public override void Apply(EditDocument document)
    {
        var at = document.Lines.IndexOf(document.RequireLine(_lineId));
        if (at < 0 || at + 1 >= document.Lines.Count)
            throw new InvalidOperationException("Link needs a next line.");
        var line = document.Lines[at];
        var next = document.Lines[at + 1];
        // Snapshots keep any pre-existing link's originals (line 461-466).
        line.EndTimeLink = new EditEndTimeLink(
            line.EndTimeLink?.OriginalEndTime ?? line.EndTimeMs,
            line.EndTimeLink?.OriginalNextStartTime ?? next.StartTimeMs);
        var desired = next.StartTimeMs;
        line.EndTimeMs = desired;
        next.StartTimeMs = desired;
    }

    public override void Revert(EditDocument document)
    {
        // Unlink restores snapshots (lines 427-449).
        new UnlinkEndTimeCommand(_lineId).Apply(document);
    }
}

public sealed class UnlinkEndTimeCommand : LyricCommand
{
    private readonly string _lineId;
    private EditEndTimeLink? _linkBefore;
    private int _endBefore;
    private int? _nextStartBefore;
    private bool _applied;

    public UnlinkEndTimeCommand(string lineId) => _lineId = lineId;

    public override string Description => "Unlink end time";

    public override void Apply(EditDocument document)
    {
        var at = document.Lines.IndexOf(document.RequireLine(_lineId));
        if (at < 0) return;
        var line = document.Lines[at];
        _linkBefore = line.EndTimeLink;
        var link = line.EndTimeLink;
        if (link is null) return;
        _endBefore = line.EndTimeMs;
        _nextStartBefore = at + 1 < document.Lines.Count
            ? document.Lines[at + 1].StartTimeMs
            : null;
        _applied = true;
        line.EndTimeMs = link.OriginalEndTime;
        if (at + 1 < document.Lines.Count && link.OriginalNextStartTime is int ns)
            document.Lines[at + 1].StartTimeMs = ns;
        line.EndTimeLink = null;
    }

    public override void Revert(EditDocument document)
    {
        if (!_applied) return;
        var at = document.Lines.IndexOf(document.RequireLine(_lineId));
        if (at < 0) return;
        var line = document.Lines[at];
        line.EndTimeMs = _endBefore;
        if (at + 1 < document.Lines.Count && _nextStartBefore is int ns)
            document.Lines[at + 1].StartTimeMs = ns;
        line.EndTimeLink = _linkBefore;
    }
}
