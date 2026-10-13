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
        // Structural watchers: word add/remove/replace inside a line does NOT
        // recycle its container, so the panel is rebuilt here (with focus
        // restore). Text edits never touch the collection: no rebuild, no
        // focus risk.
        private readonly Dictionary<EditLine, System.Collections.Specialized.NotifyCollectionChangedEventHandler> _wordWatchers = new();
        private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _linesWatcher;
        private System.ComponentModel.PropertyChangedEventHandler? _docWatcher;

        public void Attach(LyricStore store, SelectionService selection, LyricViewStateService viewState)
        {
            Detach();
            _store = store;
            _selection = selection;
            _viewState = viewState;
            _store.Changed += OnStoreChanged;
            _viewState.Changed += OnViewStateChanged;
            WatchLines();
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

        private EditDocument? _watchedDoc;

        private void WatchLines()
        {
            if (_store is null) return;
            UnwatchLines();
            var doc = _store.Document;
            _watchedDoc = doc;
            foreach (var line in doc.Lines) WatchLine(line);
            _linesWatcher = (_, e) =>
            {
                if (e.NewItems is not null)
                    foreach (EditLine line in e.NewItems) WatchLine(line);
                if (e.OldItems is not null)
                    foreach (EditLine line in e.OldItems) UnwatchLine(line);
            };
            doc.Lines.CollectionChanged += _linesWatcher;
            _docWatcher = (_, e) =>
            {
                if (e.PropertyName == nameof(LyricStore.Document)) WatchLines();
            };
            _store.PropertyChanged += _docWatcher;
        }

        private void WatchLine(EditLine line)
        {
            if (_wordWatchers.ContainsKey(line)) return;
            System.Collections.Specialized.NotifyCollectionChangedEventHandler handler = (_, _) =>
            {
                if (DispatcherQueue.HasThreadAccess) RebuildLinePanelFor(line);
                else DispatcherQueue.TryEnqueue(() => RebuildLinePanelFor(line));
            };
            _wordWatchers[line] = handler;
            line.Words.CollectionChanged += handler;
        }

        private void UnwatchLine(EditLine line)
        {
            if (_wordWatchers.Remove(line, out var handler))
                line.Words.CollectionChanged -= handler;
        }

        private void UnwatchLines()
        {
            if (_watchedDoc is not null && _linesWatcher is not null)
                _watchedDoc.Lines.CollectionChanged -= _linesWatcher;
            if (_store is not null && _docWatcher is not null)
                _store.PropertyChanged -= _docWatcher;
            _watchedDoc = null;
            _linesWatcher = null;
            _docWatcher = null;
            foreach (var (line, handler) in _wordWatchers)
                line.Words.CollectionChanged -= handler;
            _wordWatchers.Clear();
        }

        private void RebuildLinePanelFor(EditLine line)
        {
            if (_store is null) return;
            var index = _store.Document.Lines.IndexOf(line);
            if (index >= 0) RebuildLinePanel(index);
        }

        // Rebuilds one line's chip row (word add/remove/split/merge/undo).
        // Uncommitted text commits first; if the focused box lived in this
        // row, focus returns to the same word+field with the caret clamped.
        private void RebuildLinePanel(int index)
        {
            if (_store is null || index < 0 || index >= _store.Document.Lines.Count) return;
            if (LineList.ContainerFromIndex(index) is not ListViewItem container) return;
            if (container.ContentTemplateRoot is not Grid card) return;
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return;
            var focused = FocusManager.GetFocusedElement(XamlRoot) as TextBox;
            string? focusWord = null;
            bool focusRoman = false;
            int caret = 0;
            if (focused is not null && _sessions.ContainsKey(focused) && IsIn(panel, focused))
            {
                CommitBoxIfDirty(focused);
                focusWord = focused.Tag as string;
                focusRoman = Grid.GetRow(focused) == 1;
                caret = focused.SelectionStart;
            }
            BuildLinePanel(panel, _store.Document.Lines[index], index);
            if (focusWord is not null)
            {
                var line = _store.Document.Lines[index];
                var wi = line.Words.ToList().FindIndex(w => w.Id == focusWord);
                if (wi >= 0 && FindChipBox(index, wi, focusRoman) is TextBox box)
                {
                    box.Focus(FocusState.Programmatic);
                    box.SelectionStart = Math.Min(caret, box.Text.Length);
                }
            }
        }

        private static bool IsIn(DependencyObject root, DependencyObject target)
        {
            if (ReferenceEquals(root, target)) return true;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                if (IsIn(VisualTreeHelper.GetChild(root, i), target)) return true;
            return false;
        }

        private string? _insertLineId;
        private TextBox? _insertBox;
        private string _insertTextStash = "";

        private void BuildLinePanel(LyricWrapPanel panel, EditLine line, int index)
        {
            foreach (var child in panel.Children.OfType<Grid>().ToList())
                DropSessions(child);
            if (_insertBox is not null && IsIn(panel, _insertBox))
            {
                _insertTextStash = _insertBox.Text;
                _insertBox.KeyDown -= OnInsertKeyDown;
                _insertBox.LostFocus -= OnInsertLostFocus;
            }
            _insertBox = null;
            panel.Children.Clear();
            for (var i = 0; i < line.Words.Count; i++)
                panel.Children.Add(BuildChip(line, line.Words[i], i, index));
            var add = new Button
            {
                Content = new FontIcon { FontSize = 14, Glyph = "\uE710" },
                VerticalAlignment = VerticalAlignment.Center,
                Tag = line.Id,
            };
            add.Click += OnAddWordClick;
            ToolTipService.SetToolTip(add, "Add word");
            AutomationProperties.SetAutomationId(add, $"AddWord{index + 1}");
            AutomationProperties.SetName(add, $"Add word to line {index + 1}");
            panel.Children.Add(add);
            // Inline "Insert word here..." box (original insertWordField):
            // open at the row end while adding; Enter commits a new word
            // (default 0/0 timing) and keeps focus for repeat adds.
            if (_insertLineId == line.Id)
            {
                var insert = new TextBox
                {
                    PlaceholderText = "Insert word here...",
                    AcceptsReturn = false,
                    MinWidth = 120,
                    VerticalAlignment = VerticalAlignment.Center,
                    Text = _insertTextStash,
                };
                AutomationProperties.SetAutomationId(insert, $"InsertBox{index + 1}");
                AutomationProperties.SetName(insert, $"Insert word into line {index + 1}");
                insert.KeyDown += OnInsertKeyDown;
                insert.LostFocus += OnInsertLostFocus;
                _insertBox = insert;
                panel.Children.Add(insert);
            }
        }

        private void OnAddWordClick(object sender, RoutedEventArgs e)
        {
            if (_store is null) return;
            var lineId = (sender as Button)?.Tag as string;
            var line = lineId is null ? null
                : _store.Document.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) return;
            var index = _store.Document.Lines.IndexOf(line);
            if (_insertLineId == line.Id)
            {
                CloseInsertBox();
                RebuildLinePanel(index);
                return;
            }
            CommitActiveBox();
            _insertLineId = line.Id;
            _insertTextStash = "";
            RebuildLinePanel(index);
            _insertBox?.Focus(FocusState.Programmatic);
        }

        internal bool OpenInsertBox(int lineIndex)
        {
            if (_store is null || lineIndex < 0 || lineIndex >= _store.Document.Lines.Count) return false;
            CommitActiveBox();
            _insertLineId = _store.Document.Lines[lineIndex].Id;
            _insertTextStash = "";
            LineList.ScrollIntoView(LineList.Items[lineIndex]);
            LineList.UpdateLayout();
            RebuildLinePanel(lineIndex);
            if (_insertBox is null) return false;
            _insertBox.Focus(FocusState.Programmatic);
            return true;
        }

        private void CloseInsertBox()
        {
            _insertLineId = null;
            _insertTextStash = "";
            _insertBox = null;
        }

        internal bool CommitInsertBox()
        {
            if (_store is null || _insertBox is null) return false;
            var lineId = _insertLineId;
            var line = lineId is null ? null
                : _store.Document.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) return false;
            var raw = _insertBox.Text;
            // Trailing "|" seeds ruby (model: empty == absent, so strip).
            var text = raw.EndsWith("|") ? raw[..^1] : raw;
            var word = EditWord.New();
            word.Text = text;
            _store.Execute(new AddWordCommand(line.Id, line.Words.Count, word));
            _selection?.SelectWord(line.Id, word.Id);
            _insertBox.Text = "";
            _insertTextStash = "";
            _insertBox.Focus(FocusState.Programmatic);
            return true;
        }

        private void OnInsertKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_store is null) return;
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                CommitInsertBox();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                var lineId = _insertLineId;
                CloseInsertBox();
                if (lineId is not null)
                {
                    var idx = _store.Document.Lines.ToList().FindIndex(l => l.Id == lineId);
                    if (idx >= 0) RebuildLinePanel(idx);
                }
                e.Handled = true;
            }
        }

        private void OnInsertLostFocus(object sender, RoutedEventArgs e)
        {
            // Blur keeps the draft (stash) but closes nothing: re-opening
            // restores it. The box commits only on Enter, like the original.
            if (sender is TextBox box) _insertTextStash = box.Text;
        }

        public void Detach()
        {
            UnwatchLines();
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
            if (DispatcherQueue.HasThreadAccess) { RefreshEmptyState(); RefreshBoxTexts(); RefreshFlagVisuals(); }
            else DispatcherQueue.TryEnqueue(() => { RefreshEmptyState(); RefreshBoxTexts(); RefreshFlagVisuals(); });
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

        private readonly HashSet<DependencyObject> _wiredCards = new();

        private void OnDeleteButtonClick(object sender, RoutedEventArgs e)
        {
            if (_store is null) return;
            var container = FindListViewItem((DependencyObject)sender);
            if (container is null) return;
            DeleteLineAt(LineList.IndexFromContainer(container));
        }

        // --- context menus (standard MenuFlyout, accelerator text shown) ---

        private void RebuildLineMenu(MenuFlyout flyout, Grid card)
        {
            flyout.Items.Clear();
            if (_store is null || _selection is null) return;
            var container = FindListViewItem(card);
            if (container is null) return;
            var index = LineList.IndexFromContainer(container);
            if (index < 0 || index >= _store.Document.Lines.Count) return;
            var line = _store.Document.Lines[index];
            foreach (var item in BuildLineMenuItems(index, line.Id))
                flyout.Items.Add(item);
        }

        private List<MenuFlyoutItemBase> BuildLineMenuItems(int index, string lineId)
        {
            var items = new List<MenuFlyoutItemBase>();
            var selCount = _selection?.SelectedLineIds.Count ?? 0;
            var before = new MenuFlyoutItem { Text = "Insert line before" };
            before.Click += (_, _) => InsertLineBefore(index);
            var after = new MenuFlyoutItem { Text = "Insert line after" };
            after.Click += (_, _) => InsertLineAfter(index);
            var dup = new MenuFlyoutItem
            {
                Text = "Duplicate line",
                KeyboardAcceleratorTextOverride = "Ctrl+D",
            };
            dup.Click += (_, _) => DuplicateSelectedLines();
            var combine = new MenuFlyoutItem { Text = "Combine lines" };
            combine.IsEnabled = selCount >= 2;
            combine.Click += (_, _) => CombineSelectedLines();
            var up = new MenuFlyoutItem
            {
                Text = "Move line up",
                KeyboardAcceleratorTextOverride = "Alt+Up",
            };
            up.Click += (_, _) =>
            {
                if (_selection?.SelectedLineId is null && _store is not null)
                {
                    var l = _store.Document.Lines[index];
                    LineList.SelectedItem = l;
                    _selection?.SelectLine(l.Id);
                }
                MovePrimaryLine(-1);
            };
            var down = new MenuFlyoutItem
            {
                Text = "Move line down",
                KeyboardAcceleratorTextOverride = "Alt+Down",
            };
            down.Click += (_, _) =>
            {
                if (_selection?.SelectedLineId is null && _store is not null)
                {
                    var l = _store.Document.Lines[index];
                    LineList.SelectedItem = l;
                    _selection?.SelectLine(l.Id);
                }
                MovePrimaryLine(1);
            };
            var bg = new ToggleMenuFlyoutItem { Text = "BG lyric" };
            var duet = new ToggleMenuFlyoutItem { Text = "Duet lyric" };
            if (_store is not null)
            {
                var cur = _store.Document.Lines
                    .FirstOrDefault(l => l.Id == lineId);
                bg.IsChecked = cur?.IsBg == true;
                duet.IsChecked = cur?.IsDuet == true;
            }
            bg.Click += (_, _) => ToggleLineFlag(lineId, isBg: true, bg.IsChecked);
            duet.Click += (_, _) => ToggleLineFlag(lineId, isBg: false, duet.IsChecked);
            var del = new MenuFlyoutItem
            {
                Text = "Delete line",
                KeyboardAcceleratorTextOverride = "Delete",
            };
            del.Click += (_, _) => DeleteLineAt(index);
            items.Add(before);
            items.Add(after);
            items.Add(dup);
            items.Add(combine);
            items.Add(new MenuFlyoutSeparator());
            items.Add(up);
            items.Add(down);
            items.Add(new MenuFlyoutSeparator());
            items.Add(bg);
            items.Add(duet);
            items.Add(new MenuFlyoutSeparator());
            items.Add(del);
            return items;
        }

        private void ToggleLineFlag(string lineId, bool isBg, bool value)
        {
            if (_store is null) return;
            CommitActiveBox();
            _store.Execute(new SetLineFlagsCommand(lineId,
                isBg: isBg ? value : null,
                isDuet: isBg ? null : value));
        }

        private void AttachWordMenu(Grid chip, EditLine line, string wordId)
        {
            var flyout = new MenuFlyout();
            flyout.Opening += (_, _) =>
            {
                flyout.Items.Clear();
                foreach (var item in BuildWordMenuItems(line, wordId))
                    flyout.Items.Add(item);
            };
            chip.ContextFlyout = flyout;
        }

        private List<MenuFlyoutItemBase> BuildWordMenuItems(EditLine line, string wordId)
        {
            var items = new List<MenuFlyoutItemBase>();
            var wordCount = _selection?.SelectedWordIds.Count ?? 0;
            var lineWordSel = line.Words.Count(w => _selection?.SelectedWordIds.Contains(w.Id) == true);
            var split = new MenuFlyoutItem { Text = "Split word..." };
            split.Click += (_, _) => ShowSplitDialog(line.Id, wordId);
            var replace = new MenuFlyoutItem { Text = "Replace word..." };
            replace.Click += (_, _) => ShowReplaceDialog(line.Id, wordId);
            var merge = new MenuFlyoutItem { Text = "Merge words" };
            merge.IsEnabled = lineWordSel > 1;
            merge.Click += (_, _) => MergeSelectedWords(line.Id);
            var delWords = new MenuFlyoutItem
            {
                Text = "Delete selected words",
                KeyboardAcceleratorTextOverride = "Delete",
            };
            delWords.IsEnabled = wordCount > 0;
            delWords.Click += (_, _) => DeleteSelected();
            var toNew = new MenuFlyoutItem { Text = "Move selected words to new line" };
            toNew.IsEnabled = wordCount > 0;
            toNew.Click += (_, _) => MoveSelectedWordsToNewLine(line.Id);
            var following = new MenuFlyoutItem { Text = "Move this and following words to new line" };
            following.Click += (_, _) => MoveFollowingWordsToNewLine(line.Id, wordId);
            items.Add(split);
            items.Add(replace);
            items.Add(merge);
            items.Add(delWords);
            items.Add(new MenuFlyoutSeparator());
            items.Add(toNew);
            items.Add(following);
            items.Add(new MenuFlyoutSeparator());
            foreach (var item in BuildLineMenuItems(
                _store?.Document.Lines.IndexOf(line) ?? -1, line.Id))
                items.Add(item);
            return items;
        }

        // Split/replace go through small dialogs (original parity). In test
        // mode real dialogs never open (stub rule); the Test_ hooks drive
        // the same one-step commands instead.
        private async void ShowSplitDialog(string lineId, string wordId)
        {
#if DEBUG
            if (Services.HarnessHost.IsTest) return;
#endif
            if (_store is null) return;
            var word = _store.Document.Lines
                .FirstOrDefault(l => l.Id == lineId)?.Words
                .FirstOrDefault(w => w.Id == wordId);
            if (word is null) return;
            var input = new TextBox
            {
                PlaceholderText = "Split positions, e.g. 2,5",
                Text = "",
            };
            var dialog = new ContentDialog
            {
                Title = $"Split word \"{word.Text}\"",
                Content = input,
                PrimaryButtonText = "Split",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            AutomationProperties.SetAutomationId(dialog, "SplitWordDialog");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var indices = input.Text.Split(',',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : -1)
                .Where(n => n > 0 && n < word.Text.Length)
                .ToList();
            if (indices.Count > 0) SplitWordAt(lineId, wordId, indices);
        }

        private async void ShowReplaceDialog(string lineId, string wordId)
        {
#if DEBUG
            if (Services.HarnessHost.IsTest) return;
#endif
            if (_store is null) return;
            var word = _store.Document.Lines
                .FirstOrDefault(l => l.Id == lineId)?.Words
                .FirstOrDefault(w => w.Id == wordId);
            if (word is null) return;
            var input = new TextBox { PlaceholderText = "Replacement text" };
            var dialog = new ContentDialog
            {
                Title = $"Replace word \"{word.Text}\"",
                Content = input,
                PrimaryButtonText = "Replace",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            AutomationProperties.SetAutomationId(dialog, "ReplaceWordDialog");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            ReplaceWord(lineId, wordId, word.Text, input.Text);
        }

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

        // --- Step-2 structure hooks (UI thread) ----------------------------
        internal int SelectedLineCount() => _selection?.SelectedLineIds.Count ?? -1;
        internal int SelectedWordCount() => _selection?.SelectedWordIds.Count ?? -1;

        internal void SelectLines(IEnumerable<int> indices)
        {
            if (_store is null) return;
            LineList.SelectedItems.Clear();
            foreach (var i in indices)
            {
                if (i >= 0 && i < _store.Document.Lines.Count)
                    LineList.SelectedItems.Add(_store.Document.Lines[i]);
            }
        }

        internal void SelectAllLines()
        {
            CommitActiveBox();
            LineList.SelectAll();
        }

        internal void ClearEditSelection()
        {
            LineList.SelectedItem = null;
            _selection?.Clear();
        }

        internal bool ToggleChipWord(int line, int word)
        {
            if (_store is null || _selection is null) return false;
            if (line < 0 || line >= _store.Document.Lines.Count) return false;
            var docLine = _store.Document.Lines[line];
            if (word < 0 || word >= docLine.Words.Count) return false;
            if (!LineList.SelectedItems.Contains(docLine))
                LineList.SelectedItems.Add(docLine);
            _selection.ToggleWord(docLine.Id, docLine.Words[word].Id);
            _selection.RetainWords(LineOfWord);
            return true;
        }

        internal bool InsertBoxOpen() => _insertBox is not null;

        internal void SetInsertText(string text)
        {
            if (_insertBox is not null) _insertBox.Text = text;
        }

        internal string InsertText() => _insertBox?.Text ?? "nobox";

        internal bool EscapeInsertBox()
        {
            if (_insertBox is null) return false;
            CloseInsertBox();
            return true;
        }

        internal int LineIndexOfSelected() => SelectedLineIndex();

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
            BuildLinePanel(panel, line, index);
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
            // Delete + context menu are wired once per card root (roots are
            // pooled across lines; handlers resolve the line dynamically).
            if (_wiredCards.Add(card))
            {
                if (card.FindName("DeleteButton") is Button deleteBtn)
                    deleteBtn.Click += OnDeleteButtonClick;
                var flyout = new MenuFlyout();
                flyout.Opening += (_, _) => RebuildLineMenu(flyout, card);
                card.ContextFlyout = flyout;
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

        // Flag visuals: BG/duet badges (WinUI tinted-border style) plus an
        // automation-name suffix for AT.
        private void ApplyFlagVisual(DependencyObject container, int index, EditLine line)
        {
            var name = $"Line {index + 1}";
            if (line.IsBg) name += ", BG";
            if (line.IsDuet) name += ", duet";
            AutomationProperties.SetName(container, name);
            if (container is ListViewItem item
                && item.ContentTemplateRoot is Grid card)
            {
                if (card.FindName("BgBadge") is FrameworkElement bg)
                    bg.Visibility = line.IsBg ? Visibility.Visible : Visibility.Collapsed;
                if (card.FindName("DuetBadge") is FrameworkElement duet)
                    duet.Visibility = line.IsDuet ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void RefreshFlagVisuals()
        {
            if (_store is null) return;
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                ApplyFlagVisual(container, i, _store.Document.Lines[i]);
            }
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
            AttachWordMenu(chip, line, wordId);
            chip.Children.Add(text);
            chip.Children.Add(roman);
            return chip;
        }

        private static bool IsCtrlDown() =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        private static bool IsAltDown() =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        private void OnChipPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Grid chip || chip.Tag is not string wordId) return;
            if (_selection is null || _store is null) return;
            var container = FindListViewItem(chip);
            if (container is null) return;
            var index = LineList.IndexFromContainer(container);
            if (index < 0 || index >= _store.Document.Lines.Count) return;
            var line = _store.Document.Lines[index];
            if (IsCtrlDown())
            {
                // Multi-select: toggle the word, keep the line selected.
                if (!LineList.SelectedItems.Contains(line))
                    LineList.SelectedItems.Add(line);
                _selection.ToggleWord(line.Id, wordId);
                _selection.RetainWords(LineOfWord);
            }
            else
            {
                LineList.SelectedItem = line;
                _selection.SelectWord(line.Id, wordId);
            }
            e.Handled = true;
        }

        private string? LineOfWord(string wordId) =>
            _store?.Document.Lines
                .FirstOrDefault(l => l.Words.Any(w => w.Id == wordId))?.Id;

        private void OnLineSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selection is null) return;
            var added = e.AddedItems.OfType<EditLine>().Select(l => l.Id).ToList();
            var removed = e.RemovedItems.OfType<EditLine>().Select(l => l.Id).ToList();
            if (added.Count == 0 && removed.Count == 0) return;
            _selection.SyncLineSet(added, removed, LineOfWord);
        }

        // List-scoped keys (KeyDown, NOT accelerators: a focused TextBox
        // marks text-editing keys handled, so typing never triggers these).
        private void OnLineListKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_store is null || _selection is null) return;
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
            if (e.Key == Windows.System.VirtualKey.Delete)
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                _selection.Clear();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.D && IsCtrlDown())
            {
                DuplicateSelectedLines();
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.A && IsCtrlDown())
            {
                // Original Ctrl+A toggles word/line scope: words selected →
                // all words of the selected lines; else all lines.
                if ((_selection?.SelectedWordIds.Count ?? 0) > 0
                    && _selection?.SelectedLineId is string lid && _store is not null)
                {
                    var selLine = _store.Document.Lines.FirstOrDefault(l => l.Id == lid);
                    if (selLine is not null)
                        _selection.SelectAllWords(lid, selLine.Words.Select(w => w.Id));
                }
                else SelectAllLines();
                e.Handled = true;
            }
            else if ((e.Key == Windows.System.VirtualKey.Up
                || e.Key == Windows.System.VirtualKey.Down) && IsAltDown())
            {
                MovePrimaryLine(e.Key == Windows.System.VirtualKey.Up ? -1 : 1);
                e.Handled = true;
            }
        }

        // --- structure ops (each Execute = one history step) ----------------

        internal void DeleteSelected()
        {
            if (_store is null || _selection is null) return;
            CommitActiveBox();
            var wordIds = _selection.SelectedWordIds.ToList();
            if (wordIds.Count > 0)
            {
                // Original Delete with a word selection strips the words
                // globally (no line splice); selection clears.
                var wanted = new HashSet<string>(wordIds);
                var cmds = new List<ILyricCommand>();
                foreach (var line in _store.Document.Lines)
                {
                    var ids = line.Words
                        .Where(w => wanted.Contains(w.Id)).Select(w => w.Id).ToList();
                    if (ids.Count > 0) cmds.Add(new RemoveWordsCommand(line.Id, ids));
                }
                if (cmds.Count > 0)
                    _store.Execute(new CompositeCommand("Remove words", cmds));
                _selection.Clear();
                return;
            }
            var lineIds = _selection.SelectedLineIds.ToList();
            if (lineIds.Count == 0) return;
            var firstIndex = _store.Document.Lines
                .Select((l, i) => (l, i)).Where(t => lineIds.Contains(t.l.Id))
                .Select(t => t.i).DefaultIfEmpty(0).Min();
            _store.Execute(new RemoveLinesCommand(lineIds));
            _selection.Clear();
            // Selection moves to the neighboring line.
            var neighbor = Math.Min(firstIndex, _store.Document.Lines.Count - 1);
            if (neighbor >= 0) LineList.SelectedItem = _store.Document.Lines[neighbor];
        }

        internal void DeleteLineAt(int index)
        {
            if (_store is null || _selection is null) return;
            if (index < 0 || index >= _store.Document.Lines.Count) return;
            CommitActiveBox();
            var id = _store.Document.Lines[index].Id;
            var ids = _selection.SelectedLineIds.Contains(id)
                ? _selection.SelectedLineIds.ToList() : new List<string> { id };
            _store.Execute(new RemoveLinesCommand(ids));
            _selection.Clear();
            var neighbor = Math.Min(index, _store.Document.Lines.Count - 1);
            if (neighbor >= 0) LineList.SelectedItem = _store.Document.Lines[neighbor];
        }

        internal void InsertLineBefore(int index)
        {
            if (_store is null || _selection is null) return;
            CommitActiveBox();
            index = Math.Clamp(index, 0, _store.Document.Lines.Count);
            var line = EditLine.New();
            _store.Execute(new AddLineCommand(index, line));
            LineList.SelectedItem = line;
            _selection.SelectLine(line.Id);
            OpenInsertBox(index);
        }

        internal void InsertLineAfter(int index)
        {
            if (_store is null) return;
            InsertLineBefore(index + 1);
        }

        internal void AddLineAfterSelected()
        {
            if (_store is null || _selection is null) return;
            CommitActiveBox();
            int at = _store.Document.Lines.Count;
            if (_selection.SelectedLineId is string id)
            {
                var found = _store.Document.Lines.ToList().FindIndex(l => l.Id == id);
                if (found >= 0) at = found + 1;
            }
            var line = EditLine.New();
            _store.Execute(new AddLineCommand(at, line));
            LineList.SelectedItem = line;
            _selection.SelectLine(line.Id);
            OpenInsertBox(at);
        }

        internal void DuplicateSelectedLines()
        {
            if (_store is null || _selection is null) return;
            CommitActiveBox();
            var ids = _store.Document.Lines
                .Where(l => _selection.SelectedLineIds.Contains(l.Id))
                .Select(l => l.Id).ToList();
            if (ids.Count == 0) return;
            _store.Execute(new DuplicateLinesCommand(ids));
        }

        internal void MovePrimaryLine(int delta)
        {
            if (_store is null || _selection is null) return;
            if (_selection.SelectedLineId is not string id) return;
            CommitActiveBox();
            var lines = _store.Document.Lines;
            var at = lines.ToList().FindIndex(l => l.Id == id);
            if (at < 0) return;
            var to = Math.Clamp(at + delta, 0, lines.Count - 1);
            if (to == at) return;
            _store.Execute(new MoveLineCommand(id, to));
            LineList.ScrollIntoView(lines[to]);
        }

        internal bool CombineSelectedLines()
        {
            if (_store is null || _selection is null) return false;
            CommitActiveBox();
            var idx = _store.Document.Lines
                .Select((l, i) => (l, i))
                .Where(t => _selection.SelectedLineIds.Contains(t.l.Id))
                .Select(t => t.i).OrderBy(i => i).ToList();
            if (idx.Count < 2 || idx.Max() - idx.Min() + 1 != idx.Count) return false;
            var ids = idx.Select(i => _store.Document.Lines[i].Id).ToList();
            _store.Execute(new CombineLinesCommand(ids));
            return true;
        }

        internal bool MergeSelectedWords(string lineId)
        {
            if (_store is null || _selection is null) return false;
            CommitActiveBox();
            var line = _store.Document.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) return false;
            var ids = line.Words
                .Where(w => _selection.SelectedWordIds.Contains(w.Id))
                .Select(w => w.Id).ToList();
            if (ids.Count <= 1) return false;
            _store.Execute(new MergeWordsCommand(lineId, ids));
            return true;
        }

        internal void RemoveSelectedWordsEverywhere()
        {
            DeleteSelected();
        }

        internal bool SplitWordAt(string lineId, string wordId, IEnumerable<int> indices)
        {
            if (_store is null) return false;
            CommitActiveBox();
            try
            {
                _store.Execute(new SplitWordCommand(lineId, wordId, indices));
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        internal void ReplaceWord(string lineId, string wordId, string target, string replacement)
        {
            if (_store is null) return;
            CommitActiveBox();
            _store.Execute(new ReplaceWordTextCommand(target, replacement,
                ignoreCase: false, onlyWordIds: new[] { wordId }));
        }

        internal bool MoveSelectedWordsToNewLine(string anchorLineId)
        {
            if (_store is null || _selection is null) return false;
            CommitActiveBox();
            var ids = _selection.SelectedWordIds.ToList();
            if (ids.Count == 0) return false;
            try
            {
                _store.Execute(new MoveWordsToNewLineCommand(anchorLineId, ids));
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        internal bool MoveFollowingWordsToNewLine(string lineId, string wordId)
        {
            if (_store is null) return false;
            CommitActiveBox();
            try
            {
                _store.Execute(new MoveFollowingWordsToNewLineCommand(lineId, wordId));
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }

        internal void AddWordToLine(string lineId, string text)
        {
            if (_store is null || _selection is null) return;
            CommitActiveBox();
            var line = _store.Document.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) return;
            text = text.EndsWith("|") ? text[..^1] : text;
            var word = EditWord.New();
            word.Text = text;
            _store.Execute(new AddWordCommand(lineId, line.Words.Count, word));
            _selection.SelectWord(lineId, word.Id);
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
