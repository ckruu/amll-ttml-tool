namespace AmllTtmlTool.WinUI.Services;

// Line/word selection for the Edit tab. UI state only (never in Core):
// the selected lines drive bulk ops, the primary line+word drives the
// ribbon (Word group follows the focused word). Multi-select mirrors the
// original's selectedLines/selectedWords sets; single-click flows keep the
// old single-select behavior (clear others, set primary).
public sealed class SelectionService
{
    private readonly HashSet<string> _lineIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wordIds = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> SelectedLineIds => _lineIds;
    public IReadOnlyCollection<string> SelectedWordIds => _wordIds;

    // Primary = last focused (drives the ribbon + single-target commands).
    public string? SelectedLineId { get; private set; }
    public string? SelectedWordId { get; private set; }

    public event Action? Changed;

    public void SelectLine(string? lineId)
    {
        _lineIds.Clear();
        _wordIds.Clear();
        if (lineId is not null) _lineIds.Add(lineId);
        if (SelectedLineId == lineId && SelectedWordId is null) return;
        SelectedLineId = lineId;
        SelectedWordId = null;
        Changed?.Invoke();
    }

    public void SelectWord(string lineId, string wordId)
    {
        _lineIds.Clear();
        _wordIds.Clear();
        _lineIds.Add(lineId);
        _wordIds.Add(wordId);
        if (SelectedLineId == lineId && SelectedWordId == wordId) return;
        SelectedLineId = lineId;
        SelectedWordId = wordId;
        Changed?.Invoke();
    }

    public void ToggleLine(string lineId)
    {
        // Word pruning needs line membership (unknown here): callers follow
        // with RetainWords(...). Primary re-anchors to the toggled line.
        if (!_lineIds.Remove(lineId)) _lineIds.Add(lineId);
        SelectedLineId = lineId;
        SelectedWordId = null;
        Changed?.Invoke();
    }

    public void ToggleWord(string lineId, string wordId)
    {
        _lineIds.Add(lineId);
        if (!_wordIds.Remove(wordId)) _wordIds.Add(wordId);
        SelectedLineId = lineId;
        SelectedWordId = wordId;
        Changed?.Invoke();
    }

    // Shift-range within one line (original: min/max over the already
    // selected words of that line plus the clicked index, unioned in).
    public void SelectWordRange(string lineId, IEnumerable<string> wordIds, string primaryWordId)
    {
        _lineIds.Add(lineId);
        foreach (var id in wordIds) _wordIds.Add(id);
        SelectedLineId = lineId;
        SelectedWordId = primaryWordId;
        Changed?.Invoke();
    }

    // Primary-only moves (focus/keyboard follow without touching the sets:
    // the original never changes selection on focus; clicks own the sets).
    // The line is ensured selected (a word implies its line); the word set
    // is left alone so multi-selections survive tabbing.
    public void AnchorLine(string lineId)
    {
        _lineIds.Add(lineId);
        SelectedLineId = lineId;
        PrunePrimary();
        Changed?.Invoke();
    }

    public void AnchorWord(string lineId, string wordId)
    {
        _lineIds.Add(lineId);
        SelectedLineId = lineId;
        // Primary may point outside the word set (focus != selection); the
        // ribbon follows it while ops act on the set. Never prune here.
        SelectedWordId = wordId;
        if (SelectedLineId is not null && !_lineIds.Contains(SelectedLineId))
            SelectedLineId = _lineIds.FirstOrDefault();
        Changed?.Invoke();
    }

    public void SelectAllLines(IEnumerable<string> lineIds)
    {
        _lineIds.Clear();
        foreach (var id in lineIds) _lineIds.Add(id);
        _wordIds.Clear();
        SelectedLineId = _lineIds.FirstOrDefault();
        SelectedWordId = null;
        Changed?.Invoke();
    }

    public void SelectAllWords(string lineId, IEnumerable<string> wordIds)
    {
        _lineIds.Clear();
        _lineIds.Add(lineId);
        _wordIds.Clear();
        foreach (var id in wordIds) _wordIds.Add(id);
        SelectedLineId = lineId;
        SelectedWordId = _wordIds.FirstOrDefault();
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_lineIds.Count == 0 && _wordIds.Count == 0
            && SelectedLineId is null && SelectedWordId is null) return;
        _lineIds.Clear();
        _wordIds.Clear();
        SelectedLineId = null;
        SelectedWordId = null;
        Changed?.Invoke();
    }

    // Deterministic single-line pin (no-op when already exact, so surviving
    // word selections are never cleared by a re-select — the B5 rule).
    // Service writers that bypass the ListView must still converge, so a
    // reselect always lands exactly on {lineId} + its own words.
    public void PinLine(string lineId, Func<string, string?> lineOfWord)
    {
        if (_lineIds.Count == 1 && _lineIds.Contains(lineId) && SelectedLineId == lineId)
            return;
        _lineIds.Clear();
        _lineIds.Add(lineId);
        SelectedLineId = lineId;
        RetainWords(lineOfWord);
    }

    // Exact-set replacement (mirrors what a ListView replace-diff converges
    // to; primary anchors to the last id like SyncLineSet).
    public void SetLines(IEnumerable<string> lineIds, Func<string, string?> lineOfWord)
    {
        _lineIds.Clear();
        string? last = null;
        foreach (var id in lineIds) { _lineIds.Add(id); last = id; }
        SelectedLineId = last ?? _lineIds.FirstOrDefault();
        SelectedWordId = null;
        RetainWords(lineOfWord);
    }

    // Additive range (mirrors a native shift-extend's convergence: the word
    // primary clears exactly like SyncLineSet does on list adds).
    public void AddLines(IEnumerable<string> lineIds, string primaryLineId, Func<string, string?> lineOfWord)
    {
        foreach (var id in lineIds) _lineIds.Add(id);
        SelectedLineId = primaryLineId;
        SelectedWordId = null;
        RetainWords(lineOfWord);
    }

    // Syncs the line set from the ListView (Extended mode). Words of
    // removed lines drop; primary re-anchors inside the surviving set.
    public void SyncLineSet(
        IEnumerable<string> added, IEnumerable<string> removed,
        Func<string, string?> lineOfWord)
    {
        var addedList = added.ToList();
        foreach (var id in addedList) _lineIds.Add(id);
        foreach (var id in removed) _lineIds.Remove(id);
        // Anchor the primary to the newest list selection (drives the
        // ribbon for pattern-driven selects); words clear on line change.
        if (addedList.Count > 0)
        {
            SelectedLineId = addedList[^1];
            SelectedWordId = null;
        }
        RetainWords(lineOfWord);
    }

    // Drops word ids whose line is no longer selected. The service does not
    // know line membership, so callers pass it after line-set changes.
    public void RetainWords(Func<string, string?> lineOfWord)
    {
        _wordIds.RemoveWhere(w => lineOfWord(w) is string line && !_lineIds.Contains(line));
        PrunePrimary();
        Changed?.Invoke();
    }

    private void PrunePrimary()
    {
        if (SelectedLineId is not null && !_lineIds.Contains(SelectedLineId))
            SelectedLineId = _lineIds.FirstOrDefault();
        if (SelectedWordId is not null && !_wordIds.Contains(SelectedWordId))
            SelectedWordId = _wordIds.FirstOrDefault();
    }
}
