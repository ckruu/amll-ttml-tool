namespace AmllTtmlTool.WinUI.Services;

// ONE owner for the three lyric-display toggles (line translation, line
// romanization, per-word romanization). Backed by the persisted AppSettings
// keys; pills, overflow-menu copies and EditPage rows all follow Changed.
// Programmatic IsChecked sets on the pills fire Checked/Unchecked (repaint)
// but not Click, so syncing pill <- service cannot loop.
public sealed class LyricViewStateService
{
    public bool ShowTranslation
    {
        get => AppSettings.ShowTranslation;
        set
        {
            if (value == AppSettings.ShowTranslation) return;
            AppSettings.ShowTranslation = value;
            AppSettings.NotifyDisplayChanged();
            Changed?.Invoke();
        }
    }

    public bool ShowRomanization
    {
        get => AppSettings.ShowRomanization;
        set
        {
            if (value == AppSettings.ShowRomanization) return;
            AppSettings.ShowRomanization = value;
            AppSettings.NotifyDisplayChanged();
            Changed?.Invoke();
        }
    }

    public bool ShowWordRomanization
    {
        get => AppSettings.ShowWordRomanization;
        set
        {
            if (value == AppSettings.ShowWordRomanization) return;
            AppSettings.ShowWordRomanization = value;
            AppSettings.NotifyDisplayChanged();
            Changed?.Invoke();
        }
    }

    public event Action? Changed;
}
