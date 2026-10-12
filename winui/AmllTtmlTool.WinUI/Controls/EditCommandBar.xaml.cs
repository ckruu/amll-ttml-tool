using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AmllTtmlTool.WinUI.Controls
{
    public sealed partial class EditCommandBar : UserControl
    {
        public event Action? NewLineRequested;
        public event Action? ImportRequested;

        private SelectionService? _selection;
        private LyricStore? _store;

        // Step 4 (render only): ribbon mirrors the selection, read-only.
        // Toggles stay disabled throughout this phase (clicking must not
        // flip state with no model effect); Phase 3 enables them for real.
        public void BindSelection(SelectionService selection, LyricStore store)
        {
            if (_selection is not null) _selection.Changed -= RefreshSelectionDisplay;
            if (_store is not null) _store.Changed -= RefreshSelectionDisplay;
            _selection = selection;
            _store = store;
            _selection.Changed += RefreshSelectionDisplay;
            _store.Changed += RefreshSelectionDisplay;
            RefreshSelectionDisplay();
        }

        private void RefreshSelectionDisplay()
        {
            EditLine? line = null;
            EditWord? word = null;
            if (_selection?.SelectedLineId is string lineId && _store is not null)
            {
                line = _store.Document.Lines.FirstOrDefault(l => l.Id == lineId);
                if (line is not null && _selection.SelectedWordId is string wordId)
                    word = line.Words.FirstOrDefault(w => w.Id == wordId);
            }
            LineStartBox.Text = line is null ? "" : LyricTime.Format(line.StartTimeMs);
            LineEndBox.Text = line is null ? "" : LyricTime.Format(line.EndTimeMs);
            BgToggle.IsChecked = line?.IsBg == true;
            DuetToggle.IsChecked = line?.IsDuet == true;
            IgnoreSyncToggle.IsChecked = line?.IgnoreSync == true;
            WordStartBox.Text = word is null ? "" : LyricTime.Format(word.StartTimeMs);
            WordEndBox.Text = word is null ? "" : LyricTime.Format(word.EndTimeMs);
            WordContentBox.Text = word?.Text ?? "";
            ObsceneToggle.IsChecked = word?.Obscene == true;
            ObsceneToggle.Content = word?.Obscene == true ? "Yes" : "No";
            TransBox.Text = line?.TranslatedLyric ?? "";
            RomanBox.Text = line?.RomanLyric ?? "";
            var hasLine = line is not null;
            var hasWord = word is not null;
            LineStartBox.IsEnabled = hasLine;
            LineEndBox.IsEnabled = hasLine;
            BgToggle.IsEnabled = hasLine;
            DuetToggle.IsEnabled = hasLine;
            IgnoreSyncToggle.IsEnabled = hasLine;
            WordStartBox.IsEnabled = hasWord;
            WordEndBox.IsEnabled = hasWord;
            WordContentBox.IsEnabled = hasWord;
            ObsceneToggle.IsEnabled = false;
            TransBox.IsEnabled = hasLine;
            RomanBox.IsEnabled = hasLine;
        }

        public EditCommandBar()
        {
            this.InitializeComponent();
            this.Loaded += (_, _) =>
            {
                OutlineToggle.IsChecked = AppSettings.ShowOutline;
                BpmToggle.IsChecked = AppSettings.ShowBpm;
                PreviewToggle.IsChecked = AppSettings.ShowPreview;
                FollowToggle.IsChecked = AppSettings.FollowPlayback;
                if (ObsceneToggle.IsChecked == true) ObsceneToggle.Content = "Yes";
                else { ObsceneToggle.IsChecked = false; ObsceneToggle.Content = "No"; }
                ApplyMasterToggles();
                AppSettings.DisplayChanged += ApplyMasterToggles;
            };
            this.Unloaded += (_, _) =>
            {
                AppSettings.DisplayChanged -= ApplyMasterToggles;
                if (_selection is not null) _selection.Changed -= RefreshSelectionDisplay;
                if (_store is not null) _store.Changed -= RefreshSelectionDisplay;
            };
        }

        private void ApplyMasterToggles()
        {
            var showTrans = AppSettings.ShowTranslation;
            var showRoman = AppSettings.ShowRomanization;
            var transVisibility = showTrans ? Visibility.Visible : Visibility.Collapsed;
            var romanVisibility = showRoman ? Visibility.Visible : Visibility.Collapsed;
            TransLabel.Visibility = TransBox.Visibility = transVisibility;
            RomanLabel.Visibility = RomanBox.Visibility = romanVisibility;
            // The flyout holds separate copies (same text via TwoWay binding),
            // so mirror the same visibility here. This also runs while the
            // flyout is open, since DisplayChanged fires on toggle change.
            FlyoutTransLabel.Visibility = FlyoutTransBox.Visibility = transVisibility;
            FlyoutRomanLabel.Visibility = FlyoutRomanBox.Visibility = romanVisibility;
            var showSection = showTrans || showRoman;
            // The tier setters collapse SecondaryGroup below Wide, but this
            // handler re-runs on every toggle change and tier change, so the
            // group is only ever shown in the Wide tier. The overflow button
            // is the single owner of its own visibility: shown exactly when
            // the section has content but lives in the flyout.
            var wide = WidthTiers?.CurrentState?.Name is null or "Wide";
            SecondaryGroup.Visibility = showSection && wide ? Visibility.Visible : Visibility.Collapsed;
            SecondaryOverflowButton.Visibility =
                showSection && !wide ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnWidthTierChanged(object sender, VisualStateChangedEventArgs e) =>
            ApplyMasterToggles();

        private void OnNewLineClick(object sender, RoutedEventArgs e) =>
            NewLineRequested?.Invoke();

        private void OnImportClick(object sender, RoutedEventArgs e) =>
            ImportRequested?.Invoke();

        private void OnStubClick(object sender, RoutedEventArgs e)
        {
        }

        private void OnObsceneClick(object sender, RoutedEventArgs e) =>
            ObsceneToggle.Content = ObsceneToggle.IsChecked == true ? "Yes" : "No";

        private void OnViewToggleClick(object sender, RoutedEventArgs e)
        {
            AppSettings.ShowOutline = OutlineToggle.IsChecked == true;
            AppSettings.ShowBpm = BpmToggle.IsChecked == true;
            AppSettings.ShowPreview = PreviewToggle.IsChecked == true;
            AppSettings.FollowPlayback = FollowToggle.IsChecked == true;
        }
    }
}
