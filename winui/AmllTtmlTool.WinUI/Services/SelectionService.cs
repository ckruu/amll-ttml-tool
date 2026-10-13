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

    // Syncs the line set from the ListView (Extended mode). Words of
    // removed lines drop; primary re-anchors inside the surviving set.
    public void SyncLineSet(
        IEnumerable<string> added, IEnumerable<string> removed,
        Func<string, string?> lineOfWord)
    {
        foreach (var id in added) _lineIds.Add(id);
        foreach (var id in removed) _lineIds.Remove(id);
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
