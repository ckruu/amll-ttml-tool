using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Lyrics.Commands;
using AmllTtmlTool.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AmllTtmlTool.WinUI.Controls
{
    public sealed partial class EditCommandBar : UserControl
    {
        public event Action? NewLineRequested;
        public event Action? ImportRequested;

        private SelectionService? _selection;
        private LyricStore? _store;

        // Shared by both end fields (original showEndTimeAsDurationAtom).
        private bool _durationMode;
        private TextBox? _focusedBox;
        private readonly HashSet<TextBox> _composing = new();
        private Brush? _errorBrush;

        public void BindSelection(SelectionService selection, LyricStore store)
        {
            if (_selection is not null) _selection.Changed -= RefreshSelectionDisplay;
            if (_store is not null) _store.Changed -= RefreshSelectionDisplay;
            _selection = selection;
            _store = store;
            _selection.Changed += RefreshSelectionDisplay;
            _store.Changed += RefreshSelectionDisplay;
            WireField(LineStartBox);
            WireField(LineEndBox);
            WireField(WordStartBox);
            WireField(WordEndBox);
            WireField(WordContentBox);
            WireField(WordRomanBox);
            WireField(EmptyBeatBox);
            WireField(TransBox);
            WireField(RomanBox);
            RefreshSelectionDisplay();
        }

        private void WireField(TextBox box)
        {
            box.KeyDown -= OnFieldKeyDown;
            box.LostFocus -= OnFieldLostFocus;
            box.GotFocus -= OnFieldGotFocus;
            box.TextCompositionStarted -= OnFieldCompositionStarted;
            box.TextCompositionEnded -= OnFieldCompositionEnded;
            box.KeyDown += OnFieldKeyDown;
            box.LostFocus += OnFieldLostFocus;
            box.GotFocus += OnFieldGotFocus;
            box.TextCompositionStarted += OnFieldCompositionStarted;
            box.TextCompositionEnded += OnFieldCompositionEnded;
        }

        private void OnFieldGotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box)
            {
                _focusedBox = box;
                box.ClearValue(TextBox.BorderBrushProperty);
            }
        }

        private void OnFieldLostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box)
            {
                if (_focusedBox == box) _focusedBox = null;
                CommitField(box);
            }
        }

        private void OnFieldCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args) =>
            _composing.Add(sender);

        private void OnFieldCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args) =>
            _composing.Remove(sender);

        private void OnFieldKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (sender is not TextBox box) return;
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (_composing.Contains(box)) return;
                CommitField(box);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                RefreshSelectionDisplay();
                e.Handled = true;
            }
        }

        private (EditLine? Line, EditWord? Word) Targets()
        {
            EditLine? line = null;
            EditWord? word = null;
            if (_selection?.SelectedLineId is string lineId && _store is not null)
            {
                line = _store.Document.Lines.FirstOrDefault(l => l.Id == lineId);
                if (line is not null && _selection.SelectedWordId is string wordId)
                    word = line.Words.FirstOrDefault(w => w.Id == wordId);
            }
            return (line, word);
        }

        private void CommitField(TextBox box)
        {
            if (_store is null) return;
            if (_composing.Contains(box)) return;
            var (line, word) = Targets();
            var raw = box.Text;
            if (box == LineStartBox && line is not null)
                CommitTime(raw, v => new SetLineTimesCommand(line.Id, v, line.EndTimeMs),
                    box, LyricTime.Format(line.StartTimeMs));
            else if (box == LineEndBox && line is not null)
                CommitEnd(line: true, raw, line, word: null, box);
            else if (box == WordStartBox && line is not null && word is not null)
                CommitTime(raw, v => new SetWordTimesCommand(line.Id, word.Id, v, word.EndTimeMs),
                    box, LyricTime.Format(word.StartTimeMs));
            else if (box == WordEndBox && line is not null && word is not null)
                CommitEnd(line: false, raw, line, word, box);
            else if (box == WordContentBox && line is not null && word is not null)
            {
                if (raw != word.Text)
                    _store.Execute(new SetWordTextCommand(line.Id, word.Id, raw));
            }
            else if (box == WordRomanBox && line is not null && word is not null)
            {
                if (raw != word.RomanWord)
                    _store.Execute(new SetWordRomanCommand(line.Id, word.Id, raw));
            }
            else if (box == EmptyBeatBox && line is not null && word is not null)
            {
                var parsed = int.TryParse(raw.Trim(), out var n) ? n : 0;
                if (parsed != word.EmptyBeat)
                    _store.Execute(new SetWordFlagsCommand(line.Id, word.Id, emptyBeat: parsed));
            }
            else if (box == TransBox && line is not null)
            {
                if (raw != line.TranslatedLyric)
                    _store.Execute(new SetTranslationCommand(line.Id, raw));
            }
            else if (box == RomanBox && line is not null)
            {
                if (raw != line.RomanLyric)
                    _store.Execute(new SetRomanizationCommand(line.Id, raw));
            }
        }

        private void CommitTime(string raw, Func<int, ILyricCommand> command, TextBox box, string revertText)
        {
            if (_store is null) return;
            int parsed;
            try { parsed = LyricTime.Parse(raw.Trim()); }
            catch (FormatException) { FlagInvalid(box, revertText); return; }
            _store.Execute(command(parsed));
        }

        // Duration-mode port of edit-mode.tsx onInputFinished: delta (+/-)
        // or absolute duration; invalid/NaN ignored; non-positive absolute
        // ignored; clamp end >= start; delta pushes an abutting next word.
        private void CommitEnd(bool line, string raw, EditLine editLine, EditWord? word, TextBox box)
        {
            if (_store is null) return;
            if (!_durationMode)
            {
                if (line) CommitTime(raw, v => new SetLineTimesCommand(editLine.Id, editLine.StartTimeMs, v),
                    box, LyricTime.Format(editLine.EndTimeMs));
                else if (word is not null) CommitTime(raw, v => new SetWordTimesCommand(editLine.Id, word.Id, word.StartTimeMs, v),
                    box, LyricTime.Format(word.EndTimeMs));
                return;
            }
            var trimmed = raw.Trim();
            var isDelta = trimmed.StartsWith("+") || trimmed.StartsWith("-");
            if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                || !double.IsFinite(parsed)) return;
            if (!isDelta && parsed <= 0) return;
            var delta = (int)parsed;
            if (line)
            {
                var newEndRaw = isDelta ? editLine.EndTimeMs + delta : editLine.StartTimeMs + delta;
                _store.Execute(new SetLineTimesCommand(editLine.Id, editLine.StartTimeMs,
                    Math.Max(editLine.StartTimeMs, newEndRaw)));
            }
            else if (word is not null)
            {
                var newEndRaw = isDelta ? word.EndTimeMs + delta : word.StartTimeMs + delta;
                var newEnd = Math.Max(word.StartTimeMs, newEndRaw);
                var cmds = new List<ILyricCommand>
                    { new SetWordTimesCommand(editLine.Id, word.Id, word.StartTimeMs, newEnd) };
                if (isDelta)
                {
                    var at = editLine.Words.ToList().FindIndex(w => w.Id == word.Id);
                    if (at >= 0 && at + 1 < editLine.Words.Count)
                    {
                        var next = editLine.Words[at + 1];
                        var nextSelected = _selection?.SelectedWordIds.Contains(next.Id) == true;
                        if (word.EndTimeMs == next.StartTimeMs
                            && (newEnd <= next.EndTimeMs || nextSelected))
                            cmds.Add(new SetWordTimesCommand(editLine.Id, next.Id, newEnd, next.EndTimeMs));
                    }
                }
                _store.Execute(cmds.Count == 1 ? cmds[0]
                    : new CompositeCommand("Set word end", cmds));
            }
        }

        private void FlagInvalid(TextBox box, string revertText)
        {
            // Standard invalid state: system error brush + revert on commit.
            // The red border survives until the box is focused again (the
            // model did not change, so no refresh would clear it for us).
            _errorBrush ??= (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            box.Text = revertText;
            box.BorderBrush = _errorBrush;
        }

        private void RefreshSelectionDisplay()
        {
            var (line, word) = Targets();
            SetBox(LineStartBox, line is null ? "" : LyricTime.Format(line.StartTimeMs));
            SetBox(LineEndBox, LineEndText(line));
            BgToggle.IsChecked = line?.IsBg == true;
            DuetToggle.IsChecked = line?.IsDuet == true;
            IgnoreSyncToggle.IsChecked = line?.IgnoreSync == true;
            SetBox(WordStartBox, word is null ? "" : LyricTime.Format(word.StartTimeMs));
            SetBox(WordEndBox, WordEndText(line, word));
            SetBox(WordContentBox, word?.Text ?? "");
            SetBox(WordRomanBox, word?.RomanWord ?? "");
            SetBox(EmptyBeatBox, word is null ? "" : word.EmptyBeat.ToString());
            ObsceneToggle.IsChecked = word?.Obscene == true;
            ObsceneToggle.Content = word?.Obscene == true ? "Yes" : "No";
            SetBox(TransBox, MultipleOr(line, l => l.TranslatedLyric));
            SetBox(RomanBox, MultipleOr(line, l => l.RomanLyric));
            LineEndModeLabel.Text = _durationMode ? "Duration" : "End";
            WordEndModeLabel.Text = _durationMode ? "Duration" : "End";
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
            WordRomanBox.IsEnabled = hasWord;
            EmptyBeatBox.IsEnabled = hasWord;
            ObsceneToggle.IsEnabled = hasWord;
            TransBox.IsEnabled = hasLine;
            RomanBox.IsEnabled = hasLine;
            // start > end is display-only red (original): still commits.
            MarkRange(LineStartBox, LineEndBox, line?.StartTimeMs, line?.EndTimeMs, hasLine);
            MarkRange(WordStartBox, WordEndBox, word?.StartTimeMs, word?.EndTimeMs, hasWord);
            SetMultiPlaceholder();
        }

        private string MultipleOr(EditLine? line, Func<EditLine, string> pick)
        {
            if (line is null) return "";
            if (_selection is null || _selection.SelectedLineIds.Count <= 1)
                return pick(line);
            var values = new HashSet<string>();
            if (_store is not null)
                foreach (var id in _selection.SelectedLineIds)
                {
                    var l = _store.Document.Lines.FirstOrDefault(x => x.Id == id);
                    if (l is not null) values.Add(pick(l));
                }
            return values.Count == 1 ? values.First() : "";
        }

        private void SetBox(TextBox box, string text)
        {
            if (_focusedBox == box) return; // never clobber typing
            if (box.Text != text) box.Text = text;
        }

        // Multi-select with differing values: blank + placeholder, like the
        // original's 多个值.... Single selection clears the placeholders.
        private void SetMultiPlaceholder()
        {
            const string multi = "Multiple values...";
            foreach (var box in new[] { LineStartBox, LineEndBox, TransBox, RomanBox,
                WordStartBox, WordEndBox, WordContentBox, WordRomanBox, EmptyBeatBox })
                box.PlaceholderText = "";
            if (_selection is null || _store is null) return;
            if (_selection.SelectedLineIds.Count > 1)
            {
                var lines = _selection.SelectedLineIds
                    .Select(id => _store.Document.Lines.FirstOrDefault(l => l.Id == id))
                    .Where(l => l is not null).ToList();
                if (lines.Select(l => l!.StartTimeMs).Distinct().Count() > 1)
                {
                    if (_focusedBox != LineStartBox) LineStartBox.Text = "";
                    LineStartBox.PlaceholderText = multi;
                }
                var endsDiffer = _durationMode
                    ? lines.Select(l => l!.EndTimeMs - l!.StartTimeMs).Distinct().Count() > 1
                    : lines.Select(l => l!.EndTimeMs).Distinct().Count() > 1;
                if (endsDiffer)
                {
                    if (_focusedBox != LineEndBox) LineEndBox.Text = "";
                    LineEndBox.PlaceholderText = multi;
                }
                if (lines.Select(l => l!.TranslatedLyric).Distinct().Count() > 1)
                {
                    if (_focusedBox != TransBox) TransBox.Text = "";
                    TransBox.PlaceholderText = multi;
                }
                if (lines.Select(l => l!.RomanLyric).Distinct().Count() > 1)
                {
                    if (_focusedBox != RomanBox) RomanBox.Text = "";
                    RomanBox.PlaceholderText = multi;
                }
            }
            if (_selection.SelectedWordIds.Count > 1)
            {
                var words = _selection.SelectedWordIds
                    .SelectMany(wid => _store.Document.Lines
                        .SelectMany(l => l.Words.Where(w => w.Id == wid)
                            .Select(w => (EditWord?)w)))
                    .Where(w => w is not null).ToList();
                void Multi(TextBox box, Func<EditWord, string> pick)
                {
                    if (words.Select(w => pick(w!)).Distinct().Count() > 1)
                    {
                        if (_focusedBox != box) box.Text = "";
                        box.PlaceholderText = multi;
                    }
                }
                Multi(WordStartBox, w => LyricTime.Format(w.StartTimeMs));
                Multi(WordEndBox, w => _durationMode
                    ? (w.EndTimeMs - w.StartTimeMs).ToString()
                    : LyricTime.Format(w.EndTimeMs));
                Multi(WordContentBox, w => w.Text);
                Multi(WordRomanBox, w => w.RomanWord);
                Multi(EmptyBeatBox, w => w.EmptyBeat.ToString());
            }
        }

        private string LineEndText(EditLine? line)
        {
            if (line is null) return "";
            if (_durationMode) return (line.EndTimeMs - line.StartTimeMs).ToString();
            return LyricTime.Format(line.EndTimeMs);
        }

        private string WordEndText(EditLine? line, EditWord? word)
        {
            if (word is null) return "";
            if (_durationMode) return (word.EndTimeMs - word.StartTimeMs).ToString();
            return LyricTime.Format(word.EndTimeMs);
        }

        private void MarkRange(TextBox startBox, TextBox endBox, int? start, int? end, bool enabled)
        {
            var error = enabled && start is int s && end is int e && s > e;
            if (error)
            {
                _errorBrush ??= (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                startBox.BorderBrush = _errorBrush;
                endBox.BorderBrush = _errorBrush;
            }
            else
            {
                if (_focusedBox != startBox) startBox.ClearValue(TextBox.BorderBrushProperty);
                if (_focusedBox != endBox) endBox.ClearValue(TextBox.BorderBrushProperty);
            }
        }

        private void OnDurationModeClick(object sender, RoutedEventArgs e)
        {
            _durationMode = !_durationMode;
            RefreshSelectionDisplay();
        }

        private void OnLineFlagClick(object sender, RoutedEventArgs e)
        {
            if (_store is null) return;
            var (line, _) = Targets();
            if (line is null) return;
            if (sender == BgToggle)
                _store.Execute(new SetLineFlagsCommand(line.Id, isBg: BgToggle.IsChecked == true));
            else if (sender == DuetToggle)
                _store.Execute(new SetLineFlagsCommand(line.Id, isDuet: DuetToggle.IsChecked == true));
            else if (sender == IgnoreSyncToggle)
                _store.Execute(new SetLineFlagsCommand(line.Id, ignoreSync: IgnoreSyncToggle.IsChecked == true));
        }

        private void OnObsceneClick(object sender, RoutedEventArgs e)
        {
            ObsceneToggle.Content = ObsceneToggle.IsChecked == true ? "Yes" : "No";
            if (_store is null) return;
            var (line, word) = Targets();
            if (line is null || word is null) return;
            _store.Execute(new SetWordFlagsCommand(line.Id, word.Id,
                obscene: ObsceneToggle.IsChecked == true));
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

        private void OnViewToggleClick(object sender, RoutedEventArgs e)
        {
            AppSettings.ShowOutline = OutlineToggle.IsChecked == true;
            AppSettings.ShowBpm = BpmToggle.IsChecked == true;
            AppSettings.ShowPreview = PreviewToggle.IsChecked == true;
            AppSettings.FollowPlayback = FollowToggle.IsChecked == true;
        }

        // --- test hooks (driven by ShellPage Test_ methods, UI thread) -----
        internal string RibbonText(string which) => which switch
        {
            "linestart" => LineStartBox.Text,
            "lineend" => LineEndBox.Text,
            "wordstart" => WordStartBox.Text,
            "wordend" => WordEndBox.Text,
            "content" => WordContentBox.Text,
            "romanword" => WordRomanBox.Text,
            "beats" => EmptyBeatBox.Text,
            "trans" => TransBox.Text,
            "lineroman" => RomanBox.Text,
            _ => throw new ArgumentException($"Unknown field: {which}"),
        };

        internal void SetRibbonText(string which, string text)
        {
            var box = which switch
            {
                "linestart" => LineStartBox,
                "lineend" => LineEndBox,
                "wordstart" => WordStartBox,
                "wordend" => WordEndBox,
                "content" => WordContentBox,
                "romanword" => WordRomanBox,
                "beats" => EmptyBeatBox,
                "trans" => TransBox,
                "lineroman" => RomanBox,
                _ => throw new ArgumentException($"Unknown field: {which}"),
            };
            box.Text = text;
        }

        internal void CommitRibbon(string which)
        {
            var box = which switch
            {
                "linestart" => LineStartBox,
                "lineend" => LineEndBox,
                "wordstart" => WordStartBox,
                "wordend" => WordEndBox,
                "content" => WordContentBox,
                "romanword" => WordRomanBox,
                "beats" => EmptyBeatBox,
                "trans" => TransBox,
                "lineroman" => RomanBox,
                _ => throw new ArgumentException($"Unknown field: {which}"),
            };
            CommitField(box);
        }

        internal bool RibbonEnabled(string which) => which switch
        {
            "linestart" => LineStartBox.IsEnabled,
            "lineend" => LineEndBox.IsEnabled,
            "wordstart" => WordStartBox.IsEnabled,
            "wordend" => WordEndBox.IsEnabled,
            "content" => WordContentBox.IsEnabled,
            "romanword" => WordRomanBox.IsEnabled,
            "beats" => EmptyBeatBox.IsEnabled,
            "obscene" => ObsceneToggle.IsEnabled,
            "trans" => TransBox.IsEnabled,
            "lineroman" => RomanBox.IsEnabled,
            "bg" => BgToggle.IsEnabled,
            "duet" => DuetToggle.IsEnabled,
            "ignoresync" => IgnoreSyncToggle.IsEnabled,
            _ => throw new ArgumentException($"Unknown field: {which}"),
        };

        internal void ClickRibbonToggle(string which)
        {
            switch (which)
            {
                case "bg": BgToggle.IsChecked = !(BgToggle.IsChecked == true); OnLineFlagClick(BgToggle, new RoutedEventArgs()); break;
                case "duet": DuetToggle.IsChecked = !(DuetToggle.IsChecked == true); OnLineFlagClick(DuetToggle, new RoutedEventArgs()); break;
                case "ignoresync": IgnoreSyncToggle.IsChecked = !(IgnoreSyncToggle.IsChecked == true); OnLineFlagClick(IgnoreSyncToggle, new RoutedEventArgs()); break;
                case "obscene": ObsceneToggle.IsChecked = !(ObsceneToggle.IsChecked == true); OnObsceneClick(ObsceneToggle, new RoutedEventArgs()); break;
                case "duration": OnDurationModeClick(LineEndModeButton, new RoutedEventArgs()); break;
                default: throw new ArgumentException($"Unknown toggle: {which}");
            }
        }

        internal bool DurationMode() => _durationMode;

        internal void SetComposing(string which, bool composing)
        {
            var box = which switch
            {
                "linestart" => LineStartBox,
                "lineend" => LineEndBox,
                "wordstart" => WordStartBox,
                "wordend" => WordEndBox,
                "content" => WordContentBox,
                "romanword" => WordRomanBox,
                "beats" => EmptyBeatBox,
                "trans" => TransBox,
                "lineroman" => RomanBox,
                _ => throw new ArgumentException($"Unknown field: {which}"),
            };
            if (composing) _composing.Add(box);
            else _composing.Remove(box);
        }
    }
}
