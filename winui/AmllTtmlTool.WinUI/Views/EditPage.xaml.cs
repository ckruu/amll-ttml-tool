using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Lyrics.Commands;
using AmllTtmlTool.WinUI.Controls;
using AmllTtmlTool.WinUI.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;

namespace AmllTtmlTool.WinUI.Views
{
    public sealed partial class EditPage : Page
    {
        public event Action? ImportRequested;

        private LyricStore? _store;
        private SelectionService? _selection;
        private LyricViewStateService? _viewState;
        private Compositor? _compositor;
        private CubicBezierEasingFunction? _decelerate;
        private int _toggleVersion;

        public EditPage()
        {
            this.InitializeComponent();
            this.Unloaded += (_, _) => Detach();
        }

        // Called by ShellPage on navigation (fresh instance each time).
        public void Attach(LyricStore store, SelectionService selection, LyricViewStateService viewState)
        {
            Detach();
            _store = store;
            _selection = selection;
            _viewState = viewState;
            _store.Changed += OnStoreChanged;
            _viewState.Changed += OnViewStateChanged;
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _decelerate = _compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));
            // Glide for cards below a resized sibling. Set in code (not XAML)
            // so the animations-off setting snaps instead.
            LineList.ItemContainerTransitions =
                AnimationsOn()
                    ? new TransitionCollection { new RepositionThemeTransition() }
                    : new TransitionCollection();
            LineList.ItemsSource = _store.Document.Lines;
            RefreshEmptyState();
        }

        public void Detach()
        {
            if (_store is not null)
            {
                _store.Changed -= OnStoreChanged;
                _store = null;
            }
            if (_viewState is not null)
            {
                _viewState.Changed -= OnViewStateChanged;
                _viewState = null;
            }
            _selection = null;
        }

        private static bool AnimationsOn()
        {
            try
            {
                return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            }
            catch
            {
                return true;
            }
        }

        public bool IsEmptyStateVisible => EmptyStatePanel.Visibility == Visibility.Visible;

        internal void SelectLineAt(int index)
        {
            if (_store is null) return;
            if (index < 0 || index >= _store.Document.Lines.Count) return;
            LineList.SelectedItem = _store.Document.Lines[index];
        }

        internal int SelectedLineIndex()
        {
            if (LineList.SelectedItem is null) return -1;
            var container = LineList.ContainerFromItem(LineList.SelectedItem);
            return container is null ? -1 : LineList.IndexFromContainer(container);
        }

        // --- Step-5 test helpers (driven by Test_ hooks, UI thread) --------

        internal int RealizedLineCount()
        {
            var n = 0;
            for (var i = 0; i < LineList.Items.Count; i++)
                if (LineList.ContainerFromIndex(i) is not null) n++;
            return n;
        }

        internal int RealizedTextBoxCount()
        {
            var n = 0;
            for (var i = 0; i < LineList.Items.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not DependencyObject root) continue;
                n += CountTextBoxes(root);
            }
            return n;
        }

        private static int CountTextBoxes(DependencyObject root)
        {
            var n = root is TextBox ? 1 : 0;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                n += CountTextBoxes(VisualTreeHelper.GetChild(root, i));
            return n;
        }

        internal void ScrollLineIntoView(int index)
        {
            if (index < 0 || index >= LineList.Items.Count) return;
            CommitActiveBox();
            LineList.ScrollIntoView(LineList.Items[index]);
            LineList.UpdateLayout();
        }

        // which: 0 = translation row, 1 = romanization row, 2 = first chip's
        // romaji box. Returns (int)Visibility or -1 when not realized.
        internal int RowVisibility(int line, int which)
        {
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return -1;
            if (container.ContentTemplateRoot is not Grid card) return -1;
            FrameworkElement? el = which switch
            {
                0 => (card.FindName("TransLabel") as FrameworkElement) ?? card.FindName("TransBox") as FrameworkElement,
                1 => (card.FindName("RomanLabel") as FrameworkElement) ?? card.FindName("RomanBox") as FrameworkElement,
                _ => FirstChipRoman(card),
            };
            return el is null ? -1 : (int)el.Visibility;
        }

        private static FrameworkElement? FirstChipRoman(Grid card)
        {
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return null;
            foreach (var child in panel.Children)
            {
                if (child is Grid chip && chip.Children.Count >= 2
                    && chip.Children[1] is FrameworkElement roman)
                    return roman;
            }
            return null;
        }

        // --- Step-1 edit hooks (UI thread) ----------------------------------
        internal TextBox? FindChipBox(int line, int word, bool roman)
        {
            if (_store is null || line < 0 || line >= _store.Document.Lines.Count) return null;
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return null;
            if (container.ContentTemplateRoot is not Grid card) return null;
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return null;
            var docLine = _store.Document.Lines[line];
            if (word < 0 || word >= docLine.Words.Count) return null;
            var wordId = docLine.Words[word].Id;
            foreach (var child in panel.Children)
            {
                if (child is Grid chip && chip.Tag is string tag && tag == wordId
                    && chip.Children.Count >= 2)
                    return chip.Children[roman ? 1 : 0] as TextBox;
            }
            return null;
        }

        internal TextBox? FindLineFieldBox(int line, bool translation)
        {
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return null;
            if (container.ContentTemplateRoot is not Grid card) return null;
            return card.FindName(translation ? "TransBox" : "RomanBox") as TextBox;
        }

        internal bool FocusChip(int line, int word, bool roman)
        {
            var box = FindChipBox(line, word, roman);
            if (box is null) return false;
            return box.Focus(FocusState.Programmatic);
        }

        internal string ChipBoxText(int line, int word, bool roman) =>
            FindChipBox(line, word, roman)?.Text ?? "nobox";

        internal void SetChipBoxText(int line, int word, bool roman, string text)
        {
            if (FindChipBox(line, word, roman) is TextBox box) box.Text = text;
        }

        // Appends one char on the UI thread and returns the cost in ms
        // (typing-latency probe: no model touch, layout only).
        internal double TypeChipChar(int line, int word, bool roman, char ch)
        {
            var box = FindChipBox(line, word, roman);
            if (box is null) return -1;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            box.Text += ch;
            box.SelectionStart = box.Text.Length;
            LineList.UpdateLayout();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        internal void CommitChip(int line, int word, bool roman)
        {
            if (FindChipBox(line, word, roman) is TextBox box) CommitBoxIfDirty(box);
        }

        internal void EscapeChip(int line, int word, bool roman)
        {
            var box = FindChipBox(line, word, roman);
            if (box is null || !_sessions.TryGetValue(box, out var session)) return;
            try { SetTextPreservingCaret(box, session.GetModel()); }
            catch { }
        }

        internal int ChipCaret(int line, int word, bool roman) =>
            FindChipBox(line, word, roman)?.SelectionStart ?? -1;

        internal bool ChipIsFocused(int line, int word, bool roman) =>
            FindChipBox(line, word, roman) is TextBox box
            && ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), box);

        internal void SetChipComposing(int line, int word, bool roman, bool composing)
        {
            if (FindChipBox(line, word, roman) is TextBox box) Test_SetComposing(box, composing);
        }

        internal bool HasUncommittedEdit()
        {
            foreach (var (box, session) in _sessions)
            {
                string model;
                try { model = session.GetModel(); }
                catch { continue; }
                if (!session.Composing && box.Text != model) return true;
            }
            return false;
        }

        internal int UndoDepth() => _store?.Document.UndoDepth ?? -1;

        internal string LineFieldText(int line, bool translation) =>
            FindLineFieldBox(line, translation)?.Text ?? "nobox";

        internal void SetLineFieldText(int line, bool translation, string text)
        {
            if (FindLineFieldBox(line, translation) is TextBox box) box.Text = text;
        }

        internal void CommitLineField(int line, bool translation)
        {
            if (FindLineFieldBox(line, translation) is TextBox box) CommitBoxIfDirty(box);
        }

        internal void EscapeLineField(int line, bool translation)
        {
            var box = FindLineFieldBox(line, translation);
            if (box is null || !_sessions.TryGetValue(box, out var session)) return;
            try { SetTextPreservingCaret(box, session.GetModel()); }
            catch { }
        }

        internal string ChipWidths(int line, int word)
        {
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return "nopanel";
            if (word < 0 || word >= panel.Children.Count) return "norow";
            if (panel.Children[word] is not Grid chip || chip.Children.Count < 2) return "nochip";
            var tw = (chip.Children[0] as FrameworkElement)?.ActualWidth ?? double.NaN;
            var rw = (chip.Children[1] as FrameworkElement)?.ActualWidth ?? double.NaN;
            return $"{tw:F2},{rw:F2}";
        }

        internal bool ToggleState(string which) => _viewState is not null && which switch
        {
            "trans" => _viewState.ShowTranslation,
            "roman" => _viewState.ShowRomanization,
            "wordroman" => _viewState.ShowWordRomanization,
            _ => throw new ArgumentException($"Unknown toggle: {which}"),
        };

        // Flips a toggle through the service (same path as the pills) and
        // returns the synchronous UI-thread cost in ms.
        internal long FlipToggle(string which)
        {
            if (_viewState is null) throw new InvalidOperationException("No view state.");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            switch (which)
            {
                case "trans": _viewState.ShowTranslation = !_viewState.ShowTranslation; break;
                case "roman": _viewState.ShowRomanization = !_viewState.ShowRomanization; break;
                case "wordroman": _viewState.ShowWordRomanization = !_viewState.ShowWordRomanization; break;
                default: throw new ArgumentException($"Unknown toggle: {which}");
            }
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }

        // Samples CompositionTarget.Rendering around a service flip.
        // Returns "frames=N median=X p95=Y max=Z over13_9=A over2xmedian=B".
        internal async Task<string> SampleToggleFramesAsync(string which, int durationMs)
        {
            var stamps = new List<TimeSpan>();
            void Handler(object? sender, object args)
            {
                if (args is Microsoft.UI.Xaml.Media.RenderingEventArgs re)
                    stamps.Add(re.RenderingTime);
            }
            CompositionTarget.Rendering += Handler;
            try
            {
                FlipToggle(which);
                await Task.Delay(durationMs);
            }
            finally
            {
                CompositionTarget.Rendering -= Handler;
            }
            if (stamps.Count < 2) return $"frames={stamps.Count} median=0 p95=0 max=0 over13_9=0 over2xmedian=0";
            var deltas = new List<double>();
            for (var i = 1; i < stamps.Count; i++)
                deltas.Add((stamps[i] - stamps[i - 1]).TotalMilliseconds);
            deltas.Sort();
            var median = deltas[deltas.Count / 2];
            var p95 = deltas[(int)(deltas.Count * 0.95)];
            var max = deltas[^1];
            var over13_9 = deltas.Count(d => d > 13.9);
            var over2xmedian = deltas.Count(d => d > 2 * median);
            return $"frames={stamps.Count} median={median:F2} p95={p95:F2} max={max:F2} over13_9={over13_9} over2xmedian={over2xmedian}";
        }

        private void OnStoreChanged()
        {
            if (DispatcherQueue.HasThreadAccess) { RefreshEmptyState(); RefreshBoxTexts(); }
            else DispatcherQueue.TryEnqueue(() => { RefreshEmptyState(); RefreshBoxTexts(); });
        }

        // Pushes model text into every realized box WITHOUT stealing focus:
        // text is only assigned when it differs, and the caret is restored
        // clamped, so typing never loses caret/focus on model notify.
        private void RefreshBoxTexts()
        {
            if (_store is null) return;
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid card) continue;
                var line = _store.Document.Lines[i];
                if (card.FindName("TransBox") is TextBox trans)
                    SetTextPreservingCaret(trans, line.TranslatedLyric);
                if (card.FindName("RomanBox") is TextBox roman)
                    SetTextPreservingCaret(roman, line.RomanLyric);
                if (card.FindName("ChipPanel") is not LyricWrapPanel panel) continue;
                foreach (var child in panel.Children)
                {
                    if (child is not Grid chip || chip.Tag is not string wordId) continue;
                    if (chip.Children.Count < 2) continue;
                    var word = line.Words.FirstOrDefault(w => w.Id == wordId);
                    if (word is null) continue;
                    if (chip.Children[0] is TextBox text)
                        SetTextPreservingCaret(text, word.Text);
                    if (chip.Children[1] is TextBox rom)
                        SetTextPreservingCaret(rom, word.RomanWord);
                    var blank = word.EmptyBeat > 0 && string.IsNullOrWhiteSpace(word.Text);
                    chip.Opacity = blank ? 0.55 : 1;
                }
            }
        }

        private static void SetTextPreservingCaret(TextBox box, string text)
        {
            if (box.Text == text) return;
            var start = box.SelectionStart;
            var len = box.SelectionLength;
            box.Text = text;
            box.SelectionStart = Math.Min(start, text.Length);
            box.SelectionLength = Math.Min(len, Math.Max(0, text.Length - box.SelectionStart));
        }

        private void RefreshEmptyState()
        {
            var empty = _store is null || _store.Document.Lines.Count == 0;
            EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            LineList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnImportClick(object sender, RoutedEventArgs e) =>
            ImportRequested?.Invoke();

        // Commit sessions for uncommitted edits. Every mutation goes through
        // EditDocument commands: one committed edit = one history step, and
        // unchanged text commits nothing.
        private sealed class BoxSession
        {
            public Func<string> GetModel = () => "";
            public Action<string> Commit = _ => { };
            public bool Composing;
        }

        private readonly Dictionary<TextBox, BoxSession> _sessions = new();

        private void WireBox(TextBox box, Func<string> getModel, Action<string> commit)
        {
            if (_sessions.TryGetValue(box, out var existing))
            {
                existing.GetModel = getModel;
                existing.Commit = commit;
                SetTextPreservingCaret(box, getModel());
                return;
            }
            var session = new BoxSession { GetModel = getModel, Commit = commit };
            _sessions[box] = session;
            try { SetTextPreservingCaret(box, getModel()); }
            catch { }
            box.KeyDown += OnEditKeyDown;
            box.LostFocus += OnEditLostFocus;
            box.GotFocus += OnEditGotFocus;
            box.TextCompositionStarted += OnCompositionStarted;
            box.TextCompositionEnded += OnCompositionEnded;
            box.Unloaded += OnBoxUnloaded;
        }

        private void DropSessions(DependencyObject root)
        {
            if (root is TextBox box)
            {
                CommitBoxIfDirty(box);
                if (_sessions.TryGetValue(box, out _))
                {
                    box.KeyDown -= OnEditKeyDown;
                    box.LostFocus -= OnEditLostFocus;
                    box.GotFocus -= OnEditGotFocus;
                    box.TextCompositionStarted -= OnCompositionStarted;
                    box.TextCompositionEnded -= OnCompositionEnded;
                    box.Unloaded -= OnBoxUnloaded;
                    _sessions.Remove(box);
                }
                return;
            }
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                DropSessions(VisualTreeHelper.GetChild(root, i));
        }

        private void OnBoxUnloaded(object sender, RoutedEventArgs e)
        {
            // Recycle safety net: a box removed without ContainerChanging
            // (e.g. panel child swap) commits before it disappears.
            if (sender is TextBox box && _sessions.ContainsKey(box))
                DropSessions(box);
        }

        private void CommitBoxIfDirty(TextBox box)
        {
            if (!_sessions.TryGetValue(box, out var session)) return;
            if (session.Composing) return;
            var current = box.Text;
            string model;
            try { model = session.GetModel(); }
            catch { return; }
            if (current != model) session.Commit(current);
        }

        // Commits the focused box (if any) before a scroll/recycle that
        // would remove it. LostFocus usually got there first; this covers
        // programmatic scrolls.
        private void CommitActiveBox()
        {
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox box)
                CommitBoxIfDirty(box);
        }

        private void OnCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
        {
            if (_sessions.TryGetValue(sender, out var session)) session.Composing = true;
        }

        private void OnCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
        {
            if (_sessions.TryGetValue(sender, out var session)) session.Composing = false;
        }

        internal void Test_SetComposing(TextBox box, bool composing)
        {
            if (_sessions.TryGetValue(box, out var session)) session.Composing = composing;
        }

        private void OnEditKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (sender is not TextBox box) return;
            if (!_sessions.TryGetValue(box, out var session)) return;
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                // The Enter that confirms an IME candidate must NOT commit
                // and must not insert a newline (single-line boxes can't
                // take one anyway). Only the next Enter commits.
                if (session.Composing) return;
                CommitBoxIfDirty(box);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                try { SetTextPreservingCaret(box, session.GetModel()); }
                catch { }
                e.Handled = true;
            }
        }

        private void OnEditLostFocus(object sender, RoutedEventArgs e)
        {
            // Tab/Shift+Tab commit on the way out through this same path.
            if (sender is TextBox box) CommitBoxIfDirty(box);
        }

        private void OnEditGotFocus(object sender, RoutedEventArgs e)
        {
            // Focusing a box selects its line AND word; the ribbon Word
            // group follows the focused word. List selection is set first
            // (its change event selects the line), then the word.
            if (sender is not TextBox box || _selection is null || _store is null) return;
            var container = FindListViewItem(box);
            if (container is null) return;
            var index = LineList.IndexFromContainer(container);
            if (index < 0 || index >= _store.Document.Lines.Count) return;
            var line = _store.Document.Lines[index];
            LineList.SelectedItem = line;
            if (box.Tag is string wordId && line.Words.Any(w => w.Id == wordId))
                _selection.SelectWord(line.Id, wordId);
            else
                _selection.SelectLine(line.Id);
            LineList.ScrollIntoView(line);
        }

        private EditLine? LineOf(TextBox box)
        {
            if (_store is null) return null;
            var container = FindListViewItem(box);
            if (container is null) return null;
            var index = LineList.IndexFromContainer(container);
            return index < 0 || index >= _store.Document.Lines.Count
                ? null : _store.Document.Lines[index];
        }

        private void CommitWordTextBox(TextBox box, EditLine line, string wordId, bool roman)
        {
            var word = line.Words.FirstOrDefault(w => w.Id == wordId);
            if (word is null || _store is null) return;
            var raw = box.Text;
            if (roman)
            {
                if (raw != word.RomanWord)
                    _store.Execute(new SetWordRomanCommand(line.Id, wordId, raw));
                return;
            }
            // Trailing "|" seeds an (empty) ruby list, like the original's
            // parseRubyShortcut. The C# model holds Ruby as a collection
            // where empty == absent, so stripping the suffix is the whole op.
            var text = raw.EndsWith("|") ? raw[..^1] : raw;
            if (text != word.Text)
                _store.Execute(new SetWordTextCommand(line.Id, wordId, text));
        }

        private void CommitLineFieldBox(TextBox box, EditLine line, bool translation)
        {
            if (_store is null) return;
            var raw = box.Text;
            if (translation)
            {
                if (raw != line.TranslatedLyric)
                    _store.Execute(new SetTranslationCommand(line.Id, raw));
            }
            else if (raw != line.RomanLyric)
            {
                _store.Execute(new SetRomanizationCommand(line.Id, raw));
            }
        }

        // Line numbers, chip units, per-chip automation and the trailing "+"
        // are applied here (they need container indices, which templates
        // cannot see). Chip text boxes are the real editable boxes: commit
        // on Enter/blur as ONE history step, Esc reverts, unchanged = no
        // step. Uncommitted edits commit before recycle clears the panel.
        private void OnLineContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer.ContentTemplateRoot is not Grid card) return;
            if (args.Item is not EditLine line) return;
            var index = LineList.IndexFromContainer(args.ItemContainer);
            var number = card.FindName("LineNumber") as TextBlock;
            if (number is not null)
            {
                number.Text = (index + 1).ToString();
                AutomationProperties.SetName(number, $"Line number {index + 1}");
            }
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return;
            foreach (var child in panel.Children.OfType<Grid>().ToList())
                DropSessions(child);
            panel.Children.Clear();
            for (var i = 0; i < line.Words.Count; i++)
                panel.Children.Add(BuildChip(line, line.Words[i], i, index));
            var add = new Button
            {
                Content = new FontIcon { FontSize = 14, Glyph = "\uE710" },
                IsEnabled = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(add, "Add word (editing arrives in the next update)");
            AutomationProperties.SetName(add, $"Add word to line {index + 1}");
            panel.Children.Add(add);
            // Line field boxes persist across recycles: refresh delegates +
            // text instead of rewiring. BG/duet visuals follow flags.
            if (card.FindName("TransBox") is TextBox transField)
                WireBox(transField,
                    () => line.TranslatedLyric,
                    _ => CommitLineFieldBox(transField, line, translation: true));
            if (card.FindName("RomanBox") is TextBox romanField)
                WireBox(romanField,
                    () => line.RomanLyric,
                    _ => CommitLineFieldBox(romanField, line, translation: false));
            ApplyFlagVisual(args.ItemContainer, index, line);
            if (card.FindName("DeleteButton") is Button delete)
                AutomationProperties.SetName(delete, $"Delete line {index + 1}");
            // Stable per-line automation identities for the row boxes: the
            // UI-verification driver addresses rows by direct AutomationId
            // lookup (tree walks omit nodes), and an explicit Name keeps the
            // label association deterministic across documents.
            if (card.FindName("TransBox") is FrameworkElement transBox)
            {
                AutomationProperties.SetAutomationId(transBox, $"TransBox{index + 1}");
                AutomationProperties.SetName(transBox, "Translation");
            }
            if (card.FindName("RomanBox") is FrameworkElement romanBox)
            {
                AutomationProperties.SetAutomationId(romanBox, $"RomanBox{index + 1}");
                AutomationProperties.SetName(romanBox, "Romanization");
            }
            // Rows snap to the current toggle state on realization;
            // off-screen lines never animate (AnimateToggleStates covers
            // only realized containers).
            ApplyCardVisibility(card,
                _viewState?.ShowTranslation ?? true,
                _viewState?.ShowRomanization ?? true,
                _viewState?.ShowWordRomanization ?? true,
                animate: false, collapseNow: true);
        }

        private void OnViewStateChanged()
        {
            if (DispatcherQueue.HasThreadAccess) ApplyToggleStates(animate: true);
            else DispatcherQueue.TryEnqueue(() => ApplyToggleStates(animate: true));
        }

        private void ApplyToggleStates(bool animate)
        {
            if (_store is null || _viewState is null) return;
            if (animate && !AnimationsOn()) animate = false;
            if (animate) _toggleVersion++;
            // Snapshot BEFORE mutating: measuring after the change observes a
            // steady state and compensates nothing (observed: dy=32px on
            // toggle-off). The deferred-collapse shift is compensated again
            // in HideAfterAsync.
            var anchor = SnapshotSelected();
            var showTrans = _viewState.ShowTranslation;
            var showRoman = _viewState.ShowRomanization;
            var showWordRoman = _viewState.ShowWordRomanization;
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid card) continue;
                ApplyCardVisibility(card, showTrans, showRoman, showWordRoman, animate, collapseNow: false);
            }
            CompensateSelected(anchor);
            if (animate) StartShowAnimations();
        }

        private void ApplyCardVisibility(Grid card, bool showTrans, bool showRoman, bool showWordRoman, bool animate, bool collapseNow)
        {
            SetRow(card, "TransLabel", "TransBox", showTrans, animate, collapseNow);
            SetRow(card, "RomanLabel", "RomanBox", showRoman, animate, collapseNow);
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return;
            foreach (var child in panel.Children)
            {
                if (child is not Grid chip || chip.Children.Count < 2) continue;
                if (chip.Children[1] is FrameworkElement roman) SetElement(roman, showWordRoman, animate, collapseNow);
            }
        }

        private void SetRow(Grid card, string labelName, string boxName, bool show, bool animate, bool collapseNow)
        {
            if (card.FindName(labelName) is FrameworkElement label) SetElement(label, show, animate, collapseNow);
            if (card.FindName(boxName) is FrameworkElement box) SetElement(box, show, animate, collapseNow);
        }

        private readonly List<(FrameworkElement Element, Visual Visual)> _pendingShow = new();
        private readonly List<FrameworkElement> _pendingHide = new();

        private void SetElement(FrameworkElement el, bool show, bool animate, bool collapseNow)
        {
            if (!animate || _compositor is null)
            {
                el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                el.Opacity = 1;
                return;
            }
            var visual = ElementCompositionPreview.GetElementVisual(el);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Offset");
            if (show)
            {
                el.Visibility = Visibility.Visible;
                el.Opacity = 1;
                _pendingShow.Add((el, visual));
            }
            else if (collapseNow)
            {
                el.Visibility = Visibility.Collapsed;
                el.Opacity = 1;
            }
            else
            {
                _pendingHide.Add(el);
            }
        }

        private void StartShowAnimations()
        {
            foreach (var (el, visual) in _pendingShow)
            {
                var start = visual.Offset;
                visual.Opacity = 0;
                visual.Offset = new Vector3(start.X, start.Y + 8, start.Z);
                var fade = _compositor!.CreateScalarKeyFrameAnimation();
                fade.Duration = TimeSpan.FromMilliseconds(150);
                fade.InsertKeyFrame(1f, 1f, _decelerate);
                visual.StartAnimation("Opacity", fade);
                var slide = _compositor.CreateVector3KeyFrameAnimation();
                slide.Duration = TimeSpan.FromMilliseconds(150);
                slide.InsertKeyFrame(1f, new Vector3(start.X, start.Y, start.Z), _decelerate);
                visual.StartAnimation("Offset", slide);
            }
            _pendingShow.Clear();
            if (_pendingHide.Count == 0) return;
            var version = _toggleVersion;
            var hiding = _pendingHide.ToList();
            _pendingHide.Clear();
            foreach (var el in hiding)
            {
                var visual = ElementCompositionPreview.GetElementVisual(el);
                visual.StopAnimation("Opacity");
                var fade = _compositor!.CreateScalarKeyFrameAnimation();
                fade.Duration = TimeSpan.FromMilliseconds(150);
                fade.InsertKeyFrame(1f, 0f, _decelerate);
                visual.StartAnimation("Opacity", fade);
            }
            _ = HideAfterAsync(hiding, version);
        }

        private async Task HideAfterAsync(List<FrameworkElement> hiding, int version)
        {
            await Task.Delay(160);
            if (version != _toggleVersion) return;
            // Measure BEFORE collapsing: the fade-out rows still occupy layout,
            // so AnchorSelected (called earlier) saw no shift and compensated
            // nothing. The collapse below moves the selected card; compensate
            // that shift here or it would visibly jump (observed: dy=32px).
            double beforeY = double.NaN;
            ListViewItem? anchorContainer = null;
            if (LineList.SelectedItem is EditLine selected
                && LineList.ContainerFromItem(selected) is ListViewItem c)
            {
                anchorContainer = c;
                beforeY = c.TransformToVisual(LineList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            }
            foreach (var el in hiding) el.Visibility = Visibility.Collapsed;
            if (anchorContainer is not null && !double.IsNaN(beforeY))
            {
                LineList.UpdateLayout();
                var afterY = anchorContainer.TransformToVisual(LineList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                if (FindScroller() is ScrollViewer sv && Math.Abs(afterY - beforeY) > 0.5)
                    sv.ChangeView(null, sv.VerticalOffset + (afterY - beforeY), null, true);
            }
        }

        private ScrollViewer? _scroller;

        private ScrollViewer? FindScroller()
        {
            if (_scroller is not null) return _scroller;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(LineList);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is ScrollViewer sv)
                {
                    _scroller = sv;
                    return sv;
                }
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
            }
            return null;
        }

        // Keeps the selected line at its screen position across row
        // show/collapse: measure before, snap layout, then shift the scroll
        // offset by the delta (instant, unanimated). The reposition
        // transition glides everything else; the selected card does not move.
        private void AnchorSelected(bool animate)
        {
            _ = animate;
            if (LineList.SelectedItem is not EditLine selected) return;
            if (LineList.ContainerFromItem(selected) is not ListViewItem container) return;
            var before = container.TransformToVisual(LineList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            LineList.UpdateLayout();
            var after = container.TransformToVisual(LineList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            if (FindScroller() is ScrollViewer sv && Math.Abs(after - before) > 0.5)
                sv.ChangeView(null, sv.VerticalOffset + (after - before), null, true);
        }

        private (ListViewItem? Container, double Y) SnapshotSelected()
        {
            if (LineList.SelectedItem is EditLine selected
                && LineList.ContainerFromItem(selected) is ListViewItem container)
                return (container, container.TransformToVisual(LineList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y);
            return (null, double.NaN);
        }

        private void CompensateSelected((ListViewItem? Container, double Y) anchor)
        {
            if (anchor.Container is null || double.IsNaN(anchor.Y)) return;
            LineList.UpdateLayout();
            var after = anchor.Container.TransformToVisual(LineList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            if (FindScroller() is ScrollViewer sv && Math.Abs(after - anchor.Y) > 0.5)
                sv.ChangeView(null, sv.VerticalOffset + (after - anchor.Y), null, true);
        }

        // Flag visuals live here in code (no template change): automation
        // names carry BG/duet for AT; the visible badges land in Step 3.
        private void ApplyFlagVisual(DependencyObject container, int index, EditLine line)
        {
            var name = $"Line {index + 1}";
            if (line.IsBg) name += ", BG";
            if (line.IsDuet) name += ", duet";
            AutomationProperties.SetName(container, name);
        }

        private Grid BuildChip(EditLine line, EditWord word, int index, int lineIndex)
        {
            var chip = new Grid
            {
                MinWidth = 44,
                Tag = word.Id,
            };
            chip.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chip.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Raw model text (never the "Space ×N" display alias: committing
            // that alias would write it into the model). EmptyBeat shows as
            // a dimmed cue only.
            var isSpace = word.EmptyBeat > 0 && string.IsNullOrWhiteSpace(word.Text);
            var text = new TextBox
            {
                Text = word.Text,
                AcceptsReturn = false,
                IsTabStop = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = word.Id,
            };
            var roman = new TextBox
            {
                Text = word.RomanWord,
                AcceptsReturn = false,
                IsTabStop = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = word.Id,
            };
            Grid.SetRow(roman, 1);
            if (isSpace) chip.Opacity = 0.55;
            // The two boxes share the chip width (grid = max child width,
            // both stretch): widths hug the wider content instantly while
            // typing, with no animation and no rebuild, so caret/focus stay.
            AutomationProperties.SetAutomationId(text, $"ChipText{lineIndex + 1}_{index + 1}");
            AutomationProperties.SetName(text, $"Word {index + 1}: {word.Text}");
            AutomationProperties.SetAutomationId(roman, $"ChipRoman{lineIndex + 1}_{index + 1}");
            AutomationProperties.SetName(roman, $"Romanization of word {index + 1}");
            var wordId = word.Id;
            WireBox(text,
                () => line.Words.FirstOrDefault(w => w.Id == wordId)?.Text ?? "",
                _ => CommitWordTextBox(text, line, wordId, roman: false));
            WireBox(roman,
                () => line.Words.FirstOrDefault(w => w.Id == wordId)?.RomanWord ?? "",
                _ => CommitWordTextBox(roman, line, wordId, roman: true));
            // handledEventsToo: TextBoxes mark pointer input handled, which
            // would otherwise starve both this handler and item select.
            // (GotFocus covers keyboard focus; this covers pointer select.)
            chip.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(OnChipPointerPressed), true);
            chip.Children.Add(text);
            chip.Children.Add(roman);
            return chip;
        }

        private void OnChipPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Grid chip || chip.Tag is not string wordId) return;
            if (_selection is null || _store is null) return;
            var container = FindListViewItem(chip);
            if (container is null) return;
            var index = LineList.IndexFromContainer(container);
            if (index < 0 || index >= _store.Document.Lines.Count) return;
            var line = _store.Document.Lines[index];
            LineList.SelectedItem = line;
            _selection.SelectWord(line.Id, wordId);
            e.Handled = true;
        }

        private void OnLineSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selection is null) return;
            if (LineList.SelectedItem is EditLine line) _selection.SelectLine(line.Id);
            else _selection.Clear();
        }

        private static ListViewItem? FindListViewItem(DependencyObject start)
        {
            var current = VisualTreeHelper.GetParent(start);
            while (current is not null && current is not ListViewItem)
                current = VisualTreeHelper.GetParent(current);
            return current as ListViewItem;
        }
    }
}
