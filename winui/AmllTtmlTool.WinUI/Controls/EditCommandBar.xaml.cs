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
        private LyricViewStateService? _viewState;

        // Shared by both end fields (original showEndTimeAsDurationAtom).
        private bool _durationMode;
        private TextBox? _focusedBox;
        private readonly HashSet<TextBox> _composing = new();
        private readonly HashSet<TextBox> _placeholderBoxes = new();
        private Brush? _errorBrush;

        public void BindSelection(SelectionService selection, LyricStore store, LyricViewStateService viewState)
        {
            if (_selection is not null) _selection.Changed -= RefreshSelectionDisplay;
            if (_store is not null) _store.Changed -= RefreshSelectionDisplay;
            if (_viewState is not null) _viewState.Changed -= OnViewStateChanged;
            _selection = selection;
            _store = store;
            _viewState = viewState;
            _selection.Changed += RefreshSelectionDisplay;
            _store.Changed += RefreshSelectionDisplay;
            _viewState.Changed += OnViewStateChanged;
            WireField(LineStartBox);
            WireField(LineEndBox);
            WireField(WordStartBox);
            WireField(WordEndBox);
            WireField(WordContentBox);
            WireField(WordRomanBox);
            WireField(EmptyBeatBox);
            WireField(TransBox);
            WireField(RomanBox);
            ApplyWordRomanVisibility();
            RefreshSelectionDisplay();
        }

        private void WireField(TextBox box)
        {
            box.KeyDown -= OnFieldKeyDown;
            box.LostFocus -= OnFieldLostFocus;
            box.GotFocus -= OnFieldGotFocus;
            box.TextCompositionStarted -= OnFieldCompositionStarted;
            box.TextCompositionEnded -= OnFieldCompositionEnded;
            box.TextChanged -= OnFieldTextChanged;
            box.KeyDown += OnFieldKeyDown;
            box.LostFocus += OnFieldLostFocus;
            box.GotFocus += OnFieldGotFocus;
            box.TextCompositionStarted += OnFieldCompositionStarted;
            box.TextCompositionEnded += OnFieldCompositionEnded;
            box.TextChanged += OnFieldTextChanged;
        }

        private void OnFieldTextChanged(object sender, TextChangedEventArgs e)
        {
            // User typed: a showing multi-placeholder no longer describes it.
            if (sender is TextBox box) _placeholderBoxes.Remove(box);
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

        private void OnViewStateChanged()
        {
            if (DispatcherQueue.HasThreadAccess) { ApplyWordRomanVisibility(); RefreshSelectionDisplay(); }
            else DispatcherQueue.TryEnqueue(() => { ApplyWordRomanVisibility(); RefreshSelectionDisplay(); });
        }

        // The Roman field is Collapsed (no gap) whenever the per-word
        // romanization toggle is off; Beats stays. Beats shares Roman's
        // columns, so they keep Beats' width and nothing shifts.
        private void ApplyWordRomanVisibility()
        {
            var show = _viewState?.ShowWordRomanization ?? true;
            var visibility = show ? Visibility.Visible : Visibility.Collapsed;
            WordRomanLabel.Visibility = visibility;
            WordRomanBox.Visibility = visibility;
            SyncWordLabelColumns();
        }

        // Label columns (Start/Content/Beats over End/Obscene/Roman) share
        // one width: the longest visible label wins. WinUI Grid has no
        // SharedSizeGroup, so this runs after layout-affecting changes
        // (tier switch, roman toggle, group resize). Guarded: setting an
        // unchanged width never re-invalidates layout.
        private void SyncWordLabelColumns()
        {
            var labels = new[] { WordStartLabel, WordContentLabel, EmptyBeatLabel,
                WordEndModeLabel, ObsceneLabel, WordRomanLabel };
            var widest = 0.0;
            foreach (var label in labels)
            {
                if (label.Visibility != Visibility.Visible) continue;
                label.Measure(new Windows.Foundation.Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                widest = System.Math.Max(widest, label.DesiredSize.Width);
            }
            if (widest <= 0) return;
            foreach (var index in new[] { 0, 2, 4 })
            {
                var column = WordGroup.ColumnDefinitions[index];
                if (System.Math.Abs(column.Width.Value - widest) > 0.5)
                    column.Width = new GridLength(widest);
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
            // Stale-target commits (selection died mid-flight) are no-ops.
            try { CommitFieldCore(box); }
            catch (InvalidOperationException) { }
        }

        // Multi-selection targets in document order (the original writes
        // every selected line/word from one field edit, one history step).
        private List<EditLine> SelectedLines()
        {
            if (_store is null || _selection is null) return new List<EditLine>();
            return _store.Document.Lines
                .Where(l => _selection.SelectedLineIds.Contains(l.Id)).ToList();
        }

        private List<(EditLine Line, EditWord Word)> SelectedWords()
        {
            if (_store is null || _selection is null) return new List<(EditLine, EditWord)>();
            var wanted = _selection.SelectedWordIds;
            var found = new List<(EditLine, EditWord)>();
            foreach (var line in _store.Document.Lines)
                foreach (var word in line.Words)
                    if (wanted.Contains(word.Id)) found.Add((line, word));
            return found;
        }

        // One Execute for any batch (single direct command when exactly one
        // target differs, so single-selection history is byte-identical).
        private void ExecuteBatch(string description, List<ILyricCommand> cmds)
        {
            if (_store is null || cmds.Count == 0) return;
            _store.Execute(cmds.Count == 1 ? cmds[0]
                : new CompositeCommand(description, cmds));
        }

        private void CommitFieldCore(TextBox box)
        {
            var (line, word) = Targets();
            var raw = box.Text;
            if (box == LineStartBox && line is not null)
            {
                var lines = SelectedLines();
                if (lines.Count <= 1)
                {
                    CommitTime(raw, v => new SetLineTimesCommand(line.Id, v, line.EndTimeMs),
                        box, LyricTime.Format(line.StartTimeMs));
                    return;
                }
                int parsed;
                try { parsed = LyricTime.Parse(raw.Trim()); }
                catch (FormatException) { FlagInvalid(box, LyricTime.Format(line.StartTimeMs)); return; }
                ExecuteBatch("Set line start",
                    lines.Where(l => parsed != l.StartTimeMs)
                        .Select(l => (ILyricCommand)new SetLineTimesCommand(l.Id, parsed, l.EndTimeMs)).ToList());
            }
            else if (box == LineEndBox && line is not null)
                CommitEnd(line: true, raw, line, word: null, box);
            else if (box == WordStartBox && line is not null && word is not null)
            {
                var words = SelectedWords();
                if (words.Count <= 1)
                {
                    CommitTime(raw, v => new SetWordTimesCommand(line.Id, word.Id, v, word.EndTimeMs),
                        box, LyricTime.Format(word.StartTimeMs));
                    return;
                }
                int parsed;
                try { parsed = LyricTime.Parse(raw.Trim()); }
                catch (FormatException) { FlagInvalid(box, LyricTime.Format(word.StartTimeMs)); return; }
                ExecuteBatch("Set word start",
                    words.Where(t => parsed != t.Word.StartTimeMs)
                        .Select(t => (ILyricCommand)new SetWordTimesCommand(
                            t.Line.Id, t.Word.Id, parsed, t.Word.EndTimeMs)).ToList());
            }
            else if (box == WordEndBox && line is not null && word is not null)
                CommitEnd(line: false, raw, line, word, box);
            else if (box == WordContentBox && line is not null && word is not null)
            {
                var words = SelectedWords();
                if (words.Count <= 1)
                {
                    if (raw != word.Text)
                        _store.Execute(new SetWordTextCommand(line.Id, word.Id, raw));
                    return;
                }
                ExecuteBatch("Set word text",
                    words.Where(t => raw != t.Word.Text)
                        .Select(t => (ILyricCommand)new SetWordTextCommand(t.Line.Id, t.Word.Id, raw)).ToList());
            }
            else if (box == WordRomanBox && line is not null && word is not null)
            {
                var words = SelectedWords();
                if (words.Count <= 1)
                {
                    if (raw != word.RomanWord)
                        _store.Execute(new SetWordRomanCommand(line.Id, word.Id, raw));
                    return;
                }
                ExecuteBatch("Set word romanization",
                    words.Where(t => raw != t.Word.RomanWord)
                        .Select(t => (ILyricCommand)new SetWordRomanCommand(t.Line.Id, t.Word.Id, raw)).ToList());
            }
            else if (box == EmptyBeatBox && line is not null && word is not null)
            {
                var parsed = int.TryParse(raw.Trim(), out var n) ? n : 0;
                var words = SelectedWords();
                if (words.Count <= 1)
                {
                    if (parsed != word.EmptyBeat)
                        _store.Execute(new SetWordFlagsCommand(line.Id, word.Id, emptyBeat: parsed));
                    return;
                }
                ExecuteBatch("Set empty beat",
                    words.Where(t => parsed != t.Word.EmptyBeat)
                        .Select(t => (ILyricCommand)new SetWordFlagsCommand(
                            t.Line.Id, t.Word.Id, emptyBeat: parsed)).ToList());
            }
            else if (box == TransBox && line is not null)
            {
                var lines = SelectedLines();
                if (lines.Count <= 1)
                {
                    if (raw != line.TranslatedLyric)
                        _store.Execute(new SetTranslationCommand(line.Id, raw));
                    return;
                }
                ExecuteBatch("Set translation",
                    lines.Where(l => raw != l.TranslatedLyric)
                        .Select(l => (ILyricCommand)new SetTranslationCommand(l.Id, raw)).ToList());
            }
            else if (box == RomanBox && line is not null)
            {
                var lines = SelectedLines();
                if (lines.Count <= 1)
                {
                    if (raw != line.RomanLyric)
                        _store.Execute(new SetRomanizationCommand(line.Id, raw));
                    return;
                }
                ExecuteBatch("Set romanization",
                    lines.Where(l => raw != l.RomanLyric)
                        .Select(l => (ILyricCommand)new SetRomanizationCommand(l.Id, raw)).ToList());
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
                if (line)
                {
                    var lines = SelectedLines();
                    if (lines.Count <= 1)
                    {
                        CommitTime(raw, v => new SetLineTimesCommand(editLine.Id, editLine.StartTimeMs, v),
                            box, LyricTime.Format(editLine.EndTimeMs));
                        return;
                    }
                    int parsedLine;
                    try { parsedLine = LyricTime.Parse(raw.Trim()); }
                    catch (FormatException) { FlagInvalid(box, LyricTime.Format(editLine.EndTimeMs)); return; }
                    ExecuteBatch("Set line end",
                        lines.Where(l => parsedLine != l.EndTimeMs)
                            .Select(l => (ILyricCommand)new SetLineTimesCommand(
                                l.Id, l.StartTimeMs, parsedLine)).ToList());
                    return;
                }
                if (word is not null)
                {
                    var words = SelectedWords();
                    if (words.Count <= 1)
                    {
                        CommitTime(raw, v => new SetWordTimesCommand(editLine.Id, word.Id, word.StartTimeMs, v),
                            box, LyricTime.Format(word.EndTimeMs));
                        return;
                    }
                    int parsedAbs;
                    try { parsedAbs = LyricTime.Parse(raw.Trim()); }
                    catch (FormatException) { FlagInvalid(box, LyricTime.Format(word.EndTimeMs)); return; }
                    ExecuteBatch("Set word end",
                        words.Where(t => parsedAbs != t.Word.EndTimeMs)
                            .Select(t => (ILyricCommand)new SetWordTimesCommand(
                                t.Line.Id, t.Word.Id, t.Word.StartTimeMs, parsedAbs)).ToList());
                    return;
                }
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
                var lines = SelectedLines();
                if (lines.Count <= 1)
                {
                    var newEndRaw = isDelta ? editLine.EndTimeMs + delta : editLine.StartTimeMs + delta;
                    _store.Execute(new SetLineTimesCommand(editLine.Id, editLine.StartTimeMs,
                        Math.Max(editLine.StartTimeMs, newEndRaw)));
                    return;
                }
                ExecuteBatch("Set line end", lines.Select(l =>
                {
                    var rawEnd = isDelta ? l.EndTimeMs + delta : l.StartTimeMs + delta;
                    return (ILyricCommand)new SetLineTimesCommand(l.Id, l.StartTimeMs,
                        Math.Max(l.StartTimeMs, rawEnd));
                }).ToList());
            }
            else if (word is not null)
            {
                var words = SelectedWords();
                if (words.Count <= 1)
                {
                    CommitWordEndDuration(editLine, word, delta, isDelta);
                    return;
                }
                var cmds = new List<ILyricCommand>();
                foreach (var (ln, w) in words)
                    CollectWordEndDuration(cmds, ln, w, delta, isDelta);
                ExecuteBatch("Set word end", cmds);
            }
        }

        // Duration-mode end edit for one word (+ optional abutting-next push,
        // original): factored so multi-selection reuses it per word.
        private void CommitWordEndDuration(EditLine editLine, EditWord word, int delta, bool isDelta)
        {
            if (_store is null) return;
            var cmds = new List<ILyricCommand>();
            CollectWordEndDuration(cmds, editLine, word, delta, isDelta);
            ExecuteBatch("Set word end", cmds);
        }

        private void CollectWordEndDuration(
            List<ILyricCommand> cmds, EditLine editLine, EditWord word, int delta, bool isDelta)
        {
            var newEndRaw = isDelta ? word.EndTimeMs + delta : word.StartTimeMs + delta;
            var newEnd = Math.Max(word.StartTimeMs, newEndRaw);
            cmds.Add(new SetWordTimesCommand(editLine.Id, word.Id, word.StartTimeMs, newEnd));
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
            DuetToggle.IsChecked = line is not null && _store is not null
                && DuetDisplay.EffectiveDuet(line,
                    DuetDisplay.PrimaryAgent(_store.Document.Lines));
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
            // Group caption: selection count while several words share the
            // section ("3 selected"); hidden otherwise (column zeroed so no
            // leftover space — the Tight tier additionally forces it off).
            var wordCount = _selection?.SelectedWordIds.Count ?? 0;
            var showCaption = wordCount > 1;
            WordCountCaption.Text = $"{wordCount} selected";
            WordCountCaption.Visibility = showCaption ? Visibility.Visible : Visibility.Collapsed;
            if (WordGroup.ColumnDefinitions.Count > 6)
                WordGroup.ColumnDefinitions[6].Width = showCaption
                    ? new GridLength(1, GridUnitType.Auto)
                    : new GridLength(0);
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

        // Multi-select with differing values: blank + short placeholder (the
        // full "Multiple values..." clipped in 80-100px boxes), with the full
        // wording as tooltip, like the original's 多个值.... Single selection
        // clears placeholders and tooltips.
        private void SetMultiPlaceholder()
        {
            // \u2026 escape: this file has no BOM and the compiler decodes
            // it in the system codepage, which would mangle a literal ….
            const string multiShort = "Multiple\u2026";
            const string multiFull = "Multiple values";
            var boxes = new[] { LineStartBox, LineEndBox, TransBox, RomanBox,
                WordStartBox, WordEndBox, WordContentBox, WordRomanBox, EmptyBeatBox };
            foreach (var box in boxes)
            {
                box.PlaceholderText = "";
                ToolTipService.SetToolTip(box, null);
            }

            void ShowMulti(TextBox box)
            {
                if (_focusedBox != box) box.Text = "";
                box.PlaceholderText = multiShort;
                ToolTipService.SetToolTip(box, multiFull);
                _placeholderBoxes.Add(box);
            }
            if (_selection is null || _store is null) return;
            if (_selection.SelectedLineIds.Count > 1)
            {
                var lines = _selection.SelectedLineIds
                    .Select(id => _store.Document.Lines.FirstOrDefault(l => l.Id == id))
                    .Where(l => l is not null).ToList();
                if (lines.Select(l => l!.StartTimeMs).Distinct().Count() > 1)
                    ShowMulti(LineStartBox);
                var endsDiffer = _durationMode
                    ? lines.Select(l => l!.EndTimeMs - l!.StartTimeMs).Distinct().Count() > 1
                    : lines.Select(l => l!.EndTimeMs).Distinct().Count() > 1;
                if (endsDiffer)
                    ShowMulti(LineEndBox);
                if (lines.Select(l => l!.TranslatedLyric).Distinct().Count() > 1)
                    ShowMulti(TransBox);
                if (lines.Select(l => l!.RomanLyric).Distinct().Count() > 1)
                    ShowMulti(RomanBox);
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
                        ShowMulti(box);
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
            var lines = SelectedLines();
            bool? isBg = null;
            bool? isDuet = null;
            bool? ignoreSync = null;
            if (sender == BgToggle) isBg = BgToggle.IsChecked == true;
            else if (sender == DuetToggle) isDuet = DuetToggle.IsChecked == true;
            else if (sender == IgnoreSyncToggle) ignoreSync = IgnoreSyncToggle.IsChecked == true;
            else return;
            if (lines.Count <= 1)
            {
                _store.Execute(new SetLineFlagsCommand(line.Id,
                    isBg: sender == BgToggle ? isBg : null,
                    isDuet: sender == DuetToggle ? isDuet : null,
                    ignoreSync: sender == IgnoreSyncToggle ? ignoreSync : null));
                return;
            }
            ExecuteBatch("Set line flags", lines
                .Select(l => (ILyricCommand)new SetLineFlagsCommand(l.Id,
                    isBg: sender == BgToggle ? isBg : null,
                    isDuet: sender == DuetToggle ? isDuet : null,
                    ignoreSync: sender == IgnoreSyncToggle ? ignoreSync : null)).ToList());
        }

        private void OnObsceneClick(object sender, RoutedEventArgs e)
        {
            ObsceneToggle.Content = ObsceneToggle.IsChecked == true ? "Yes" : "No";
            if (_store is null) return;
            var (line, word) = Targets();
            if (line is null || word is null) return;
            var words = SelectedWords();
            if (words.Count <= 1)
            {
                _store.Execute(new SetWordFlagsCommand(line.Id, word.Id,
                    obscene: ObsceneToggle.IsChecked == true));
                return;
            }
            var value = ObsceneToggle.IsChecked == true;
            ExecuteBatch("Set obscene", words
                .Select(t => (ILyricCommand)new SetWordFlagsCommand(t.Line.Id, t.Word.Id,
                    obscene: value)).ToList());
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
                WordGroup.SizeChanged += OnWordGroupSizeChanged;
                SyncWordLabelColumns();
            };
            this.Unloaded += (_, _) =>
            {
                AppSettings.DisplayChanged -= ApplyMasterToggles;
                WordGroup.SizeChanged -= OnWordGroupSizeChanged;
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

        private void OnWidthTierChanged(object sender, VisualStateChangedEventArgs e)
        {
            ApplyMasterToggles();
            SyncWordLabelColumns();
        }

        private void OnWordGroupSizeChanged(object sender, SizeChangedEventArgs e) =>
            SyncWordLabelColumns();

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

        // STEP3 verification hooks (headless, no window resize needed
        // except BarReport which reads live layout after Test_SetWindowWidth).
        internal int WordGroupRows() => WordGroup.RowDefinitions.Count;

        internal string Test_TierName() => WidthTiers?.CurrentState?.Name ?? "none";

        internal bool WordRomanVisible() => WordRomanBox.Visibility == Visibility.Visible;

        internal string WordCaption() =>
            $"{WordCountCaption.Text}|{WordCountCaption.Visibility}";

        internal double WordGroupWidth()
        {
            WordGroup.UpdateLayout();
            return WordGroup.ActualWidth;
        }

        // "placeholder|tooltip|textWidth|boxWidth" for a word field: the
        // short multi text must fit its box (text measured with the same
        // font size as the ribbon boxes).
        internal string MultiState(string which)
        {
            var box = which switch
            {
                "wordstart" => WordStartBox,
                "wordend" => WordEndBox,
                "content" => WordContentBox,
                "romanword" => WordRomanBox,
                "beats" => EmptyBeatBox,
                _ => throw new ArgumentException($"Unknown field: {which}"),
            };
            box.UpdateLayout();
            var probe = new TextBlock
            {
                Text = box.PlaceholderText,
                FontSize = box.FontSize,
                FontFamily = box.FontFamily,
            };
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var tip = Microsoft.UI.Xaml.Controls.ToolTipService.GetToolTip(box);
            static string Num(double v) =>
                v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            return $"{box.PlaceholderText}|{tip}|{Num(probe.DesiredSize.Width)}|{Num(box.ActualWidth)}";
        }

        // Shared-column geometry: per input "left,width,top,height" for the
        // six word inputs + label column widths + row heights, in one call.
        // "S:l,w;C:l,w;B:l,w;E:l,w;O:l,w;R:l,w;labs:w,w,w;rows:h,h;vis:1/0"
        // (R omitted while Roman is collapsed). Same-column inputs must share
        // left edge and width within 1px; label columns one width; rows equal.
        internal string Test_WordGridGeometry()
        {
            static string Num(double v) =>
                v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            WordGroup.UpdateLayout();
            string Geo(FrameworkElement el)
            {
                var p = el.TransformToVisual(WordGroup)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                return $"{Num(p.X)},{Num(el.ActualWidth)},{Num(p.Y)},{Num(el.ActualHeight)}";
            }
            // Label COLUMN widths (not label text widths: labels left-align
            // inside their shared column).
            var labs = string.Join(",", new[] { 0, 2, 4 }
                .Select(i => Num(WordGroup.ColumnDefinitions[i].ActualWidth)));
            var r0h = Num(System.Math.Max(WordStartBox.ActualHeight, WordContentBox.ActualHeight));
            var r1els = new List<FrameworkElement> { WordEndBox, ObsceneToggle };
            if (WordRomanBox.Visibility == Visibility.Visible) r1els.Add(WordRomanBox);
            var r1h = Num(r1els.Max(e => e.ActualHeight));
            var roman = WordRomanBox.Visibility == Visibility.Visible
                ? $";R:{Geo(WordRomanBox)}" : "";
            return $"S:{Geo(WordStartBox)};C:{Geo(WordContentBox)};B:{Geo(EmptyBeatBox)};" +
                $"E:{Geo(WordEndBox)};O:{Geo(ObsceneToggle)}{roman};" +
                $"labs:{labs};rows:{r0h},{r1h};vis:{(WordGroup.Visibility == Visibility.Visible ? 1 : 0)}";
        }

        // Same-row baseline check: max vertical-center deviation within row
        // 0 fields and within row 1 fields (labels are Center-aligned, boxes
        // share MinHeight 28). Collapsed Roman is skipped. "r0dev,r1dev".
        internal string BarBaselines()
        {
            static double CenterY(FrameworkElement el, FrameworkElement rel)
            {
                el.UpdateLayout();
                return el.TransformToVisual(rel)
                    .TransformPoint(new Windows.Foundation.Point(0, el.ActualHeight / 2)).Y;
            }
            var r0 = new FrameworkElement[] { WordStartLabel, WordStartBox, WordContentLabel,
                WordContentBox, EmptyBeatLabel, EmptyBeatBox };
            var r1 = new FrameworkElement[] { WordEndModeButton, WordEndBox,
                ObsceneLabel, ObsceneToggle, WordRomanLabel, WordRomanBox };
            var c0 = r0.Select(e => CenterY(e, WordGroup)).ToList();
            var c1 = r1.Where(e => e.Visibility == Visibility.Visible)
                .Select(e => CenterY(e, WordGroup)).ToList();
            static string Dev(List<double> cs) =>
                (cs.Max() - cs.Min()).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            return $"{Dev(c0)},{Dev(c1)}";
        }

        // Clipping sweep support: "ok:barW:winW" or "CLIP:..." / window-level
        // "WINCLIP:barW>winW". Child-level fit is not enough: an
        // over-wide RootGrid itself can pass the window edge (the ribbon
        // card gives it no scroll fallback by design).
        internal string BarReport()
        {
            RootGrid.UpdateLayout();
            var barW = RootGrid.ActualWidth;
            double winW = double.NaN;
            try
            {
                var window = ((App)Application.Current).MainWindow;
                if (window is not null)
                    winW = window.AppWindow.Size.Width / XamlRoot.RasterizationScale;
            }
            catch { }
            var bad = new List<string>();
            foreach (var child in RootGrid.Children)
            {
                if (child is not FrameworkElement el) continue;
                if (el.Visibility != Visibility.Visible) continue;
                var right = el.TransformToVisual(RootGrid)
                    .TransformPoint(new Windows.Foundation.Point(el.ActualWidth, 0)).X;
                if (right > barW + 1)
                    bad.Add((el as Control)?.Name ?? el.GetType().Name);
            }
            // Word-group internals (caption!) must also fit the group.
            WordGroup.UpdateLayout();
            foreach (var child in WordGroup.Children)
            {
                if (child is not FrameworkElement el) continue;
                if (el.Visibility != Visibility.Visible) continue;
                var right = el.TransformToVisual(WordGroup)
                    .TransformPoint(new Windows.Foundation.Point(el.ActualWidth, 0)).X;
                if (right > WordGroup.ActualWidth + 1)
                    bad.Add("WordGroup:" + (el as Control)?.Name);
            }
            static string Num(double v) =>
                v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            if (bad.Count > 0) return $"CLIP:{string.Join(",", bad)}:{Num(barW)}";
            // Per-group widths for tier budgeting ("name:vis:width;...").
            var groups = new[] { "ManageGroup", "LineGroup", "WordGroup",
                "SecondaryGroup", "ViewGroup" };
            var parts = new List<string>();
            foreach (var name in groups)
            {
                if (FindName(name) is FrameworkElement g)
                    parts.Add($"{name}:{g.Visibility}:{Num(g.ActualWidth)}");
            }
            var detail = string.Join(";", parts);
            if (!double.IsNaN(winW) && barW > winW - 26 + 1)
                return $"WINCLIP:{Num(barW)}>{Num(winW)}:{detail}";
            return $"ok:{Num(barW)}:{Num(winW)}:{detail}";
        }

        // Step-4 routing: the focused ribbon box owns Ctrl+Z/Y while its
        // text is uncommitted (differs from the model and is not a freshly
        // shown multi-placeholder); IME composing counts as uncommitted.
        internal bool IsFocusedDirty()
        {
            if (_focusedBox is null) return false;
            if (_composing.Contains(_focusedBox)) return true;
            if (_placeholderBoxes.Contains(_focusedBox)
                && _focusedBox.Text.Length == 0) return false;
            return _focusedBox.Text != ExpectedText(_focusedBox);
        }

        private string ExpectedText(TextBox box)
        {
            var (line, word) = Targets();
            if (box == LineStartBox) return line is null ? "" : LyricTime.Format(line.StartTimeMs);
            if (box == LineEndBox) return LineEndText(line);
            if (box == WordStartBox) return word is null ? "" : LyricTime.Format(word.StartTimeMs);
            if (box == WordEndBox) return WordEndText(line, word);
            if (box == WordContentBox) return word?.Text ?? "";
            if (box == WordRomanBox) return word?.RomanWord ?? "";
            if (box == EmptyBeatBox) return word is null ? "" : word.EmptyBeat.ToString();
            if (box == TransBox) return MultipleOr(line, l => l.TranslatedLyric);
            if (box == RomanBox) return MultipleOr(line, l => l.RomanLyric);
            return box.Text;
        }

        internal bool FocusRibbon(string which)
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
            return box.Focus(FocusState.Programmatic);
        }

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
