namespace AmllTtmlTool.WinUI.Services;

// Line/word selection for the Edit tab. UI state only (never in Core):
// the selected line drives the ribbon (Phase 2 step 4) and later phases.
// Single selection; a chip click also records the word.
public sealed class SelectionService
{
    public string? SelectedLineId { get; private set; }
    public string? SelectedWordId { get; private set; }

    public event Action? Changed;

    public void SelectLine(string? lineId)
    {
        if (SelectedLineId == lineId && SelectedWordId is null) return;
        SelectedLineId = lineId;
        SelectedWordId = null;
        Changed?.Invoke();
    }

    public void SelectWord(string lineId, string wordId)
    {
        if (SelectedLineId == lineId && SelectedWordId == wordId) return;
        SelectedLineId = lineId;
        SelectedWordId = wordId;
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (SelectedLineId is null && SelectedWordId is null) return;
        SelectedLineId = null;
        SelectedWordId = null;
        Changed?.Invoke();
    }
}
