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
using Microsoft.UI.Xaml.Shapes;
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
        private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

        public EditPage()
        {
            this.InitializeComponent();
            this.Unloaded += (_, _) => Detach();
            // Theme/accent changes: XAML {ThemeResource} parts (card tint,
            // pill) follow automatically; CODE-created chip brushes (outline,
            // wash, space label) are re-resolved + repainted here so Dark and
            // High Contrast never keep stale Light values (the unreadable-
            // selection root cause: Application.Current.Resources lookups are
            // NOT theme-resolved). Accent-only changes do not raise
            // ActualThemeChanged, so UISettings changes refresh too.
            this.ActualThemeChanged += (_, _) =>
            {
                ApplySelectionVisuals();
                RefreshChipThemeBrushes();
            };
            try
            {
                _uiSettings.ColorValuesChanged += (_, _) =>
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        ApplySelectionVisuals();
                        RefreshChipThemeBrushes();
                    });
            }
            catch { }
            // Drag tracking (handledEventsToo: presses on boxes are marked
            // handled, but moves/releases must still reach the session).
            this.AddHandler(UIElement.PointerMovedEvent,
                new PointerEventHandler(OnDragPointerMoved), true);
            this.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler(OnDragPointerReleased), true);
            this.AddHandler(UIElement.PointerCaptureLostEvent,
                new PointerEventHandler(OnDragCaptureLost), true);
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
            // The accent bar + selected fill are driven from ONE source (the
            // selection set) on every selection change, prepare and recycle.
            _selection.Changed += ApplySelectionVisuals;
            // The insert box lives on exactly one line: any selection move to
            // another line (click, keys, menu, drag, delete) closes it. A
            // select on its own line (e.g. the commit reselect) keeps it.
            // Window deactivation never touches it (only drags cancel there).
            _selection.Changed += CloseInsertOnSelectionMove;
            WatchLines();
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _decelerate = _compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));
            // Glide for cards below a resized sibling + fade for add/remove.
            // Set in code (not XAML) so the animations-off setting snaps.
            LineList.ItemContainerTransitions =
                AnimationsOn()
                    ? new TransitionCollection
                    {
                        new AddDeleteThemeTransition(),
                        new RepositionThemeTransition(),
                    }
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
            // FLIP snapshot: word-id -> position, so surviving chips glide
            // to their new spots after the rebuild (same 150ms decelerate
            // as the toggle animations). Typing never rebuilds: unaffected.
            var before = new Dictionary<string, Windows.Foundation.Point>();
            if (AnimationsOn())
            {
                foreach (var child in panel.Children)
                {
                    if (child is Grid chip && chip.Tag is string id)
                        before[id] = chip.TransformToVisual(panel)
                            .TransformPoint(new Windows.Foundation.Point(0, 0));
                }
            }
            BuildLinePanel(panel, _store.Document.Lines[index], index);
            // Panel-level layout only: a list-wide UpdateLayout on a large
            // doc costs ~100ms+ per structural op for no benefit here.
            panel.UpdateLayout();
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
            if (AnimationsOn()) AnimatePanelFlip(panel, before);
        }

        // Entrance for added chips + glide for moved survivors. The chip
        // holding focus is never touched (no visual disturbance).
        private void AnimatePanelFlip(
            LyricWrapPanel panel, Dictionary<string, Windows.Foundation.Point> before)
        {
            if (_compositor is null || _decelerate is null) return;
            // Layout is already current (caller ran panel.UpdateLayout()).
            var focused = FocusManager.GetFocusedElement(XamlRoot) as TextBox;
            foreach (var child in panel.Children)
            {
                if (child is not Grid chip || chip.Tag is not string id) continue;
                if (focused is not null && IsIn(chip, focused)) continue;
                var visual = ElementCompositionPreview.GetElementVisual(chip);
                visual.StopAnimation("Opacity");
                visual.StopAnimation("Offset");
                if (!before.ContainsKey(id))
                {
                    var start = visual.Offset;
                    visual.Opacity = 0;
                    visual.Offset = new Vector3(start.X, start.Y + 8, start.Z);
                    var fade = _compositor.CreateScalarKeyFrameAnimation();
                    fade.Duration = TimeSpan.FromMilliseconds(150);
                    fade.InsertKeyFrame(1f, 1f, _decelerate);
                    visual.StartAnimation("Opacity", fade);
                    var slide = _compositor.CreateVector3KeyFrameAnimation();
                    slide.Duration = TimeSpan.FromMilliseconds(150);
                    slide.InsertKeyFrame(1f, start, _decelerate);
                    visual.StartAnimation("Offset", slide);
                }
                else
                {
                    var now = chip.TransformToVisual(panel)
                        .TransformPoint(new Windows.Foundation.Point(0, 0));
                    var old = before[id];
                    var dx = (float)(old.X - now.X);
                    var dy = (float)(old.Y - now.Y);
                    if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;
                    var baseOff = visual.Offset;
                    visual.Offset = new Vector3(baseOff.X + dx, baseOff.Y + dy, baseOff.Z);
                    var glide = _compositor.CreateVector3KeyFrameAnimation();
                    glide.Duration = TimeSpan.FromMilliseconds(150);
                    glide.InsertKeyFrame(1f, baseOff, _decelerate);
                    visual.StartAnimation("Offset", glide);
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
            // The "+" lives in XAML beside the panel (centered on the whole
            // chip row, not its top row); only its Tag/Name are refreshed
            // per prepare in OnLineContainerChanging.
            // Inline "Insert word here..." box (original insertWordField):
            // open at the row end while adding; Enter commits a new word
            // (default 0/0 timing) and keeps focus for repeat adds.
            if (_insertLineId == line.Id)
            {
                var insert = new TextBox
                {
                    PlaceholderText = "Insert word here...",
                    AcceptsReturn = false,
                    IsSpellCheckEnabled = false,
                    IsTextPredictionEnabled = false,
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
            // Per-word romaji visibility is owned SOLELY by the master toggle:
            // freshly built chips default to visible romans, so snap them here
            // (the "+" rebuild must never force them on while the toggle is
            // off). Same snap as container prepare, no animation.
            if (FindCardOfPanel(panel) is Grid builtCard)
                ApplyCardVisibility(builtCard,
                    _viewState?.ShowTranslation ?? true,
                    _viewState?.ShowRomanization ?? true,
                    _viewState?.ShowWordRomanization ?? true,
                    animate: false, collapseNow: true);
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

        // Selection-driven insert lifetime: leaving the insert's line closes
        // the box (draft dropped, like Esc). Same-line reselects (the commit
        // path selects the new word on the same line) never close it.
        private void CloseInsertOnSelectionMove()
        {
            if (_insertLineId is null || _store is null || _selection is null) return;
            if (_selection.SelectedLineId == _insertLineId) return;
            var lineId = _insertLineId;
            CloseInsertBox();
            var at = _store.Document.Lines.ToList().FindIndex(l => l.Id == lineId);
            if (at >= 0) RebuildLinePanel(at);
        }

        // Insert-box probe: "open:0/1;children:N;chips:G" (children = chips +
        // the box; exactly one non-chip child while open).
        internal string Test_InsertInfo()
        {
            if (_insertBox is null) return "open:0";
            var panel = VisualTreeHelper.GetParent(_insertBox) as LyricWrapPanel;
            if (panel is null) return "open:1;detached";
            var grids = panel.Children.OfType<Grid>().Count();
            return $"open:1;children:{panel.Children.Count};chips:{grids}";
        }

        internal bool CommitInsertBox()
        {
            if (_store is null || _insertBox is null) return false;
            var lineId = _insertLineId;
            var line = lineId is null ? null
                : _store.Document.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) return false;
            var raw = _insertBox.Text;
            var at = _store.Document.Lines.IndexOf(line);
            // An empty insert box closes instead of adding an empty word.
            if (raw.Length == 0)
            {
                CloseInsertBox();
                if (at >= 0) RebuildLinePanel(at);
                return true;
            }
            // Trailing "|" seeds ruby (model: empty == absent, so strip).
            var text = raw.EndsWith("|") ? raw[..^1] : raw;
            var word = EditWord.New();
            word.Text = text;
            _store.Execute(new AddWordCommand(line.Id, line.Words.Count, word));
            // Service-only selection (SelectionMode=None): the new word is
            // selected, no ListView mirror exists.
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
                // Enter commits (empty closes); the box stays open with focus
                // for repeat adds, exactly like the original insert field.
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
            // Never leak a drag session (timer/visuals) across navigation.
            try { CleanupDrag(); } catch { }
            DisarmDrag();
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
            if (_selection is not null)
            {
                _selection.Changed -= ApplySelectionVisuals;
                _selection.Changed -= CloseInsertOnSelectionMove;
                _selection = null;
            }
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
            var line = _store.Document.Lines[index];
            // Single source of truth is the SelectionService (the ListView
            // runs SelectionMode=None): PinLine converges exactly, keeping
            // the line's own surviving word selection per the B5 rule.
            _selection?.PinLine(line.Id, LineOfWord);
        }

        internal int SelectedLineIndex()
        {
            if (_store is null || _selection?.SelectedLineId is not string id) return -1;
            return _store.Document.Lines.ToList().FindIndex(l => l.Id == id);
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
            // A space label cannot take focus: swap in its editor first so
            // keyboard users can always reach every chip.
            if (box.Visibility == Visibility.Collapsed
                && _store is not null && line >= 0 && line < _store.Document.Lines.Count
                && word >= 0 && word < _store.Document.Lines[line].Words.Count)
                EnterSpaceEdit(_store.Document.Lines[line].Words[word].Id);
            return box.Focus(FocusState.Programmatic);
        }

        internal string ChipBoxText(int line, int word, bool roman) =>
            FindChipBox(line, word, roman)?.Text ?? "nobox";

        // Edit-box width probe (space chips must be wide enough to SEE the
        // caret while editing raw whitespace): "visible:1;width:NN.NN".
        internal string ChipBoxWidth(int line, int word, bool roman)
        {
            var box = FindChipBox(line, word, roman);
            if (box is null) return "nobox";
            return $"visible:{(box.Visibility == Visibility.Visible ? 1 : 0)};width:{box.ActualWidth.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}";
        }

        internal bool SetChipBoxText(int line, int word, bool roman, string text)
        {
            if (FindChipBox(line, word, roman) is not TextBox box) return false;
            try { box.Text = text; }
            catch { return false; }
            return true;
        }

        // Appends one char on the UI thread and returns the cost in ms
        // (typing-latency probe: no model touch, layout only). Layout is
        // panel-level: a list-wide UpdateLayout per keystroke costs ~10ms+
        // on a large doc for no benefit (only the chip row reflows).
        private double _lastSetMs;
        private double _lastLayoutMs;
        private double _lastSetCpuMs;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetThreadTimes(IntPtr hThread,
            out System.Runtime.InteropServices.ComTypes.FILETIME creation,
            out System.Runtime.InteropServices.ComTypes.FILETIME exit,
            out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
            out System.Runtime.InteropServices.ComTypes.FILETIME user);

        private static double CurrentThreadCpuMs()
        {
            try
            {
                if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var k, out var u))
                    return -1;
                static long ToLong(System.Runtime.InteropServices.ComTypes.FILETIME f) =>
                    ((long)(uint)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime;
                return (ToLong(k) + ToLong(u)) / 10000.0;
            }
            catch { return -1; }
        }

        internal double TypeChipChar(int line, int word, bool roman, char ch)
        {
            var box = FindChipBox(line, word, roman);
            if (box is null) return -1;
            var cpu0 = CurrentThreadCpuMs();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // One set per keystroke: appending to Text only. (A caret move
            // here would pump bring-into-view requests up the tree and
            // pollute the measurement; real keystrokes move it themselves.)
            box.Text += ch;
            _lastSetMs = sw.Elapsed.TotalMilliseconds;
            _lastSetCpuMs = cpu0 < 0 ? -1 : CurrentThreadCpuMs() - cpu0;
            var parent = VisualTreeHelper.GetParent(box);
            while (parent is not null && parent is not LyricWrapPanel)
                parent = VisualTreeHelper.GetParent(parent);
            if (parent is FrameworkElement panel) panel.UpdateLayout();
            else LineList.UpdateLayout();
            _lastLayoutMs = sw.Elapsed.TotalMilliseconds - _lastSetMs;
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }

        // Measurement hooks format with the invariant culture: the harness
        // parses with InvariantCulture, and the OS locale may use ','.
        internal static string F2(double v) =>
            v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        internal string TypeBreakdown() => $"{F2(_lastSetMs)},{F2(_lastLayoutMs)},{F2(_lastSetCpuMs)}";

        // Returns true when a history step resulted (false = box missing
        // or nothing to commit). Lets the harness tell silent no-ops apart
        // from real commits.
        internal bool CommitChip(int line, int word, bool roman)
        {
            if (FindChipBox(line, word, roman) is not TextBox box) return false;
            var before = _store?.Document.UndoDepth ?? -1;
            CommitBoxIfDirty(box);
            // A no-op commit leaves focus (and EDIT) in place; refreshes
            // already re-sync the label after real commits.
            if (_store is not null && line >= 0 && line < _store.Document.Lines.Count
                && word >= 0 && word < _store.Document.Lines[line].Words.Count)
                SyncSpaceVisual(_store.Document.Lines[line].Words[word].Id);
            var after = _store?.Document.UndoDepth ?? -1;
            return after != before;
        }

        internal void EscapeChip(int line, int word, bool roman)
        {
            var box = FindChipBox(line, word, roman);
            if (box is null || !_sessions.TryGetValue(box, out var session)) return;
            try
            {
                var model = session.GetModel();
                SetTextPreservingCaret(box, model);
                session.LastSeenModel = model;
            }
            catch { }
            if (_store is not null && line >= 0 && line < _store.Document.Lines.Count
                && word >= 0 && word < _store.Document.Lines[line].Words.Count)
                SyncSpaceVisual(_store.Document.Lines[line].Words[word].Id);
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

        // True when the FOCUSED box holds text differing from the model
        // (Step-4 Ctrl+Z routing: the TextBox then owns the keystroke).
        internal bool IsFocusedBoxDirty()
        {
            if (FocusManager.GetFocusedElement(XamlRoot) is not TextBox box) return false;
            if (!_sessions.TryGetValue(box, out var session)) return false;
            if (session.Composing) return true;
            try { return box.Text != session.GetModel(); }
            catch { return false; }
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
            try
            {
                var model = session.GetModel();
                SetTextPreservingCaret(box, model);
                session.LastSeenModel = model;
            }
            catch { }
        }

        internal string ChipWidths(int line, int word)
        {
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return "nopanel";
            if (word < 0 || word >= panel.Children.Count) return "norow";
            if (panel.Children[word] is not Grid chip || chip.Children.Count < 2) return "nochip";
            // A collapsed main box (space label showing) measures 0: use the
            // visible label instead, which is what defines the chip width.
            FrameworkElement? main = chip.Children[0] as FrameworkElement;
            if (main is not null && main.Visibility == Visibility.Collapsed
                && chip.Children.Count >= 3 && chip.Children[2] is FrameworkElement labelEl)
                main = labelEl;
            var tw = main?.ActualWidth ?? double.NaN;
            var rw = (chip.Children[1] as FrameworkElement)?.ActualWidth ?? double.NaN;
            return $"{F2(tw)},{F2(rw)}";
        }

        // Space-label text for a chip ("Space xN" when the label shows,
        // "hidden" while its editor is open, "notspace" for normal words).
        internal string SpaceLabel(int line, int word)
        {
            if (_store is null || line < 0 || line >= _store.Document.Lines.Count) return "noline";
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return "nopanel";
            var docLine = _store.Document.Lines[line];
            if (word < 0 || word >= docLine.Words.Count) return "noword";
            if (!IsSpaceWord(docLine.Words[word])) return "notspace";
            var wordId = docLine.Words[word].Id;
            foreach (var child in panel.Children)
            {
                if (child is Grid chip && chip.Tag is string tag && tag == wordId
                    && chip.Children.Count >= 3 && chip.Children[2] is TextBlock label)
                    return label.Visibility == Visibility.Visible ? label.Text : "hidden";
            }
            return "nolabel";
        }

        // Full chip-unit width (hug check: must be small for one glyph).
        internal string ChipUnitWidth(int line, int word)
        {
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return "nopanel";
            if (word < 0 || word >= panel.Children.Count) return "norow";
            if (panel.Children[word] is not Grid chip) return "nochip";
            card.UpdateLayout();
            return $"{F2(chip.ActualWidth)}";
        }

        // Card top pad (card top -> chip row top) vs bottom pad (sub-rows
        // bottom -> card bottom). Equal within 1px, romaji on and off.
        internal string CardPads(int line)
        {
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return "nopanel";
            if (card.FindName("SubRows") is not Grid subRows) return "nosubrows";
            card.UpdateLayout();
            var panelTop = panel.TransformToVisual(card)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            var subBottom = subRows.TransformToVisual(card)
                .TransformPoint(new Windows.Foundation.Point(0, subRows.ActualHeight)).Y;
            return $"{F2(panelTop)},{F2(card.ActualHeight - subBottom)}";
        }

        // Realized indices whose overlay accent bar is showing.
        internal string IndicatedIndices()
        {
            if (_store is null) return "nostore";
            var shown = new List<int>();
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                var (_, pill, _) = FindCardChrome(container);
                if (pill is not null && pill.Visibility == Visibility.Visible) shown.Add(i);
            }
            return string.Join(",", shown);
        }

        // --- Drag hooks (same execution path as pointer-up) -----------------
        internal bool Test_DragBeginLines(int[] indices)
        {
            if (_store is null || _selection is null) return false;
            SelectLines(indices);
            var ids = _store.Document.Lines
                .Where(l => _selection.SelectedLineIds.Contains(l.Id))
                .Select(l => l.Id).ToList();
            if (ids.Count == 0) return false;
            DisarmDrag();
            _drag = new DragSession { IsWords = false, Started = true };
            _drag.Ids.AddRange(ids);
            EnsureDragTimer();
            _dragTimer?.Start();
            if (indices.Length > 0
                && LineList.ContainerFromIndex(indices[0]) is ListViewItem container)
                container.Focus(FocusState.Programmatic);
            RefreshDragVisuals();
            return true;
        }

        internal bool Test_DragBeginWords(int line, int[] words)
        {
            if (_store is null || _selection is null) return false;
            if (line < 0 || line >= _store.Document.Lines.Count) return false;
            var docLine = _store.Document.Lines[line];
            var ids = words.Where(w => w >= 0 && w < docLine.Words.Count)
                .Select(w => docLine.Words[w].Id).ToList();
            if (ids.Count == 0) return false;
            // Exact-set: clear first (SelectWord early-returns on a reselect
            // of the same word, which would leave leftovers for the toggles
            // below to remove instead of add).
            ClearEditSelection();
            SelectWord(line, words[0]);
            foreach (var w in words.Skip(1)) ToggleChipWord(line, w);
            var wanted = new HashSet<string>(ids);
            var ordered = new List<string>();
            foreach (var l in _store.Document.Lines)
                foreach (var w in l.Words)
                    if (wanted.Contains(w.Id)) ordered.Add(w.Id);
            DisarmDrag();
            _drag = new DragSession { IsWords = true, Started = true };
            _drag.Ids.AddRange(ordered);
            EnsureDragTimer();
            _dragTimer?.Start();
            if (LineList.ContainerFromIndex(line) is ListViewItem container)
                container.Focus(FocusState.Programmatic);
            RefreshDragVisuals();
            return true;
        }

        internal string Test_DragToLineIndex(int index)
        {
            if (_drag is null || _drag.IsWords || _store is null) return "nodrag";
            index = Math.Max(0, Math.Min(index, _store.Document.Lines.Count));
            _drag.DropLineIndex = index;
            double y;
            if (index < _store.Document.Lines.Count
                && LineList.ContainerFromIndex(index) is ListViewItem container)
            {
                y = container.TransformToVisual(LineList)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y - 4;
            }
            else
            {
                y = 0;
                for (var i = _store.Document.Lines.Count - 1; i >= 0; i--)
                {
                    if (LineList.ContainerFromIndex(i) is ListViewItem last)
                    {
                        y = last.TransformToVisual(LineList)
                            .TransformPoint(new Windows.Foundation.Point(0, last.ActualHeight)).Y + 4;
                        break;
                    }
                }
            }
            _drag.LastX = LineList.ActualWidth / 2;
            _drag.LastY = y;
            PositionDropLine(y);
            RefreshDragVisuals();
            return $"target:{index}";
        }

        internal string Test_DragToWord(int line, int word, bool after)
        {
            if (_drag is null || !_drag.IsWords || _store is null) return "nodrag";
            if (line < 0 || line >= _store.Document.Lines.Count) return "noline";
            var docLine = _store.Document.Lines[line];
            string? wordId = word < 0 ? null
                : word < docLine.Words.Count ? docLine.Words[word].Id : null;
            if (word >= 0 && wordId is null) return "noword";
            _drag.DropLineId = docLine.Id;
            _drag.DropWordId = wordId;
            _drag.DropAfter = after;
            if (LineList.ContainerFromIndex(line) is ListViewItem container
                && container.ContentTemplateRoot is Grid card
                && card.FindName("ChipPanel") is LyricWrapPanel panel)
            {
                Grid? target = null;
                if (wordId is not null)
                {
                    foreach (var child in panel.Children)
                        if (child is Grid chip && chip.Tag is string tag && tag == wordId)
                        { target = chip; break; }
                }
                else
                {
                    target = panel.Children.OfType<Grid>().LastOrDefault();
                    after = true;
                }
                if (target is not null)
                {
                    ShowChipMark(target, after);
                    var c = target.TransformToVisual(LineList)
                        .TransformPoint(new Windows.Foundation.Point(
                            after ? target.ActualWidth : 0, target.ActualHeight / 2));
                    _drag.LastX = c.X;
                    _drag.LastY = c.Y;
                }
            }
            RefreshDragVisuals();
            return $"target:{line}:{(wordId ?? "end")}:{(after ? "after" : "before")}";
        }

        internal string Test_DragEnd(bool copy) => DropExecute(copy);

        internal string Test_DragCancel()
        {
            if (_drag is null) return "idle";
            CancelDrag();
            return "cancelled";
        }

        internal string Test_DragState()
        {
            if (_drag is null) return "idle";
            var ghost = GhostText(IsCtrlDown());
            return _drag.IsWords
                ? $"words:{_drag.Ids.Count}>{_drag.DropLineId}:{_drag.DropWordId} ghost:[{ghost}]"
                : $"lines:{_drag.Ids.Count}>{_drag.DropLineIndex} ghost:[{ghost}]";
        }

        // The PAINTED ghost label (what the overlay shows right now — driven
        // by the 16ms Ctrl poll, not by pointer moves). Host test: start a
        // drag, hold still, SendInput Ctrl down/up, read this within 50ms.
        internal string Test_DragGhost() =>
            _drag is null ? "nodrag" : DragGhostText.Text;

        // Ctrl-latency budget probe: the poll timer interval + whether it is
        // running while a drag is active (the poll — not pointer moves —
        // flips Move<->Copy, so a still mouse still meets the 50ms budget).
        internal string Test_DragTimerInfo()
        {
            if (_dragTimer is null) return "notimer";
            var ms = _dragTimer.Interval.TotalMilliseconds;
            var running = _drag is not null && _drag.Started && _dragTimer.IsEnabled;
            return $"interval:{ms.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)};running:{(running ? 1 : 0)}";
        }

        internal void Test_BuildDragDoc(int lines, int wordsPer)
        {
            if (_store is null) return;
            var built = new List<EditLine>();
            for (var li = 0; li < lines; li++)
            {
                var line = EditLine.New();
                line.StartTimeMs = li * 2000;
                line.EndTimeMs = li * 2000 + 1800;
                for (var wi = 0; wi < wordsPer; wi++)
                {
                    var word = EditWord.New();
                    word.StartTimeMs = li * 2000 + wi * 100;
                    word.EndTimeMs = li * 2000 + wi * 100 + 80;
                    word.Text = $"w{li}_{wi}";
                    line.Words.Add(word);
                }
                built.Add(line);
            }
            _store.Execute(new BulkReplaceCommand(built, new List<EditMetadata>()));
        }

        // Auto-scroll probe: parks the session pointer at the requested edge
        // and runs N scroll ticks synchronously (same ScrollTick the timer
        // calls). Returns "off0->off1 ticks:n wallMs:m".
        internal string Test_DragEdgeScroll(bool top, int ticks)
        {
            if (_drag is null) return "nodrag";
            _drag.ScrollDir = top ? -1 : 1;
            _drag.LastX = LineList.ActualWidth / 2;
            _drag.LastY = top ? 5 : Math.Max(6, LineList.ActualHeight - 5);
            var sv = FindScroller();
            var off0 = sv?.VerticalOffset ?? -1;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < ticks; i++) ScrollTick();
            sw.Stop();
            var off1 = sv?.VerticalOffset ?? -1;
            return $"{F2(off0)}->{F2(off1)} ticks:{_drag.ScrollTicks.Count} wallMs:{sw.Elapsed.TotalMilliseconds:F0}";
        }

        // Whole-doc fingerprint in ONE call (same exact format as the
        // EditCheck helper's per-hook version, but ~500x fewer dispatcher
        // round-trips on big docs: the per-op stress fingerprints were
        // saturating the UI thread at ~16s/iter on 74-line docs).
        internal string Test_DocFingerprint()
        {
            if (_store is null) return "nostore";
            var parts = new List<string>();
            foreach (var line in _store.Document.Lines)
            {
                parts.Add($"L[{line.StartTimeMs},{line.EndTimeMs},{line.IsBg},{line.IsDuet},{line.IgnoreSync},{line.TranslatedLyric},{line.RomanLyric}]");
                foreach (var w in line.Words)
                    parts.Add($"W[{w.StartTimeMs},{w.EndTimeMs},{w.Text},{w.RomanWord},{w.Obscene},{w.EmptyBeat}]");
                parts.Add("/");
            }
            return string.Join("|", parts);
        }

        // Whole-doc shape in ONE call (cheap fingerprint for drag stress):
        // "lineId,start,end,bg,duet:wordId,start,end;..." per line.
        internal string Test_DocShape()
        {
            if (_store is null) return "nostore";
            var parts = new List<string>();
            foreach (var line in _store.Document.Lines)
            {
                var words = string.Join(";",
                    line.Words.Select(w => $"{w.Id},{w.StartTimeMs},{w.EndTimeMs}"));
                parts.Add($"{line.Id},{line.StartTimeMs},{line.EndTimeMs}," +
                    $"{(line.IsBg ? 1 : 0)},{(line.IsDuet ? 1 : 0)}:{words}");
            }
            return string.Join("|", parts);
        }

        // Service-side line selection as doc indices (what multi-ops read).
        internal string Test_ServiceLineIdx()
        {
            if (_store is null || _selection is null) return "nostore";
            var lines = _store.Document.Lines.ToList();
            var idx = lines
                .Select((l, i) => (l, i))
                .Where(t => _selection.SelectedLineIds.Contains(t.l.Id))
                .Select(t => t.i).OrderBy(i => i);
            return string.Join(",", idx);
        }

        // Selected indices from the ONE source of truth (the
        // SelectionService). The ListView runs SelectionMode=None, so it
        // holds no selection of its own; IndicatedIndices() (visual bars)
        // must equal this exactly — asserted by the harness every step.
        internal string SelectedIndices()
        {
            return Test_ServiceLineIdx();
        }

        // X of the first word chip relative to its card (identical on every
        // line: fixed number column + overlay indicator take no layout space,
        // badges live on the right).
        internal string FirstChipX(int line)
        {
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return "nopanel";
            foreach (var child in panel.Children)
            {
                if (child is Grid chip)
                {
                    card.UpdateLayout();
                    return F2(chip.TransformToVisual(card)
                        .TransformPoint(new Windows.Foundation.Point(0, 0)).X);
                }
            }
            return "nochip";
        }

        // Full container height (selection/badges must not change it).
        internal string CardHeight(int line)
        {
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            container.UpdateLayout();
            return F2(container.ActualHeight);
        }

        // Min,max,count of gaps between ADJACENT realized index pairs
        // (identical everywhere: "min,max,n" with max-min <= 1). Adjacent
        // indices (not sorted-Y) so recycled/stale containers can't pair up.
        internal string CardGaps()
        {
            if (_store is null) return "nostore";
            LineList.UpdateLayout();
            var min = double.MaxValue;
            var max = double.MinValue;
            var n = 0;
            for (var i = 0; i + 1 < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem first) continue;
                if (LineList.ContainerFromIndex(i + 1) is not ListViewItem second) continue;
                if (ReferenceEquals(first, second)) continue;
                var top = first.TransformToVisual(LineList)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                var nextTop = second.TransformToVisual(LineList)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                var gap = nextTop - (top + first.ActualHeight);
                // A stale (pre-layout) container reports a garbage position;
                // only physically plausible gaps count (|gap| <= height).
                if (Math.Abs(gap) > first.ActualHeight) continue;
                if (gap < min) min = gap;
                if (gap > max) max = gap;
                n++;
            }
            if (n == 0) return "toofew";
            return $"{F2(min)},{F2(max)},{n}";
        }

        // Vertical distance between the "+" center and the chip-row center
        // (must be ~0: centered on the whole unit, not its top row).
        internal string AddButtonDy(int line)
        {
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return "nopanel";
            if (card.FindName("AddWordButton") is not Button add) return "noadd";
            card.UpdateLayout();
            var panelCenter = panel.TransformToVisual(card)
                .TransformPoint(new Windows.Foundation.Point(0, panel.ActualHeight / 2)).Y;
            var addCenter = add.TransformToVisual(card)
                .TransformPoint(new Windows.Foundation.Point(0, add.ActualHeight / 2)).Y;
            return $"{F2(Math.Abs(addCenter - panelCenter))}";
        }

        // Gap between a chip's main box (or its space label) and its romaji
        // box, in px. "collapsed" when the romaji row is toggled off.
        internal string ChipGap(int line, int word)
        {
            if (_store is null || line < 0 || line >= _store.Document.Lines.Count) return "noline";
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return "nopanel";
            var docLine = _store.Document.Lines[line];
            if (word < 0 || word >= docLine.Words.Count) return "noword";
            var wordId = docLine.Words[word].Id;
            foreach (var child in panel.Children)
            {
                if (child is not Grid chip || chip.Tag is not string tag || tag != wordId) continue;
                if (chip.Children.Count < 2 || chip.Children[1] is not FrameworkElement roman) return "noroman";
                if (roman.Visibility == Visibility.Collapsed) return "collapsed";
                card.UpdateLayout();
                FrameworkElement? main = chip.Children[0] as FrameworkElement;
                if (main is not null && main.Visibility == Visibility.Collapsed
                    && chip.Children.Count >= 3 && chip.Children[2] is FrameworkElement labelEl)
                    main = labelEl;
                if (main is null) return "nomain";
                var mainBottom = main.TransformToVisual(card)
                    .TransformPoint(new Windows.Foundation.Point(0, main.ActualHeight)).Y;
                var romanTop = roman.TransformToVisual(card)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                return $"{F2(romanTop - mainBottom)}";
            }
            return "nochip";
        }

        // Gap between the card Translation and Romanization rows, in px.
        // "collapsed" when either row is toggled off.
        internal string RowGap(int line)
        {
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            if (card.FindName("TransBox") is not FrameworkElement trans) return "notrans";
            if (card.FindName("RomanBox") is not FrameworkElement roman) return "noroman";
            if (trans.Visibility == Visibility.Collapsed || roman.Visibility == Visibility.Collapsed)
                return "collapsed";
            card.UpdateLayout();
            var transBottom = trans.TransformToVisual(card)
                .TransformPoint(new Windows.Foundation.Point(0, trans.ActualHeight)).Y;
            var romanTop = roman.TransformToVisual(card)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            return $"{F2(romanTop - transBottom)}";
        }

        // Headless-safe screenshots (RenderTargetBitmap renders the XAML
        // tree in software: no foreground, no compositor needed, unlike UIA
        // capture). Used for before/after visual verification each step.
        internal async Task<string> ShotCardAsync(int line, string path)
        {
            try
            {
                if (LineList.ContainerFromIndex(line) is not ListViewItem container)
                    return "unrealized";
                // Render the card grid itself: the container chrome (Border)
                // captures without its data-template content under RTB.
                if (container.ContentTemplateRoot is Grid card)
                    return await ShotElementAsync(card, path);
                return await ShotElementAsync(container, path);
            }
            catch (Exception ex) { return "error:" + ex.Message; }
        }

        internal async Task<string> ShotChipAsync(int line, int word, string path)
        {
            try
            {
                if (_store is null) return "noline";
                if (LineList.ContainerFromIndex(line) is not ListViewItem container)
                    return "unrealized";
                if (container.ContentTemplateRoot is not Grid card) return "nocard";
                if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return "nopanel";
                var docLine = _store.Document.Lines[line];
                if (word < 0 || word >= docLine.Words.Count) return "noword";
                var wordId = docLine.Words[word].Id;
                foreach (var child in panel.Children)
                {
                    if (child is Grid chip && chip.Tag is string tag && tag == wordId)
                        return await ShotElementAsync(chip, path);
                }
                return "nochip";
            }
            catch (Exception ex) { return "error:" + ex.Message; }
        }

        internal async Task<string> ShotListAsync(string path)
        {
            try { return await ShotElementAsync(LineList, path); }
            catch (Exception ex) { return "error:" + ex.Message; }
        }

        internal async Task<string> ShotPageAsync(string path)
        {
            try { return await ShotElementAsync(this, path); }
            catch (Exception ex) { return "error:" + ex.Message; }
        }

        private static async Task<string> ShotElementAsync(FrameworkElement el, string path)
        {
            el.UpdateLayout();
            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(el);
            var pixels = await rtb.GetPixelsAsync();
            var sb = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
                pixels,
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                rtb.PixelWidth, rtb.PixelHeight,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            using var mem = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, mem);
            enc.SetSoftwareBitmap(sb);
            await enc.FlushAsync();
            mem.Seek(0);
            using var fs = System.IO.File.Create(path);
            var reader = new Windows.Storage.Streams.DataReader(mem.GetInputStreamAt(0));
            var bytes = new byte[mem.Size];
            await reader.LoadAsync((uint)mem.Size);
            reader.ReadBytes(bytes);
            await fs.WriteAsync(bytes, 0, bytes.Length);
            return $"ok:{rtb.PixelWidth}x{rtb.PixelHeight}";
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
            var stamps = await SampleFramesAsync(() => FlipToggle(which), durationMs);
            return FormatFrameStats(stamps);
        }

        // Frame stats around a structural op (same animation path as
        // undo/redo of that op). Ops run through the real EditPage paths.
        internal async Task<string> SampleStructuralFramesAsync(string op, int durationMs)
        {
            var stamps = await SampleFramesAsync(() => RunStructuralOp(op), durationMs);
            return FormatFrameStats(stamps);
        }

        private void RunStructuralOp(string op)
        {
            if (_store is null) return;
            switch (op)
            {
                case "addline":
                    InsertLineAfter(Math.Min(2, _store.Document.Lines.Count - 1));
                    break;
                case "deleteline":
                    if (_store.Document.Lines.Count > 3)
                        DeleteLineAt(_store.Document.Lines.Count - 1);
                    break;
                case "addword":
                    if (_store.Document.Lines.Count > 0)
                        AddWordToLine(_store.Document.Lines[0].Id, "x");
                    break;
                case "removeword":
                    if (_store.Document.Lines.Count > 0
                        && _store.Document.Lines[0].Words.Count > 1)
                    {
                        var line = _store.Document.Lines[0];
                        _store.Execute(new RemoveWordsCommand(line.Id,
                            new[] { line.Words[^1].Id }));
                    }
                    break;
                default: throw new ArgumentException($"Unknown op: {op}");
            }
        }

        private static async Task<List<TimeSpan>> SampleFramesAsync(Action op, int durationMs)
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
                op();
                await Task.Delay(durationMs);
            }
            finally
            {
                CompositionTarget.Rendering -= Handler;
            }
            return stamps;
        }

        private static string FormatFrameStats(List<TimeSpan> stamps)
        {
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
            return $"frames={stamps.Count} median={F2(median)} p95={F2(p95)} max={F2(max)} over13_9={over13_9} over2xmedian={over2xmedian}";
        }

        private void OnStoreChanged()
        {
            if (_store is not null && _insertLineId is not null
                && _store.Document.Lines.All(l => l.Id != _insertLineId))
            {
                // Its line is gone (delete/undo/combine): drop the dangling
                // insert box instead of pointing at a dead line.
                CloseInsertBox();
            }
            if (DispatcherQueue.HasThreadAccess) { RefreshEmptyState(); RefreshBoxTexts(); RefreshFlagVisuals(); RefreshRealizedNumbers(); }
            else DispatcherQueue.TryEnqueue(() => { RefreshEmptyState(); RefreshBoxTexts(); RefreshFlagVisuals(); RefreshRealizedNumbers(); });
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
                    SyncBox(trans, line.TranslatedLyric);
                if (card.FindName("RomanBox") is TextBox roman)
                    SyncBox(roman, line.RomanLyric);
                if (card.FindName("ChipPanel") is not LyricWrapPanel panel) continue;
                foreach (var child in panel.Children)
                {
                    if (child is not Grid chip || chip.Tag is not string wordId) continue;
                    if (chip.Children.Count < 2) continue;
                    var word = line.Words.FirstOrDefault(w => w.Id == wordId);
                    if (word is null) continue;
                    if (chip.Children[0] is TextBox text)
                        SyncBox(text, word.Text);
                    if (chip.Children[1] is TextBox rom)
                        SyncBox(rom, word.RomanWord);
                    // Space chips keep the dim "Space xN" label in sync with
                    // the model count (typing spaces changes N live); the
                    // dim cue is the secondary-brush label itself, so the
                    // chip stays full-opacity and legible.
                    var space = IsSpaceWord(word);
                    chip.Opacity = 1;
                    if (chip.Children.Count >= 3 && chip.Children[2] is TextBlock label && space)
                    {
                        label.Text = SpaceLabelFor(word);
                        AutomationProperties.SetName(label, $"Space \u00D7{word.Text.Length}");
                    }
                    SyncSpaceVisual(wordId);
                }
            }
        }

        // Model→box sync that also re-baselines the session snapshot, so a
        // later commit test sees only user-typed divergence. (Deliberately
        // overwrites uncommitted typing, like the original's editing-state
        // reset; caret/focus are preserved.)
        private void SyncBox(TextBox box, string model)
        {
            SetTextPreservingCaret(box, model);
            if (_sessions.TryGetValue(box, out var session))
                session.LastSeenModel = model;
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

        // Empty card area selects the line. The ListView runs
        // SelectionMode=None, so ALL selection state lives in the
        // SelectionService: plain press selects, modified presses arm for
        // release/dragstart (multi-selections survive until click completes).
        // Modifiers come from the pointer event itself (KeyModifiers), with
        // the thread key state as fallback.
        private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Grid card || _selection is null || _store is null) return;
            if (IsInCardControl(card, e.OriginalSource as DependencyObject)) return;
            // Right/middle mouse: the line context menu owns it (see chip
            // handler). Touch/pen tip presses arm like left-clicks.
            var props = e.GetCurrentPoint(LineList).Properties;
            if (!props.IsLeftButtonPressed
                && e.Pointer.PointerDeviceType.ToString() == "Mouse")
                return;
            var container = FindListViewItem(card);
            if (container is null) return;
            var index = LineList.IndexFromContainer(container);
            if (index < 0 || index >= _store.Document.Lines.Count) return;
            var line = _store.Document.Lines[index];
            var (ctrl, shift) = PointerModifiers(e);
            // Plain press on an unselected line selects immediately;
            // modified presses and presses inside the selection defer to
            // release/dragstart (multi-selections survive).
            if (!ctrl && !shift && !_selection.SelectedLineIds.Contains(line.Id))
            {
                _selection.SelectLine(line.Id);
                container.Focus(FocusState.Programmatic);
            }
            ArmDrag(words: false, lineId: line.Id, wordId: null,
                point: e.GetCurrentPoint(LineList).Position, ctrl: ctrl, shift: shift);
            e.Handled = true;
        }

        // Modifiers from the pointer event args (reliable even when the
        // TextBox children swallowed key state); falls back to the thread
        // key state for synthesized/test paths without event modifiers.
        private static (bool Ctrl, bool Shift) PointerModifiers(PointerRoutedEventArgs e)
        {
            var mods = e.KeyModifiers;
            var ctrl = mods.HasFlag(Windows.System.VirtualKeyModifiers.Control)
                || IsCtrlDown();
            var shift = mods.HasFlag(Windows.System.VirtualKeyModifiers.Shift)
                || IsShiftDown();
            return (ctrl, shift);
        }



        // True when a press originates inside a chip, an edit box, a button
        // or the insert box (those own their presses); everything else on
        // the card (number, badges, chip gaps, trailing area) is line area.
        private static bool IsInCardControl(Grid card, DependencyObject? src)
        {
            var current = src;
            while (current is not null && !ReferenceEquals(current, card))
            {
                if (current is TextBox || current is Button) return true;
                if (current is Grid grid && grid.Tag is string) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        // Right-click / Shift+F10 / Menu key on an unselected line selects
        // it first (single, with the FULL selected look applied synchronously
        // before the menu opens); inside a multi-selection the set is kept.
        private void EnsureLineSelectedForContext(Grid card)
        {
            if (_store is null || _selection is null) return;
            var container = FindListViewItem(card);
            if (container is null) return;
            var index = LineList.IndexFromContainer(container);
            if (index < 0 || index >= _store.Document.Lines.Count) return;
            var line = _store.Document.Lines[index];
            if (!_selection.SelectedLineIds.Contains(line.Id))
            {
                _selection.SelectLine(line.Id);
                // Paint before the menu opens: the Opening handler builds the
                // menu right after this returns, so an async paint would show
                // only the accent pill on the first frame.
                ApplySelectionVisualsNow();
            }
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
            // The Opening handler selects the line first, so the count below
            // covers the clicked line; inside a multi-selection the menu
            // acts on all ("Delete 5 lines", original).
            var scope = selCount <= 1 ? 1 : selCount;
            var before = new MenuFlyoutItem { Text = "Insert line before" };
            before.Click += (_, _) => InsertLineBefore(index);
            var after = new MenuFlyoutItem { Text = "Insert line after" };
            after.Click += (_, _) => InsertLineAfter(index);
            var dup = new MenuFlyoutItem
            {
                Text = scope == 1 ? "Duplicate line" : $"Duplicate {scope} lines",
                KeyboardAcceleratorTextOverride = "Ctrl+D",
            };
            dup.Click += (_, _) => DuplicateSelectedLines();
            var combine = new MenuFlyoutItem
            {
                Text = selCount >= 2 ? $"Combine {selCount} lines" : "Combine lines",
            };
            combine.IsEnabled = selCount >= 2;
            combine.Click += (_, _) => CombineSelectedLines();
            var up = new MenuFlyoutItem
            {
                Text = scope == 1 ? "Move line up" : $"Move {scope} lines up",
                KeyboardAcceleratorTextOverride = "Alt+Up",
            };
            up.Click += (_, _) =>
            {
                if (_selection?.SelectedLineId is null && _store is not null)
                {
                    var l = _store.Document.Lines[index];
                    _selection?.SelectLine(l.Id);
                }
                MoveSelectedLines(-1);
            };
            var down = new MenuFlyoutItem
            {
                Text = scope == 1 ? "Move line down" : $"Move {scope} lines down",
                KeyboardAcceleratorTextOverride = "Alt+Down",
            };
            down.Click += (_, _) =>
            {
                if (_selection?.SelectedLineId is null && _store is not null)
                {
                    var l = _store.Document.Lines[index];
                    _selection?.SelectLine(l.Id);
                }
                MoveSelectedLines(1);
            };
            // Tri-state from the scope: all-set reads checked, else clear;
            // one click applies to every in-scope line (original, one step).
            // Duet state shown is the EFFECTIVE duet (agent vs primary).
            var scopeIds = ScopeLineIds(lineId);
            var bg = new ToggleMenuFlyoutItem { Text = "BG lyric" };
            var duet = new ToggleMenuFlyoutItem { Text = "Duet lyric" };
            if (_store is not null)
            {
                var primary = DuetDisplay.PrimaryAgent(_store.Document.Lines);
                var scopeLines = _store.Document.Lines
                    .Where(l => scopeIds.Contains(l.Id)).ToList();
                bg.IsChecked = scopeLines.Count > 0 && scopeLines.All(l => l.IsBg);
                duet.IsChecked = scopeLines.Count > 0
                    && scopeLines.All(l => DuetDisplay.EffectiveDuet(l, primary));
            }
            bg.Click += (_, _) => ToggleLineFlag(lineId, isBg: true, bg.IsChecked);
            duet.Click += (_, _) => ToggleLineFlag(lineId, isBg: false, duet.IsChecked);
            var del = new MenuFlyoutItem
            {
                Text = scope == 1 ? "Delete line" : $"Delete {scope} lines",
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

        // Menu scope: the selection when the clicked line is in it (the
        // Opening handler guarantees it for right-clicks), else the line.
        private List<string> ScopeLineIds(string lineId)
        {
            if (_selection is not null && _selection.SelectedLineIds.Contains(lineId)
                && _selection.SelectedLineIds.Count > 0)
                return _selection.SelectedLineIds.ToList();
            return new List<string> { lineId };
        }

        private void ToggleLineFlag(string lineId, bool isBg, bool value)
        {
            if (_store is null || _selection is null) return;
            CommitActiveBox();
            var ordered = _store.Document.Lines
                .Where(l => ScopeLineIds(lineId).Contains(l.Id))
                .Select(l => l.Id).ToList();
            if (ordered.Count == 0) return;
            ILyricCommand cmd = ordered.Count == 1
                ? new SetLineFlagsCommand(ordered[0],
                    isBg: isBg ? value : null,
                    isDuet: isBg ? null : value)
                : new CompositeCommand("Set line flags", ordered
                    .Select(id => (ILyricCommand)new SetLineFlagsCommand(id,
                        isBg: isBg ? value : null,
                        isDuet: isBg ? null : value)).ToList());
            _store.Execute(cmd);
        }

        // The menu sits on the chip AND both boxes: a TextBox shows its own
        // native menu on right-click and would otherwise swallow the word
        // menu (only the 6px chip gaps would open it). An explicit
        // ContextFlyout replaces the native one, including for Shift+F10.
        // One instance per element (never shared): reparenting a single
        // open flyout across elements is a needless crash risk.
        private void AttachWordMenu(Grid chip, TextBox text, TextBox roman, string lineId, string wordId)
        {
            chip.ContextFlyout = MakeWordMenuFlyout(lineId, wordId);
            text.ContextFlyout = MakeWordMenuFlyout(lineId, wordId);
            roman.ContextFlyout = MakeWordMenuFlyout(lineId, wordId);
        }

        private MenuFlyout MakeWordMenuFlyout(string lineId, string wordId)
        {
            var flyout = new MenuFlyout();
            flyout.Opening += (_, _) =>
            {
                // Right-click on an unselected word selects it (+ its line)
                // first; inside a multi-selection the set is kept (original).
                EnsureWordSelectedForContext(lineId, wordId);
                flyout.Items.Clear();
                foreach (var item in BuildWordMenuItems(lineId, wordId))
                    flyout.Items.Add(item);
            };
            return flyout;
        }

        private void EnsureWordSelectedForContext(string lineId, string wordId)
        {
            if (_store is null || _selection is null) return;
            var line = _store.Document.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null || !line.Words.Any(w => w.Id == wordId)) return;
            if (!_selection.SelectedWordIds.Contains(wordId))
            {
                _selection.SelectWord(lineId, wordId);
                ApplySelectionVisualsNow();
            }
        }

        private List<MenuFlyoutItemBase> BuildWordMenuItems(string lineId, string wordId)
        {
            var items = new List<MenuFlyoutItemBase>();
            if (_store is null) return items;
            var line = _store.Document.Lines.FirstOrDefault(l => l.Id == lineId);
            if (line is null) return items;
            var wordCount = _selection?.SelectedWordIds.Count ?? 0;
            var lineWordSel = line.Words.Count(w => _selection?.SelectedWordIds.Contains(w.Id) == true);
            var split = new MenuFlyoutItem { Text = "Split word..." };
            split.IsEnabled = wordCount == 1;
            split.Click += (_, _) => ShowSplitDialog(line.Id, wordId);
            var replace = new MenuFlyoutItem { Text = "Replace word..." };
            replace.IsEnabled = wordCount == 1;
            replace.Click += (_, _) => ShowReplaceDialog(line.Id, wordId);
            var merge = new MenuFlyoutItem { Text = "Merge words" };
            merge.IsEnabled = lineWordSel > 1;
            merge.Click += (_, _) => MergeSelectedWords(line.Id);
            var delWords = new MenuFlyoutItem
            {
                Text = wordCount == 1 ? "Delete selected word" : $"Delete {wordCount} selected words",
                KeyboardAcceleratorTextOverride = "Delete",
            };
            delWords.IsEnabled = wordCount > 0;
            delWords.Click += (_, _) => DeleteSelected();
            var toNew = new MenuFlyoutItem
            {
                Text = wordCount == 1 ? "Move selected word to new line" : $"Move {wordCount} selected words to new line",
            };
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
                _store.Document.Lines.IndexOf(line), line.Id))
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
            // Model text as of the last sync (wire/refresh/commit/revert).
            // A commit is legitimate only when the box text differs from
            // BOTH the live model and this snapshot: untouched pooled boxes
            // (text == snapshot, model moved on) must NOT commit their stale
            // text over the current model (observed: reverted word text +
            // stray history step desyncing later undos). Container-based
            // currency was tried and rejected: container.Content can lag the
            // model (observed: skipping a legitimate 5000-char commit).
            public string LastSeenModel = "";
            public Func<string> GetModel = () => "";
            public Action<string> Commit = _ => { };
            public bool Composing;
        }

        private readonly Dictionary<TextBox, BoxSession> _sessions = new();

        private void DropSessionOnly(TextBox box)
        {
            if (!_sessions.TryGetValue(box, out _)) return;
            box.KeyDown -= OnEditKeyDown;
            box.LostFocus -= OnEditLostFocus;
            box.GotFocus -= OnEditGotFocus;
            box.TextCompositionStarted -= OnCompositionStarted;
            box.TextCompositionEnded -= OnCompositionEnded;
            box.Unloaded -= OnBoxUnloaded;
            _sessions.Remove(box);
        }

        private void WireBox(TextBox box, Func<string> getModel, Action<string> commit)
        {
            string current;
            try { current = getModel(); }
            catch { current = box.Text; }
            if (_sessions.TryGetValue(box, out var existing))
            {
                existing.LastSeenModel = current;
                existing.GetModel = getModel;
                existing.Commit = commit;
                SetTextPreservingCaret(box, current);
                return;
            }
            var session = new BoxSession { LastSeenModel = current, GetModel = getModel, Commit = commit };
            _sessions[box] = session;
            SetTextPreservingCaret(box, current);
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
                DropSessionOnly(box);
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
            // Commit only genuine local divergence: text differing from BOTH
            // the live model and the last-synced snapshot. Untouched pooled
            // boxes (text == snapshot, model moved on) are stale — skip.
            if (current == model || current == session.LastSeenModel) return;
            // The target may be gone (line/word deleted while the box was
            // alive, e.g. recycle racing a structural undo): commands throw
            // InvalidOperationException for missing targets, and a commit
            // for a dead target is a no-op — never crash the UI thread.
            try
            {
                session.Commit(current);
                session.LastSeenModel = current;
            }
            catch (InvalidOperationException) { }
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

        internal void SelectWord(int line, int word)
        {
            if (_store is null || _selection is null) return;
            if (line < 0 || line >= _store.Document.Lines.Count) return;
            var docLine = _store.Document.Lines[line];
            if (word < 0 || word >= docLine.Words.Count) return;
            _selection.SelectWord(docLine.Id, docLine.Words[word].Id);
        }

        internal void SelectLines(IEnumerable<int> indices)
        {
            if (_store is null || _selection is null) return;
            var ids = indices
                .Where(i => i >= 0 && i < _store.Document.Lines.Count)
                .Select(i => _store.Document.Lines[i].Id).ToList();
            // Service-only (SelectionMode=None): no ListView event follows.
            _selection.SetLines(ids, LineOfWord);
        }

        internal void SelectAllLines()
        {
            if (_store is null || _selection is null) return;
            CommitActiveBox();
            _selection.SetLines(_store.Document.Lines.Select(l => l.Id), LineOfWord);
        }

        internal void ClearEditSelection()
        {
            _selection?.Clear();
        }

        internal bool ToggleChipWord(int line, int word)
        {
            if (_store is null || _selection is null) return false;
            if (line < 0 || line >= _store.Document.Lines.Count) return false;
            var docLine = _store.Document.Lines[line];
            if (word < 0 || word >= docLine.Words.Count) return false;
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
            // Same path as the Esc key: close + rebuild (drops the box).
            if (_insertBox is null) return false;
            var lineId = _insertLineId;
            CloseInsertBox();
            if (_store is not null && lineId is not null)
            {
                var idx = _store.Document.Lines.ToList().FindIndex(l => l.Id == lineId);
                if (idx >= 0) RebuildLinePanel(idx);
            }
            return true;
        }

        internal int LineIndexOfSelected() => SelectedLineIndex();

        internal bool IsLineRealized(int index) =>
            index >= 0 && index < LineList.Items.Count
            && LineList.ContainerFromIndex(index) is not null;

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
                // Enter returns to SELECT: focus back on the line container.
                FindListViewItem(box)?.Focus(FocusState.Programmatic);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                // Esc mid-drag cancels the drag instead of reverting text.
                if (_drag is not null && _drag.Started)
                {
                    CancelDrag();
                    e.Handled = true;
                    return;
                }
                try
                {
                    var model = session.GetModel();
                    SetTextPreservingCaret(box, model);
                    session.LastSeenModel = model;
                }
                catch { }
                // Esc returns to SELECT (reverted): focus back on the line.
                FindListViewItem(box)?.Focus(FocusState.Programmatic);
                e.Handled = true;
            }
        }

        private void OnEditLostFocus(object sender, RoutedEventArgs e)
        {
            // Tab/Shift+Tab commit on the way out through this same path.
            // Blur also returns space chips to their label (SELECT).
            if (sender is TextBox box)
            {
                CommitBoxIfDirty(box);
                if (box.Tag is string wordId && IsSpaceId(wordId))
                    SyncSpaceVisual(wordId);
            }
        }

        // GotFocus delivery counter (test hook: proves the event reached the
        // handler even when an inactive headless window routes focus oddly).
        private int _gotFocusCount;
        internal int Test_GotFocusCount() => _gotFocusCount;

        private void OnEditGotFocus(object sender, RoutedEventArgs e)
        {
            // Focus moves the primary (the ribbon Word group follows the
            // focused word) but NEVER the sets: clicks own the sets via
            // PointerPressed, so multi-selections survive tabbing, exactly
            // like the original (selection is click-driven). For pointer the
            // press already selected; the anchor is then a no-op.
            if (sender is not TextBox box || _selection is null || _store is null) return;
            _gotFocusCount++;
            // Stale-event guard: a queued GotFocus for a box that is no
            // longer focused (e.g. a programmatic Focus() whose event arrives
            // after a later click already moved focus on) must not clobber
            // the primary. Real tab/click focus always has sender == focused.
            if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), box)) return;
            // Resolve by Tag first (always accurate, no visual-tree
            // dependency); fall back to the container for line-field boxes.
            if (box.Tag is string wordId && LineOfWord(wordId) is string lineId)
            {
                var line = _store.Document.Lines.FirstOrDefault(l => l.Id == lineId);
                if (line is null) return;
                _selection.AnchorWord(lineId, wordId);
                // A space label can only be reached via its box: focusing it
                // swaps in the editor (double-click/F2/Tab all land here).
                if (IsSpaceId(wordId)) SyncSpaceVisual(wordId);
                LineList.ScrollIntoView(line);
                return;
            }
            var container = FindListViewItem(box);
            if (container is null) return;
            var index = LineList.IndexFromContainer(container);
            if (index < 0 || index >= _store.Document.Lines.Count) return;
            var fieldLine = _store.Document.Lines[index];
            _selection.AnchorLine(fieldLine.Id);
            LineList.ScrollIntoView(fieldLine);
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
            // Pooled containers keep their boxes (and live sessions) until
            // rebind, and the model can change while pooled (undo/redo/ops).
            // Committing the stale text AT REBIND would regress the model
            // (observed: word text reverted + stray history step). So commit
            // while current (now) and drop: rebind then starts clean. At
            // recycle time realized boxes are always model-current (every
            // store change refreshes them first), so this preserves genuine
            // uncommitted typing and is a no-op otherwise.
            if (args.InRecycleQueue)
            {
                // Pooled containers keep their boxes (and live sessions)
                // until rebind, and the model can change while pooled
                // (undo/redo/ops). Committing a box whose text the user did
                // not touch since the last sync would regress the model, so
                // DropSessions (which skips untouched boxes via LastSeenModel
                // but preserves genuine typing) runs here, at recycle time.
                if (args.ItemContainer.ContentTemplateRoot is Grid oldCard)
                    DropSessions(oldCard);
                // A pooled container keeps its last visuals until rebind: a
                // selected-then-recycled card would otherwise flash (or keep)
                // its bar on another line. Reset to unselected now; prepare
                // re-applies from the selection set. The number is cleared
                // too so a pooled card can never show another line's number
                // if prepare is delayed (the "2, 2" / "7, 7" duplicates).
                if (args.ItemContainer is ListViewItem recycled)
                    ResetSelectionVisual(recycled);
                if (args.ItemContainer.ContentTemplateRoot is Grid oldCard2
                    && oldCard2.FindName("LineNumber") is TextBlock oldNumber)
                    oldNumber.Text = "";
                return;
            }
            if (args.ItemContainer.ContentTemplateRoot is not Grid card) return;
            if (args.Item is not EditLine line) return;
            // The displayed number is DERIVED from the line's CURRENT index
            // in the document (not the container index, which can lag during
            // structural layout). Fallback to the container index only if the
            // line is momentarily absent from the document.
            var index = _store?.Document.Lines.IndexOf(line) ?? -1;
            if (index < 0) index = LineList.IndexFromContainer(args.ItemContainer);
            var number = card.FindName("LineNumber") as TextBlock;
            if (number is not null)
            {
                var want = (index + 1).ToString();
                if (number.Text != want)
                {
                    number.Text = want;
                    AutomationProperties.SetName(number, $"Line number {index + 1}");
                }
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
            ApplyFlagVisual(args.ItemContainer, index, line,
                _store is null ? "v1" : DuetDisplay.PrimaryAgent(_store.Document.Lines));
            // Indicator from the selection set (single source), not the VSM:
            // recycled containers arrive here with arbitrary stale states.
            if (_selection is not null && args.ItemContainer is ListViewItem prepared)
                ApplySelectionVisual(prepared, _selection.SelectedLineIds.Contains(line.Id));
            if (card.FindName("DeleteButton") is Button delete)
            {
                AutomationProperties.SetAutomationId(delete, $"DeleteLine{index + 1}");
                AutomationProperties.SetName(delete, $"Delete line {index + 1}");
            }
            // The XAML "+" beside the chip row resolves its line via Tag
            // (roots are pooled; Tag must follow the current line).
            if (card.FindName("AddWordButton") is Button addWord)
            {
                addWord.Tag = line.Id;
                ToolTipService.SetToolTip(addWord, "Add word");
                AutomationProperties.SetAutomationId(addWord, $"AddWord{index + 1}");
                AutomationProperties.SetName(addWord, $"Add word to line {index + 1}");
            }
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
                if (card.FindName("AddWordButton") is Button addWordBtn)
                    addWordBtn.Click += OnAddWordClick;
                // Empty-area press selects the line (chips, boxes and
                // buttons own theirs). The card + panel are Transparent
                // (hit-testable) so number/badges/gaps/trailing all work.
                // Release completes the click (press only arms/selects-first).
                card.AddHandler(UIElement.PointerPressedEvent,
                    new PointerEventHandler(OnCardPointerPressed), true);
                var flyout = new MenuFlyout();
                flyout.Opening += (_, _) =>
                {
                    // Right-click (or Shift+F10/Menu key) on an unselected
                    // line selects it first; inside a multi-selection the
                    // set is kept and the menu acts on all (original).
                    EnsureLineSelectedForContext(card);
                    RebuildLineMenu(flyout, card);
                };
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
            // The Translation<->Romanization gap equals the chip main<->romaji
            // gap (one shared ChipRowSpacing value). With only one row left
            // the gap drops to 0: a leftover 6px would pad the card bottom
            // and break top==bottom padding. Guarded sets (skip when equal)
            // so steady-state toggles never invalidate layout for nothing.
            if (card.FindName("SubRows") is Grid subRows)
            {
                var subGap = showTrans && showRoman ? ChipRowGap : 0;
                if (subRows.RowSpacing != subGap) subRows.RowSpacing = subGap;
            }
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return;
            var chipGap = showWordRoman ? ChipRowGap : 0;
            foreach (var child in panel.Children)
            {
                if (child is not Grid chip || chip.Children.Count < 2) continue;
                if (chip.RowSpacing != chipGap) chip.RowSpacing = chipGap;
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
            if (_store is not null && _selection?.SelectedLineId is string selId)
            {
                var at = _store.Document.Lines.ToList().FindIndex(l => l.Id == selId);
                if (at >= 0 && LineList.ContainerFromIndex(at) is ListViewItem c)
                {
                    anchorContainer = c;
                    beforeY = c.TransformToVisual(LineList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                }
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
            if (_store is null || _selection?.SelectedLineId is not string id) return;
            var at = _store.Document.Lines.ToList().FindIndex(l => l.Id == id);
            if (at < 0 || LineList.ContainerFromIndex(at) is not ListViewItem container) return;
            var before = container.TransformToVisual(LineList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            LineList.UpdateLayout();
            var after = container.TransformToVisual(LineList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            if (FindScroller() is ScrollViewer sv && Math.Abs(after - before) > 0.5)
                sv.ChangeView(null, sv.VerticalOffset + (after - before), null, true);
        }

        private (ListViewItem? Container, double Y) SnapshotSelected()
        {
            if (_store is not null && _selection?.SelectedLineId is string id)
            {
                var at = _store.Document.Lines.ToList().FindIndex(l => l.Id == id);
                if (at >= 0 && LineList.ContainerFromIndex(at) is ListViewItem container)
                    return (container, container.TransformToVisual(LineList).TransformPoint(new Windows.Foundation.Point(0, 0)).Y);
            }
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
        // automation-name suffix for AT. Duet shown is the EFFECTIVE duet
        // (raw agent vs document primary), not the raw model flag.
        private void ApplyFlagVisual(DependencyObject container, int index, EditLine line, string primary)
        {
            var duet = DuetDisplay.EffectiveDuet(line, primary);
            var name = $"Line {index + 1}";
            if (line.IsBg) name += ", BG";
            if (duet) name += ", duet";
            AutomationProperties.SetName(container, name);
            if (container is ListViewItem item
                && item.ContentTemplateRoot is Grid card)
            {
                if (card.FindName("BgBadge") is FrameworkElement bg)
                    bg.Visibility = line.IsBg ? Visibility.Visible : Visibility.Collapsed;
                if (card.FindName("DuetBadge") is FrameworkElement duetBadge)
                    duetBadge.Visibility = duet ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void RefreshFlagVisuals()
        {
            if (_store is null) return;
            var primary = DuetDisplay.PrimaryAgent(_store.Document.Lines);
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                ApplyFlagVisual(container, i, _store.Document.Lines[i], primary);
            }
        }

        // Realized indices showing the duet badge + the document primary
        // agent (Step-6 display probes, one round-trip each).
        internal string Test_PrimaryAgent() =>
            _store is null ? "nostore" : DuetDisplay.PrimaryAgent(_store.Document.Lines);

        internal string Test_DuetBadges()
        {
            if (_store is null) return "nostore";
            var primary = DuetDisplay.PrimaryAgent(_store.Document.Lines);
            var shown = new List<int>();
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (!DuetDisplay.EffectiveDuet(_store.Document.Lines[i], primary)) continue;
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid card) continue;
                if (card.FindName("DuetBadge") is FrameworkElement badge
                    && badge.Visibility == Visibility.Visible) shown.Add(i);
            }
            return string.Join(",", shown);
        }

        internal string Test_LineAgent(int index)
        {
            if (_store is null || index < 0 || index >= _store.Document.Lines.Count) return "noline";
            var line = _store.Document.Lines[index];
            return line.Unmapped.TryGetValue(EditLine.AgentKey, out var a) ? $"[{a}]" : "nostash";
        }

        // Line numbers are DERIVED from the live document index (number text
        // == index+1, always). Container prepare sets them, and EVERY store
        // change refreshes all realized containers — pooled containers can
        // never show a stale number from before an insert/delete/move/undo.
        private void RefreshRealizedNumbers()
        {
            if (_store is null) return;
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid card) continue;
                if (card.FindName("LineNumber") is TextBlock number)
                {
                    // Guarded set: assigning identical text still invalidates
                    // layout (measured 0.5px drift in pad checks), so only
                    // touch the box when the number actually changed.
                    var want = (i + 1).ToString();
                    if (number.Text != want)
                    {
                        number.Text = want;
                        AutomationProperties.SetName(number, $"Line number {i + 1}");
                    }
                }
            }
        }

        // Realized line numbers as "index:number,..." in one round-trip (the
        // per-step convergence probe for structural stresses).
        internal string Test_RealizedNumbers()
        {
            if (_store is null) return "nostore";
            var parts = new List<string>();
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid card) continue;
                if (card.FindName("LineNumber") is TextBlock number)
                    parts.Add($"{i}:{number.Text}");
            }
            return string.Join(",", parts);
        }

        // Selection indicator, driven from ONE source (the selection set):
        // the overlay accent bar shows and the card takes the selected fill
        // iff the line's id is selected. Re-applied on every selection
        // change, container prepare and recycle/clear, so pooled containers
        // can never show stale bars on other lines.
        private static (Border? Root, Rectangle? Pill, Rectangle? Tint) FindCardChrome(DependencyObject container)
        {
            Border? root = null;
            Rectangle? pill = null;
            Rectangle? tint = null;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(container);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is Border border && border.Name == "Root") root = border;
                else if (current is Rectangle rect && rect.Name == "Pill") pill = rect;
                else if (current is Rectangle rect2 && rect2.Name == "SelectionTint") tint = rect2;
                if (root is not null && pill is not null && tint is not null) break;
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++)
                    queue.Enqueue(VisualTreeHelper.GetChild(current, i));
            }
            return (root, pill, tint);
        }

        // Selected lines get a LOW-SATURATION accent tint: the SelectionTint
        // overlay (20% live Windows accent) over the card fill, normal text
        // color kept, plus the accent bar. Far less bright than a full accent
        // fill, so text stays readable; hover (CardBackgroundFillColorSecondary,
        // no bar, no tint) is clearly different. The card Background is never
        // overwritten, so Light/Dark/High Contrast all resolve via the
        // template + theme resources with zero code blending.
        // sRGB blend helper for the contrast PROBE only (mirrors what the
        // 20%-opacity SelectionTint overlay composites on screen). No product
        // visuals are computed in code: the tint/wash overlays use the live
        // accent theme resource, and the card fill always comes from the
        // template Background — so Light/Dark/High Contrast are correct with
        // zero blending. (Application.Current.Resources lookups are NOT
        // theme-resolved — they return Light values in Dark — which is why no
        // product path may read colors from the app dictionary.)
        private static Windows.UI.Color Blend(Windows.UI.Color baseColor, Windows.UI.Color accent, double accentAmount)
        {
            var a = Math.Clamp(accentAmount, 0, 1);
            byte Mix(byte b, byte f) => (byte)Math.Round(b * (1 - a) + f * a);
            return Windows.UI.Color.FromArgb(255, Mix(baseColor.R, accent.R), Mix(baseColor.G, accent.G), Mix(baseColor.B, accent.B));
        }

        // Theme-aware brush resolution for CODE-created chip visuals.
        // Application.Current.Resources[key] bypasses ThemeDictionaries (Light
        // values leak into Dark/HC). This walks the merged dictionaries and
        // picks the entry for the CURRENT theme (HighContrast / Light /
        // Default), so code brushes match the XAML {ThemeResource} parts.
        // High Contrast maps to system colors via the HC dictionary entries.
        private Windows.UI.Color ResolveThemeColor(string key)
        {
            var hc = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            var dictName = hc ? "HighContrast" : ActualTheme == ElementTheme.Light ? "Light" : "Default";
            ResourceDictionary? FindDict(ResourceDictionary root)
            {
                if (root.ThemeDictionaries.TryGetValue(dictName, out var hit) && hit is ResourceDictionary d)
                    return d;
                foreach (var m in root.MergedDictionaries)
                {
                    var found = FindDict(m);
                    if (found is not null) return found;
                }
                return null;
            }
            var dict = FindDict(Application.Current.Resources);
            if (dict is not null)
            {
                if (dict.TryGetValue(key, out var v) && v is Windows.UI.Color c) return c;
                if (dict.TryGetValue(key + "Brush", out var bv) && bv is SolidColorBrush b) return b.Color;
            }
            // Fallback: live accent / text from a realized element, else white.
            if (key.StartsWith("Accent", StringComparison.Ordinal))
            {
                try { return _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent); }
                catch { }
            }
            return Windows.UI.Color.FromArgb(255, 255, 255, 255);
        }

        private SolidColorBrush MakeThemeBrush(string key) =>
            new(ResolveThemeColor(key));

        // Debug pin for the theme-resolution path (Step 1 root-cause work):
        // which dictionary was picked, whether it was found, and what the
        // three code-brush keys resolve to under the live theme.
        internal string Test_ThemeDebug()
        {
            var hc = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            var dictName = hc ? "HighContrast" : ActualTheme == ElementTheme.Light ? "Light" : "Default";
            ResourceDictionary? FindDict(ResourceDictionary root)
            {
                if (root.ThemeDictionaries.TryGetValue(dictName, out var hit) && hit is ResourceDictionary d)
                    return d;
                foreach (var m in root.MergedDictionaries)
                {
                    var found = FindDict(m);
                    if (found is not null) return found;
                }
                return null;
            }
            var dict = FindDict(Application.Current.Resources);
            static string HexC(Windows.UI.Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            var merged = Application.Current.Resources.MergedDictionaries.Count;
            var themeDicts = Application.Current.Resources.ThemeDictionaries.Count;
            return $"actual={ActualTheme} hc={hc} dict={dictName} found={(dict is null ? 0 : 1)} " +
                $"merged={merged} rootthemes={themeDicts} " +
                $"card={HexC(ResolveThemeColor("CardBackgroundFillColorDefault"))} " +
                $"accent={HexC(ResolveThemeColor("AccentFillColorDefault"))} " +
                $"secondary={HexC(ResolveThemeColor("TextFillColorSecondary"))}";
        }

        // Repaints code-created chip brushes on every realized card (outline,
        // wash, space label + drop mark). Called on theme/accent change only
        // (ActualThemeChanged + UISettings.ColorValuesChanged): selection
        // passes touch visibility alone, and (re)built chips resolve fresh
        // brushes at build time, so colors can never go stale and selection
        // stays O(realized) cheap even in scroll/selection storms.
        // Children order is fixed in BuildChip: 0 text, 1 roman, 2 label,
        // 3 outline, 4 dropMark, 5 wash.
        private void RefreshChipThemeBrushes()
        {
            if (_store is null) return;
            var accent = ResolveThemeColor("AccentFillColorDefault");
            var secondary = ResolveThemeColor("TextFillColorSecondary");
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid card) continue;
                if (card.FindName("ChipPanel") is not LyricWrapPanel panel) continue;
                foreach (var child in panel.Children)
                {
                    if (child is not Grid chip) continue;
                    if (chip.Children.Count > 2 && chip.Children[2] is TextBlock label)
                        label.Foreground = new SolidColorBrush(secondary);
                    if (chip.Children.Count > 3 && chip.Children[3] is Border outline)
                        outline.BorderBrush = new SolidColorBrush(accent);
                    if (chip.Children.Count > 4 && chip.Children[4] is Border mark)
                        mark.BorderBrush = new SolidColorBrush(accent);
                    if (chip.Children.Count > 5 && chip.Children[5] is Border wash)
                        wash.Background = new SolidColorBrush(accent);
                }
            }
        }

        private void ApplySelectionVisual(ListViewItem container, bool selected)
        {
            var (root, pill, tint) = FindCardChrome(container);
            _ = root;
            if (pill is not null) pill.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            // The card fill is NEVER overwritten here: Root keeps its template
            // Background ({ThemeResource}, theme-correct) and the translucent
            // SelectionTint overlay supplies the accent tint.
            if (tint is not null) tint.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplySelectionVisuals()
        {
            if (_store is null || _selection is null) return;
            if (DispatcherQueue.HasThreadAccess) ApplySelectionVisualsNow();
            else DispatcherQueue.TryEnqueue(ApplySelectionVisualsNow);
        }

        private void ApplySelectionVisualsNow()
        {
            if (_store is null || _selection is null) return;
            var selected = _selection.SelectedLineIds;
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                ApplySelectionVisual(container, selected.Contains(_store.Document.Lines[i].Id));
            }
            ApplyWordSelectionVisualsNow();
        }

        // Selected words: accent outline + faint accent wash BEHIND the chip
        // content (a translucent overlay at ZIndex -1, so text stays crisp)
        // — the original's outlined chips on a filled line. The wash uses the
        // live accent theme resource, so no theme can make it unreadable.
        private void ApplyWordSelectionVisualsNow()
        {
            if (_store is null || _selection is null) return;
            var selected = _selection.SelectedWordIds;
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid card) continue;
                if (card.FindName("ChipPanel") is not LyricWrapPanel panel) continue;
                var line = _store.Document.Lines[i];
                foreach (var child in panel.Children)
                {
                    if (child is not Grid chip || chip.Tag is not string wordId) continue;
                    if (!line.Words.Any(w => w.Id == wordId)) continue;
                    var on = selected.Contains(wordId);
                    if (chip.Children.Count >= 4 && chip.Children[3] is Border outline)
                        outline.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                    if (chip.Children.Count >= 6 && chip.Children[5] is Border wash)
                        wash.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        private void ResetSelectionVisual(ListViewItem container) =>
            ApplySelectionVisual(container, selected: false);

        // A word whose text is only whitespace (length > 0) is a SPACE chip,
        // like the original (`useWordBlank` + `Space x{count}` with
        // count = word.length; PORTING-NOTES §h). `emptyBeat` is orthogonal
        // (an int field, NOT the space count), so it is not part of the gate.
        // Space words normally carry no timing, but that is left to the
        // model/commands — the chip only changes how they render.
        internal static bool IsSpaceWord(EditWord word) =>
            word.Text.Length > 0 && string.IsNullOrWhiteSpace(word.Text);

        internal static string SpaceLabelFor(EditWord word) =>
            $"Space x{word.Text.Length}";

        // The ONE shared main<->romaji gap (App.xaml ChipRowSpacing): the
        // chip grid and the card Translation/Romanization rows both use it.
        internal static double ChipRowGap =>
            Application.Current.Resources.TryGetValue("ChipRowSpacing", out var v)
            && v is double d ? d : 6;

        private static bool IsShiftDown() =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        private Grid BuildChip(EditLine line, EditWord word, int index, int lineIndex)
        {
            var chip = new Grid
            {
                // No minimum: a chip unit hugs max(main, romaji). The old
                // MinWidth=44 plus the TextBox theme floors (MinWidth 64 +
                // padding) turned "キ" into a huge box.
                MinWidth = 0,
                Tag = word.Id,
                RowSpacing = ChipRowGap,
            };
            chip.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chip.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var isSpace = IsSpaceWord(word);
            // Spellcheck/prediction off: lyric text is often non-dictionary
            // (and CJK); predictions interfere with commit/IME flows and add
            // per-keystroke text-service cost. IME composition itself is
            // unaffected (guarded separately via TextComposition events).
            // MinWidth/MinHeight/Padding override the TextBox theme floors
            // so single glyphs stay compact (item 8).
            var text = new TextBox
            {
                Text = word.Text,
                AcceptsReturn = false,
                IsTabStop = true,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(6, 3, 6, 3),
                Tag = word.Id,
            };
            var roman = new TextBox
            {
                Text = word.RomanWord,
                AcceptsReturn = false,
                IsTabStop = true,
                IsSpellCheckEnabled = false,
                IsTextPredictionEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(6, 3, 6, 3),
                Tag = word.Id,
            };
            Grid.SetRow(roman, 1);
            // Raw model text stays in the box (never the "Space xN" display
            // label: committing that label would write it into the model).
            // The label is a separate dim TextBlock overlaying row 0 while
            // the box is collapsed; AT still gets "Space ×N" on both.
            var label = new TextBlock
            {
                Text = SpaceLabelFor(word),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 3, 6, 3),
                Foreground = MakeThemeBrush("TextFillColorSecondary"),
                IsHitTestVisible = false,
                Tag = word.Id,
            };
            // SELECT state (unfocused box) shows the dim label for space
            // words; EDIT state (focused box) shows the raw-text box. Boxes
            // are always editable: the two states are focus location (item =
            // select, box = edit), never a property toggle — so commit, IME,
            // caret and undo paths stay byte-identical to Phase 3.
            var showLabel = isSpace;
            text.Visibility = showLabel ? Visibility.Collapsed : Visibility.Visible;
            label.Visibility = showLabel ? Visibility.Visible : Visibility.Collapsed;
            // The two boxes share the chip width (grid = max child width,
            // both stretch): widths hug the wider content instantly while
            // typing, with no animation and no rebuild, so caret/focus stay.
            // A collapsed main box is replaced by the label for measuring.
            AutomationProperties.SetAutomationId(text, $"ChipText{lineIndex + 1}_{index + 1}");
            AutomationProperties.SetName(text, isSpace
                ? $"Word {index + 1}: Space \u00D7{word.Text.Length}"
                : $"Word {index + 1}: {word.Text}");
            AutomationProperties.SetAutomationId(roman, $"ChipRoman{lineIndex + 1}_{index + 1}");
            AutomationProperties.SetName(roman, $"Romanization of word {index + 1}");
            AutomationProperties.SetAutomationId(label, $"SpaceLabel{lineIndex + 1}_{index + 1}");
            AutomationProperties.SetName(label, isSpace
                ? $"Space \u00D7{word.Text.Length}"
                : $"Word {index + 1}");
            var wordId = word.Id;
            WireBox(text,
                () => line.Words.FirstOrDefault(w => w.Id == wordId)?.Text ?? "",
                _ => CommitWordTextBox(text, line, wordId, roman: false));
            WireBox(roman,
                () => line.Words.FirstOrDefault(w => w.Id == wordId)?.RomanWord ?? "",
                _ => CommitWordTextBox(roman, line, wordId, roman: true));
            // handledEventsToo: TextBoxes mark pointer input handled, which
            // would otherwise starve both this handler and item select.
            // Press arms (selection on release, like the original's
            // pointerdown/up split); release completes the click gesture.
            chip.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(OnChipPointerPressed), true);

            // Double-click enters EDIT (focuses the box); single click stays
            // SELECT (PointerPressed selects + focuses the item, no caret).
            chip.DoubleTapped += (_, e) =>
            {
                if (IsCtrlDown() || IsShiftDown()) return;
                var (tappedId, tappedRoman) = ChipFieldFromSource(e.OriginalSource);
                if (tappedId is not null) EnterWordEdit(tappedId, tappedRoman);
            };
            chip.Children.Add(text);
            chip.Children.Add(roman);
            chip.Children.Add(label);
            // Selected-word outline: accent border over the unit (the wash is
            // a separate behind-content overlay, so text stays crisp).
            var selOutline = new Border
            {
                BorderBrush = MakeThemeBrush("AccentFillColorDefault"),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Visibility = _selection?.SelectedWordIds.Contains(wordId) == true
                    ? Visibility.Visible : Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            Grid.SetRowSpan(selOutline, 2);
            chip.Children.Add(selOutline);
            // Drop mark: vertical accent bar on the chip's left/right edge
            // (dropLeft/dropRight in the original). Border-only overlay, no
            // layout shift; side set per hover half.
            var dropMark = new Border
            {
                BorderBrush = MakeThemeBrush("AccentFillColorDefault"),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(2),
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            Grid.SetRowSpan(dropMark, 2);
            chip.Children.Add(dropMark);
            // Selected-word wash: translucent live-accent fill BEHIND the chip
            // content (Canvas.ZIndex -1 renders it below the boxes regardless
            // of child order, so text stays crisp). Opacity 0.15 ≈ the faint
            // fill from the spec; the card never changes its own Background.
            var selWash = new Border
            {
                Background = MakeThemeBrush("AccentFillColorDefault"),
                Opacity = 0.15,
                CornerRadius = new CornerRadius(4),
                Visibility = _selection?.SelectedWordIds.Contains(wordId) == true
                    ? Visibility.Visible : Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            Grid.SetRowSpan(selWash, 2);
            Canvas.SetZIndex(selWash, -1);
            chip.Children.Add(selWash);
            AttachWordMenu(chip, text, roman, line.Id, wordId);
            return chip;
        }

        // Resolves (wordId, romanField) from a DoubleTapped OriginalSource:
        // a box gives its field, the space label (hit-transparent, so the
        // source is the chip) gives the main field.
        private static (string? WordId, bool Roman) ChipFieldFromSource(object source)
        {
            var current = source as DependencyObject;
            while (current is not null)
            {
                if (current is TextBox box && box.Tag is string boxId)
                    return (boxId, Grid.GetRow(box) == 1);
                if (current is Grid grid && grid.Tag is string gridId)
                    return (gridId, false);
                current = VisualTreeHelper.GetParent(current);
            }
            return (null, false);
        }

        private bool IsSpaceId(string wordId)
        {
            if (_store is null) return false;
            foreach (var line in _store.Document.Lines)
            {
                var word = line.Words.FirstOrDefault(w => w.Id == wordId);
                if (word is not null) return IsSpaceWord(word);
            }
            return false;
        }

        // EDIT entry (double-click / F2 / Enter / Test hook): swaps a space
        // label for its raw-text box if needed, focuses the box and selects
        // its content. Selection is left alone (focus only anchors it).
        internal bool EnterWordEdit(string wordId, bool roman)
        {
            if (_store is null || _selection is null) return false;
            // Editing implies the primary (focus and ribbon follow the word
            // being edited). Done here — not only in GotFocus — so the
            // primary is deterministic even where focus-event routing is
            // limited; GotFocus re-anchors identically for Tab users.
            if (LineOfWord(wordId) is string lineId)
            {
                _selection.AnchorWord(lineId, wordId);
            }
            foreach (var chip in FindChips(wordId))
                ShowChipEditor(chip, roman);
            var box = FindChipBoxById(wordId, roman);
            if (box is null) return false;
            box.Focus(FocusState.Programmatic);
            box.SelectionStart = 0;
            box.SelectionLength = box.Text.Length;
            return true;
        }

        // Drives the real GotFocus handler directly (same logic as keyboard
        // Tab focus; Focus() event delivery itself is observed separately
        // since inactive headless windows route focus state without events).
        // The box is focused first so the handler's stale-event guard (which
        // drops GotFocus for a box that is no longer focused) lets it through
        // deterministically.
        internal bool Test_AnchorBox(int line, int word, bool roman)
        {
            var box = FindChipBox(line, word, roman);
            if (box is null) return false;
            box.Focus(FocusState.Programmatic);
            OnEditGotFocus(box, new RoutedEventArgs());
            return true;
        }

        internal bool EnterSpaceEdit(string wordId) => EnterWordEdit(wordId, roman: false);

        // SELECT return: focus back on the line container (no caret
        // anywhere). Commit/revert themselves run through the normal box
        // session before this is called.
        internal void ExitWordEdit(string wordId)
        {
            foreach (var chip in FindChips(wordId))
            {
                var container = FindListViewItem(chip);
                container?.Focus(FocusState.Programmatic);
            }
        }

        // --- Drag and drop (item 12, pointer-based, no OS DnD) ------------
        // Lines and words drag with the pointer (3px threshold like the
        // original's useLyricListDrag); Ctrl at drop time (shown live while
        // dragging) makes it a copy, otherwise a move. One drop = one undo
        // step via ReorderLinesCommand / MoveWordsCommand. Internal drags
        // never touch a DataPackage, so the window-level drop-to-open
        // overlay (StorageItems-gated in ShellPage) cannot appear for them.
        // Timings: normalizeLineTime runs only at touched bounds on drop,
        // exactly like the original (PORTING-NOTES section h).

        private sealed class DragSession
        {
            public bool IsWords;
            public List<string> Ids = new();
            public bool Started;
            public int DropLineIndex = -1; // lines: insertion index, -1 invalid
            public string? DropLineId;     // words: target line, null invalid
            public string? DropWordId;     // words: target word, null append
            public bool DropAfter;
            public double LastX;
            public double LastY;
            // Copy flag as last painted on the ghost. Refreshed from the live
            // Ctrl state on every pointer move AND every 16ms timer tick, so
            // a press/release with a still mouse flips the label promptly.
            public bool ShownCopy;
            public List<(FrameworkElement El, double Opacity)> Dimmed = new();
            public Grid? MarkChip;
            public int ScrollDir;
            public List<DateTime> ScrollTicks = new();
            public double ScrollFrom;
            public double ScrollTo;
        }

        private sealed class PressArm
        {
            public bool IsWords;
            public string LineId = "";
            public string? WordId;
            public double X;
            public double Y;
            public bool Ctrl;
            public bool Shift;
        }

        private PressArm? _pressArm;
        private DragSession? _drag;
        private DispatcherTimer? _dragTimer;

        private void ArmDrag(bool words, string lineId, string? wordId,
            Windows.Foundation.Point point, bool ctrl, bool shift)
        {
            _pressArm = new PressArm
            {
                IsWords = words, LineId = lineId, WordId = wordId,
                X = point.X, Y = point.Y, Ctrl = ctrl, Shift = shift,
            };
        }

        private void DisarmDrag() => _pressArm = null;

        private void OnDragPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_store is null) return;
            var point = e.GetCurrentPoint(LineList).Position;
            if (_drag is null)
            {
                if (_pressArm is null) return;
                var arm = _pressArm;
                var dx = point.X - arm.X;
                var dy = point.Y - arm.Y;
                if (dx * dx + dy * dy < 16) return; // 4px threshold
                BeginDrag(arm.IsWords, arm.LineId, arm.WordId);
                if (_drag is null) return;
            }
            UpdateDrag(point.X, point.Y);
        }

        private void OnDragPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            // A release after a started drag is the drop (copy = Ctrl held
            // at drop time). Otherwise an armed press completes its click
            // here — anywhere, even off-element — with press-time modifiers.
            if (_drag is not null && _drag.Started)
            {
                DropExecute(IsCtrlDown());
                e.Handled = true;
                return;
            }
            if (_pressArm is null || _selection is null || _store is null) return;
            var arm = _pressArm;
            DisarmDrag();
            var line = _store.Document.Lines.FirstOrDefault(l => l.Id == arm.LineId);
            if (line is null) return;
            if (arm.IsWords)
            {
                if (arm.WordId is null) return;
                var word = line.Words.FirstOrDefault(w => w.Id == arm.WordId);
                if (word is null) return;
                var index = _store.Document.Lines.IndexOf(line);
                WordPressGesture(line, index, word.Id, arm.Ctrl, arm.Shift);
            }
            else if (arm.Ctrl && !arm.Shift)
            {
                _selection.ToggleLine(line.Id);
                _selection.RetainWords(LineOfWord);
            }
            else if (arm.Shift)
            {
                ShiftSelectLineRange(line);
            }
            else
            {
                _selection.SelectLine(line.Id);
            }
            if (FindListViewItemForLine(line.Id) is ListViewItem container)
                container.Focus(FocusState.Programmatic);
            e.Handled = true;
        }

        // Shift+click line range from the anchor (SelectionService.AnchorLineId,
        // falling back to the primary when no anchor exists yet). The set
        // becomes exactly anchor..clicked, replacing any prior selection.
        private void ShiftSelectLineRange(EditLine line)
        {
            if (_store is null || _selection is null) return;
            var lines = _store.Document.Lines.ToList();
            var anchorId = _selection.AnchorLineId ?? _selection.SelectedLineId;
            var anchor = anchorId is string aid
                ? lines.FindIndex(l => l.Id == aid) : -1;
            var index = lines.FindIndex(l => l.Id == line.Id);
            if (anchor < 0 || index < 0)
            {
                _selection.SelectLine(line.Id);
                return;
            }
            var lo = Math.Min(anchor, index);
            var hi = Math.Max(anchor, index);
            _selection.SelectLineRange(
                lines.Skip(lo).Take(hi - lo + 1).Select(l => l.Id),
                line.Id);
        }

        private void OnDragCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            // Pointer lost mid-drag (window deactivated, gesture stolen):
            // cancel rather than dropping somewhere unintended.
            if (_drag is not null) CancelDrag();
            else DisarmDrag();
        }

        // Payload = current selection in document order. A drag starting on
        // an unselected item selects it first (single), like the original's
        // handleDragStart; a start inside the selection keeps it whole.
        private void BeginDrag(bool words, string lineId, string? wordId)
        {
            if (_store is null || _selection is null || _drag is not null) return;
            var startSelected = words
                ? (wordId is not null && _selection.SelectedWordIds.Contains(wordId))
                : _selection.SelectedLineIds.Contains(lineId);
            if (!startSelected)
            {
                var line = _store.Document.Lines.FirstOrDefault(l => l.Id == lineId);
                if (line is null) { DisarmDrag(); return; }
                if (words && wordId is not null) _selection.SelectWord(lineId, wordId);
                else _selection.SelectLine(lineId);
            }
            DisarmDrag();
            var session = new DragSession { IsWords = words, Started = true };
            if (words)
            {
                var wanted = _selection.SelectedWordIds;
                foreach (var line in _store.Document.Lines)
                    foreach (var w in line.Words)
                        if (wanted.Contains(w.Id)) session.Ids.Add(w.Id);
                if (session.Ids.Count == 0) return;
            }
            else
            {
                var wanted = _selection.SelectedLineIds;
                foreach (var line in _store.Document.Lines)
                    if (wanted.Contains(line.Id)) session.Ids.Add(line.Id);
                if (session.Ids.Count == 0) return;
            }
            _drag = session;
            _drag.ShownCopy = IsCtrlDown();
            if (FindListViewItemForLine(lineId) is ListViewItem container)
                container.Focus(FocusState.Programmatic);
            EnsureDragTimer();
            _dragTimer?.Start();
            RefreshDragVisuals();
        }

        private ListViewItem? FindListViewItemForLine(string lineId)
        {
            if (_store is null) return null;
            var at = _store.Document.Lines.ToList().FindIndex(l => l.Id == lineId);
            return at < 0 ? null
                : LineList.ContainerFromIndex(at) as ListViewItem;
        }

        private void EnsureDragTimer()
        {
            if (_dragTimer is not null) return;
            _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _dragTimer.Tick += (_, _) => ScrollTick();
        }

        private void UpdateDrag(double x, double y)
        {
            if (_drag is null || _store is null) return;
            _drag.LastX = x;
            _drag.LastY = y;
            _drag.ShownCopy = IsCtrlDown();
            if (_drag.IsWords) UpdateWordTarget(x, y);
            else UpdateLineTarget(y);
            // Auto-scroll near the list edges (40px, original profile).
            _drag.ScrollDir = y < 40 ? -1
                : y > Math.Max(41, LineList.ActualHeight - 40) ? 1 : 0;
            RefreshDragVisuals();
        }

        private void UpdateLineTarget(double y)
        {
            if (_drag is null || _store is null) return;
            var dragged = new HashSet<string>(_drag.Ids);
            var realized = new List<(int Index, double Top, double Bottom, string Id)>();
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                var top = container.TransformToVisual(LineList)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                realized.Add((i, top, top + container.ActualHeight,
                    _store.Document.Lines[i].Id));
            }
            realized.Sort((a, b) => a.Top.CompareTo(b.Top));
            _drag.DropLineIndex = -1;
            foreach (var (index, top, bottom, id) in realized)
            {
                if (dragged.Contains(id))
                {
                    // Over a dragged line: no indicator (original hides it).
                    if (y >= top && y <= bottom) return;
                    continue;
                }
                var mid = (top + bottom) / 2;
                if (y < mid)
                {
                    _drag.DropLineIndex = index;
                    PositionDropLine(top - 4);
                    return;
                }
            }
            if (realized.Count == 0) return;
            _drag.DropLineIndex = _store.Document.Lines.Count;
            PositionDropLine(realized[^1].Bottom + 4);
        }

        private void PositionDropLine(double y)
        {
            DropLineMark.Visibility = Visibility.Visible;
            Canvas.SetLeft(DropLineMark, 8);
            Canvas.SetTop(DropLineMark, Math.Max(0, y));
            DropLineMark.Width = Math.Max(0, LineList.ActualWidth - 16);
        }

        private void UpdateWordTarget(double x, double y)
        {
            if (_drag is null || _store is null) return;
            HideChipMark();
            _drag.DropLineId = null;
            _drag.DropWordId = null;
            var dragged = new HashSet<string>(_drag.Ids);
            LyricWrapPanel? panel = null;
            Grid? hitChip = null;
            foreach (var el in VisualTreeHelper.FindElementsInHostCoordinates(
                new Windows.Foundation.Point(x, y), LineList))
            {
                var current = el as DependencyObject;
                while (current is not null)
                {
                    if (current is Grid grid && grid.Tag is string)
                    {
                        hitChip = grid;
                        break;
                    }
                    if (current is LyricWrapPanel wrap) { panel = wrap; break; }
                    current = VisualTreeHelper.GetParent(current);
                }
                if (hitChip is not null || panel is not null) break;
            }
            if (hitChip is null && panel is null)
            {
                // Fall back to the nearest realized panel by row.
                panel = PanelAtY(y);
                if (panel is null) return;
            }
            if (panel is null && hitChip is not null)
                panel = FindChipPanel(hitChip);
            if (panel is null) return;
            var card = FindCardOfPanel(panel);
            if (card is null) return;
            var container = FindListViewItem(card);
            if (container is null) return;
            var lineIndex = LineList.IndexFromContainer(container);
            if (lineIndex < 0 || lineIndex >= _store.Document.Lines.Count) return;
            var line = _store.Document.Lines[lineIndex];
            var chips = panel.Children.OfType<Grid>().ToList();
            if (chips.Count == 0)
            {
                // Empty line: append (never a no-op target of itself).
                _drag.DropLineId = line.Id;
                _drag.DropWordId = null;
                return;
            }
            Grid? target = null;
            bool after = false;
            foreach (var chip in chips)
            {
                var left = chip.TransformToVisual(panel)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                var mid = left + chip.ActualWidth / 2;
                if (x < mid) { target = chip; after = false; break; }
                target = chip;
                after = true;
            }
            if (target is null || target.Tag is not string targetId) return;
            if (dragged.Contains(targetId)) return; // onto its own selection
            _drag.DropLineId = line.Id;
            _drag.DropWordId = targetId;
            _drag.DropAfter = after;
            ShowChipMark(target, after);
        }

        private LyricWrapPanel? PanelAtY(double y)
        {
            if (_store is null) return null;
            LyricWrapPanel? best = null;
            var bestDy = double.MaxValue;
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid card) continue;
                if (card.FindName("ChipPanel") is not LyricWrapPanel panel) continue;
                var top = panel.TransformToVisual(LineList)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                var dy = Math.Abs(y - (top + panel.ActualHeight / 2));
                if (dy < bestDy) { bestDy = dy; best = panel; }
            }
            return best;
        }

        private static LyricWrapPanel? FindChipPanel(Grid chip)
        {
            var current = VisualTreeHelper.GetParent(chip);
            while (current is not null && current is not LyricWrapPanel)
                current = VisualTreeHelper.GetParent(current);
            return current as LyricWrapPanel;
        }

        private static Grid? FindCardOfPanel(LyricWrapPanel panel)
        {
            var current = VisualTreeHelper.GetParent(panel);
            while (current is not null)
            {
                if (current is Grid grid && grid.Name == "CardRoot") return grid;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void ShowChipMark(Grid chip, bool after)
        {
            HideChipMark();
            if (chip.Children.Count >= 5 && chip.Children[4] is Border mark)
            {
                mark.BorderThickness = after
                    ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
                mark.Visibility = Visibility.Visible;
                _drag!.MarkChip = chip;
            }
        }

        private void HideChipMark()
        {
            if (_drag?.MarkChip is Grid chip
                && chip.Children.Count >= 5 && chip.Children[4] is Border mark)
                mark.Visibility = Visibility.Collapsed;
            if (_drag is not null) _drag.MarkChip = null;
        }

        private void RefreshDragVisuals()
        {
            if (_drag is null || _store is null) return;
            RestoreDimmed();
            // Dragged items translucent (realized only; re-applied every
            // move so recycled containers rejoin the look after scrolls).
            if (_drag.IsWords)
            {
                foreach (var id in _drag.Ids)
                    foreach (var chip in FindChips(id))
                    {
                        _drag.Dimmed.Add((chip, chip.Opacity));
                        chip.Opacity = 0.4;
                    }
            }
            else
            {
                var lines = _store.Document.Lines.ToList();
                foreach (var id in _drag.Ids)
                {
                    var at = lines.FindIndex(l => l.Id == id);
                    if (at < 0) continue;
                    if (LineList.ContainerFromIndex(at) is ListViewItem container)
                    {
                        _drag.Dimmed.Add((container, container.Opacity));
                        container.Opacity = 0.4;
                    }
                }
            }
            // Ghost label: "Move to N" / "Copy to N" with the item count.
            // The copy flag is the session's polled value (updated within one
            // 16ms timer tick of a Ctrl press/release, even with the mouse
            // still — WinUI delivers no pointer move then, so moves alone
            // would leave a stale label).
            var copy = _drag.ShownCopy;
            DragGhost.Visibility = Visibility.Visible;
            DragGhostText.Text = GhostText(copy);
            DragOverlay.UpdateLayout();
            var ox = Math.Max(0, Math.Min(_drag.LastX + 16,
                Math.Max(0, DragOverlay.ActualWidth - 140)));
            var oy = Math.Max(0, Math.Min(_drag.LastY + 20,
                Math.Max(0, DragOverlay.ActualHeight - 40)));
            Canvas.SetLeft(DragGhost, ox);
            Canvas.SetTop(DragGhost, oy);
        }

        private string GhostText(bool copy)
        {
            if (_drag is null || _store is null) return "";
            var verb = copy ? "Copy" : "Move";
            var unit = _drag.IsWords ? "word" : "line";
            var countText = _drag.Ids.Count == 1
                ? $"1 {unit}" : $"{_drag.Ids.Count} {unit}s";
            string target;
            if (_drag.IsWords)
            {
                if (_drag.DropLineId is null) return $"{verb} {countText}";
                var at = _store.Document.Lines.ToList().FindIndex(l => l.Id == _drag.DropLineId);
                target = (Math.Max(0, at) + 1).ToString();
            }
            else
            {
                if (_drag.DropLineIndex < 0) return $"{verb} {countText}";
                target = (Math.Max(0, Math.Min(_drag.DropLineIndex,
                    _store.Document.Lines.Count - 1)) + 1).ToString();
            }
            return $"{verb} to {target} · {countText}";
        }

        private void RestoreDimmed()
        {
            if (_drag is null) return;
            foreach (var (el, opacity) in _drag.Dimmed)
            {
                try { el.Opacity = opacity; } catch { }
            }
            _drag.Dimmed.Clear();
        }

        private void ScrollTick()
        {
            if (_drag is null || !_drag.Started) return;
            // Ctrl poll: flip Move<->Copy within one 16ms tick even when the
            // mouse is still (no pointer move arrives then). The drop itself
            // reads the live state, so label and operation never disagree.
            var copy = IsCtrlDown();
            if (copy != _drag.ShownCopy)
            {
                _drag.ShownCopy = copy;
                RefreshDragVisuals();
            }
            if (_drag.ScrollDir == 0) return;
            var sv = FindScroller();
            if (sv is null) return;
            var from = sv.VerticalOffset;
            var extent = Math.Max(0, sv.ScrollableHeight);
            var to = Math.Min(extent, Math.Max(0, from + _drag.ScrollDir * 12));
            if (Math.Abs(to - from) < 0.5) return;
            if (_drag.ScrollTicks.Count == 0) _drag.ScrollFrom = from;
            _drag.ScrollTo = to;
            _drag.ScrollTicks.Add(DateTime.UtcNow);
            sv.ChangeView(null, to, null, true);
            LineList.UpdateLayout();
            // Keep the indicator glued while the content moves under it.
            UpdateDrag(_drag.LastX, _drag.LastY);
        }

        private void CleanupDrag()
        {
            if (_drag is null) return;
            RestoreDimmed();
            HideChipMark();
            DropLineMark.Visibility = Visibility.Collapsed;
            DragGhost.Visibility = Visibility.Collapsed;
            _dragTimer?.Stop();
            _drag = null;
            DisarmDrag();
        }

        private void CancelDrag() => CleanupDrag();

        // Pure no-op checks on id sequences (dropping onto the same place
        // or inside its own selection changes nothing: no history step).
        internal static bool LinesDropNoOp(IList<string> allIds, IList<string> draggedIds, int targetIndex)
        {
            var dragged = new HashSet<string>(draggedIds);
            var remaining = allIds.Where(id => !dragged.Contains(id)).ToList();
            var adjust = 0;
            for (var i = 0; i < targetIndex && i < allIds.Count; i++)
                if (dragged.Contains(allIds[i])) adjust++;
            var safe = Math.Max(0, Math.Min(targetIndex - adjust, remaining.Count));
            var result = remaining.Take(safe).Concat(draggedIds).Concat(remaining.Skip(safe)).ToList();
            return result.SequenceEqual(allIds);
        }

        internal static bool WordsDropNoOp(
            Dictionary<string, List<string>> wordsByLine, IList<string> draggedIds,
            string targetLineId, string? targetWordId, bool insertAfter)
        {
            if (targetWordId is not null && draggedIds.Contains(targetWordId))
                return true; // onto its own selection (original ignores it)
            var dragged = new HashSet<string>(draggedIds);
            var ordered = new List<string>();
            foreach (var (lineId, words) in wordsByLine)
                foreach (var w in words)
                    if (dragged.Contains(w)) ordered.Add(w);
            var next = new Dictionary<string, List<string>>();
            foreach (var (lineId, words) in wordsByLine)
                next[lineId] = words.Where(w => !dragged.Contains(w)).ToList();
            if (!next.TryGetValue(targetLineId, out var target)) return true;
            int pos = targetWordId is null
                ? target.Count
                : target.IndexOf(targetWordId) + (insertAfter ? 1 : 0);
            if (pos < 0) return true;
            target.InsertRange(pos, ordered);
            foreach (var (lineId, words) in wordsByLine)
                if (!next[lineId].SequenceEqual(words)) return false;
            return true;
        }

        // Executes the drop (pointer-up path and Test_ hooks share this):
        // exactly ONE history step, then the items are selected and in view.
        // Returns "dropped:lines:n", "dropped:words:n" or "noop:reason".
        // The payload resolves from the LIVE selection at drop time (like
        // the original, which reads selectedWordsAtom in onDrop): nothing
        // can click mid-drag, and hook-driven setups finish selecting
        // before the drop. Session.Ids only feeds ghost/dim visuals.
        internal string DropExecute(bool copy)
        {
            if (_store is null || _selection is null || _drag is null) return "noop:nodrag";
            var session = _drag;
            try
            {
                if (!session.IsWords)
                {
                    if (session.DropLineIndex < 0 || session.DropLineIndex > _store.Document.Lines.Count)
                        return "noop:invalid";
                    var allIds = _store.Document.Lines.Select(l => l.Id).ToList();
                    var ordered = allIds.Where(id => _selection.SelectedLineIds.Contains(id)).ToList();
                    if (ordered.Count == 0) return "noop:empty";
                    if (!copy && LinesDropNoOp(allIds, ordered, session.DropLineIndex))
                        return "noop:same-place";
                    CommitActiveBox();
                    var cmd = new ReorderLinesCommand(ordered, session.DropLineIndex, copy);
                    try { RunWithMoveFlip(() => _store.Execute(cmd)); }
                    catch (InvalidOperationException) { return "noop:stale"; }
                    var resultIds = cmd.ResultIds.ToList();
                    if (copy)
                    {
                        // Original copy: the clones are selected, words clear.
                        _selection.SetLines(resultIds, LineOfWord);
                    }
                    var first = resultIds
                        .Select(id => _store.Document.Lines.ToList().FindIndex(l => l.Id == id))
                        .Where(i => i >= 0).DefaultIfEmpty(-1).Min();
                    if (first >= 0)
                        LineList.ScrollIntoView(_store.Document.Lines[first]);
                    LineList.UpdateLayout();
                    return $"dropped:lines:{resultIds.Count}";
                }
                else
                {
                    if (session.DropLineId is null) return "noop:invalid";
                    var target = _store.Document.Lines.FirstOrDefault(l => l.Id == session.DropLineId);
                    if (target is null) return "noop:stale";
                    var wanted = _selection.SelectedWordIds;
                    if (session.DropWordId is not null
                        && wanted.Contains(session.DropWordId))
                        return "noop:own-selection";
                    var wordsByLine = _store.Document.Lines
                        .ToDictionary(l => l.Id, l => l.Words.Select(w => w.Id).ToList());
                    var ordered = new List<string>();
                    foreach (var line in _store.Document.Lines)
                        foreach (var w in line.Words)
                            if (wanted.Contains(w.Id)) ordered.Add(w.Id);
                    if (ordered.Count == 0) return "noop:empty";
                    if (!copy && WordsDropNoOp(wordsByLine, ordered,
                        session.DropLineId, session.DropWordId, session.DropAfter))
                        return "noop:same-place";
                    if (session.DropWordId is not null
                        && !target.Words.Any(w => w.Id == session.DropWordId))
                        return "noop:stale";
                    CommitActiveBox();
                    var cmd = new MoveWordsCommand(ordered, session.DropLineId,
                        session.DropWordId, session.DropAfter, copy);
                    try { _store.Execute(cmd); }
                    catch (InvalidOperationException) { return "noop:stale"; }
                    var resultIds = cmd.ResultIds.ToList();
                    if (copy)
                    {
                        // Original copy: the clones are selected.
                        _selection.ReplaceWords(session.DropLineId, resultIds,
                            resultIds.FirstOrDefault() ?? "");
                    }
                    var at = _store.Document.Lines.ToList().FindIndex(l => l.Id == session.DropLineId);
                    if (at >= 0) LineList.ScrollIntoView(_store.Document.Lines[at]);
                    LineList.UpdateLayout();
                    return $"dropped:words:{resultIds.Count}";
                }
            }
            finally
            {
                CleanupDrag();
            }
        }

        // Shows the raw-text editor of one field (space-label swap for the
        // main field; no-op for already-visible boxes). A space edit box
        // gets a floor width so the caret stays visible while editing raw
        // whitespace (a 1-space chip would otherwise hug to ~18px).
        private void ShowChipEditor(Grid chip, bool roman)
        {
            if (chip.Children.Count < 3) return;
            if (roman) return;
            if (chip.Children[0] is TextBox text
                && chip.Children[2] is TextBlock label)
            {
                text.Visibility = Visibility.Visible;
                label.Visibility = Visibility.Collapsed;
                if (text.MinWidth < 40) text.MinWidth = 40;
            }
        }

        private List<Grid> FindChips(string wordId)
        {
            var found = new List<Grid>();
            if (_store is null) return found;
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid card) continue;
                if (card.FindName("ChipPanel") is not LyricWrapPanel panel) continue;
                foreach (var child in panel.Children)
                    if (child is Grid chip && chip.Tag is string tag && tag == wordId)
                        found.Add(chip);
            }
            return found;
        }

        private TextBox? FindChipBoxById(string wordId, bool roman)
        {
            foreach (var chip in FindChips(wordId))
                if (chip.Children.Count >= 2 && chip.Children[roman ? 1 : 0] is TextBox box)
                    return box;
            return null;
        }

        // Applies label-vs-editor visibility for one chip's word: the editor
        // shows while its main box is focused (EDIT), otherwise a space word
        // shows its dim "Space xN" label (SELECT). Non-space words always
        // show the editor.
        private void SyncSpaceVisual(string wordId)
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot) as TextBox;
            foreach (var chip in FindChips(wordId))
            {
                if (chip.Children.Count < 3) continue;
                if (chip.Children[0] is not TextBox text) continue;
                if (chip.Children[2] is not TextBlock label) continue;
                var showEditor = !IsSpaceId(wordId) || ReferenceEquals(focused, text);
                text.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
                label.Visibility = showEditor ? Visibility.Collapsed : Visibility.Visible;
                if (!showEditor && text.MinWidth > 0) text.MinWidth = 0;
            }
        }

        private static bool IsCtrlDown() =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        private static bool IsAltDown() =>
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Menu)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        // Chip gestures (SELECT state): single click selects; ctrl+click
        // toggles; shift+click selects a range within the line (original).
        // Focus goes to the line container — never the box — so no caret or
        // text selection can appear: EDIT needs double-click/F2/Enter.
        // Factored as WordPressGesture for the Test_ hook (same pipeline as
        // real pointer input).
        private void OnChipPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Grid chip || chip.Tag is not string wordId) return;
            if (_selection is null || _store is null) return;
            // Right/middle mouse never selects here (the context menu owns
            // right-click, including select-first in its Opening handler —
            // selecting here would destroy a multi-selection first).
            var props = e.GetCurrentPoint(LineList).Properties;
            if (!props.IsLeftButtonPressed
                && e.Pointer.PointerDeviceType.ToString() == "Mouse")
                return;
            // Press on the box being edited: caret business, not selection
            // (the TextBox keeps focus; GotFocus anchors the primary).
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox focused && IsIn(chip, focused))
                return;
            var container = FindListViewItem(chip);
            if (container is null) return;
            var index = LineList.IndexFromContainer(container);
            if (index < 0 || index >= _store.Document.Lines.Count) return;
            var line = _store.Document.Lines[index];
            var (ctrl, shift) = PointerModifiers(e);
            // Select-first only for a plain press on an unselected word
            // (instant feedback + focus for keys). Modified presses and
            // presses inside the selection defer to release/dragstart, so a
            // multi-selection survives until the click completes or the drag
            // starts — the original's pointerdown/up split.
            if (!ctrl && !shift && !_selection.SelectedWordIds.Contains(wordId))
            {
                _selection.SelectWord(line.Id, wordId);
                container.Focus(FocusState.Programmatic);
            }
            ArmDrag(words: true, lineId: line.Id, wordId: wordId,
                point: e.GetCurrentPoint(LineList).Position, ctrl: ctrl, shift: shift);
            e.Handled = true;
        }

        // Click completion lives at page level (OnDragPointerReleased):
        // releases off-element must still resolve, and exactly one gesture
        // may run per press (per-element + page handlers would double-fire
        // toggles).

        internal bool Test_WordGesture(int line, int word, bool ctrl, bool shift)
        {
            if (_store is null || _selection is null) return false;
            if (line < 0 || line >= _store.Document.Lines.Count) return false;
            var docLine = _store.Document.Lines[line];
            if (word < 0 || word >= docLine.Words.Count) return false;
            CommitActiveBox();
            WordPressGesture(docLine, line, docLine.Words[word].Id, ctrl, shift);
            if (LineList.ContainerFromIndex(line) is ListViewItem container)
                container.Focus(FocusState.Programmatic);
            return true;
        }

        private void WordPressGesture(EditLine line, int index, string wordId, bool ctrl, bool shift)
        {
            if (_selection is null) return;
            if (ctrl && !shift)
            {
                // Multi-select: toggle the word, keep the line selected.
                _selection.ToggleWord(line.Id, wordId);
                _selection.RetainWords(LineOfWord);
            }
            else if (shift)
            {
                // Range within this line over the already-selected words of
                // the line plus the clicked index (original); lines untouched.
                var order = line.Words.ToList();
                var clicked = order.FindIndex(w => w.Id == wordId);
                if (clicked < 0) return;
                var bounds = order
                    .Select((w, i) => (w, i))
                    .Where(t => _selection.SelectedWordIds.Contains(t.w.Id))
                    .Select(t => t.i).ToList();
                bounds.Add(clicked);
                var range = order.Skip(bounds.Min()).Take(bounds.Max() - bounds.Min() + 1)
                    .Select(w => w.Id);
                _selection.SelectWordRange(line.Id, range, wordId);
            }
            else
            {
                _selection.SelectWord(line.Id, wordId);
            }
        }

        // Primary indices ("lineIdx,wordIdx", -1 when none) for the hooks.
        internal string Test_Primary()
        {
            if (_store is null || _selection is null) return "nostore";
            var lines = _store.Document.Lines.ToList();
            var li = _selection.SelectedLineId is string lid
                ? lines.FindIndex(l => l.Id == lid) : -1;
            var wi = -1;
            if (li >= 0 && _selection.SelectedWordId is string wid)
                wi = lines[li].Words.ToList().FindIndex(w => w.Id == wid);
            return $"{li},{wi}";
        }

        // Selected words as "lineIdx:wordIdx,..." in document order.
        internal string Test_SelectedWordIdx()
        {
            if (_store is null || _selection is null) return "nostore";
            var parts = new List<string>();
            var lines = _store.Document.Lines.ToList();
            for (var li = 0; li < lines.Count; li++)
                for (var wi = 0; wi < lines[li].Words.Count; wi++)
                    if (_selection.SelectedWordIds.Contains(lines[li].Words[wi].Id))
                        parts.Add($"{li}:{wi}");
            return string.Join(",", parts);
        }

        // Realized chips showing the selection outline, "lineIdx:wordIdx".
        internal string Test_WordIndicated()
        {
            if (_store is null) return "nostore";
            var parts = new List<string>();
            var lines = _store.Document.Lines.ToList();
            for (var li = 0; li < lines.Count; li++)
            {
                if (LineList.ContainerFromIndex(li) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid card) continue;
                if (card.FindName("ChipPanel") is not LyricWrapPanel panel) continue;
                for (var wi = 0; wi < lines[li].Words.Count; wi++)
                {
                    var wordId = lines[li].Words[wi].Id;
                    foreach (var child in panel.Children)
                    {
                        if (child is Grid chip && chip.Tag is string tag && tag == wordId
                            && chip.Children.Count >= 4 && chip.Children[3] is Border outline
                            && outline.Visibility == Visibility.Visible)
                            parts.Add($"{li}:{wi}");
                    }
                }
            }
            return string.Join(",", parts);
        }

        // Contrast probe for the selected-line look: composites the on-screen
        // selected fill with ALPHA-CORRECT math and reports the WCAG ratio of
        // the chip text color against it. Inputs: the live accent off a Pill
        // fill and the live text off a chip foreground (both element-resolved,
        // opaque, theme-correct); card + page bases from ThemeDictionaries
        // resolved by the live theme ("Default" holds the dark values —
        // Application.Current.Resources top-level lookups are NOT
        // theme-resolved and must never be used). Card brushes are translucent
        // layered colors, so alpha-blind brush math lied (1.23 on a visibly
        // dark card), and headless renders sit on transparency instead of the
        // page/Mica, so raw pixel reads lied too (2.51, pill-dominated).
        // fill = 20% accent over the card rendered on the page, exactly what
        // the SelectionTint overlay (Opacity 0.2) composites on screen.
        internal string Test_SelectionContrast()
        {
            static string Hex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
            static double Lin(byte ch)
            {
                var s = ch / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            static double Lum(byte r, byte g, byte b) =>
                0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
            static (byte R, byte G, byte B) Over(Windows.UI.Color fg, (byte R, byte G, byte B) bg)
            {
                var a = fg.A / 255.0;
                return ((byte)Math.Round(fg.R * a + bg.R * (1 - a)),
                    (byte)Math.Round(fg.G * a + bg.G * (1 - a)),
                    (byte)Math.Round(fg.B * a + bg.B * (1 - a)));
            }
            static Windows.UI.Color DictColor(ResourceDictionary dict, string key)
            {
                if (dict.TryGetValue(key, out var v) && v is Windows.UI.Color c) return c;
                if (dict.TryGetValue(key + "Brush", out var bv) && bv is SolidColorBrush b) return b.Color;
                return Windows.UI.Color.FromArgb(255, 0, 0, 0);
            }
            if (_store is null || _selection is null) return "nostore";
            if (_store.Document.Lines.Count == 0) return "empty";
            var hc = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
            var dictName = hc ? "HighContrast" : ActualTheme == ElementTheme.Light ? "Light" : "Default";
            // ThemeDictionaries live on the merged XamlControlsResources, not
            // on the app dictionary itself: walk the merge tree.
            ResourceDictionary? FindThemeDict(ResourceDictionary root)
            {
                if (root.ThemeDictionaries.TryGetValue(dictName, out var hit) && hit is ResourceDictionary d)
                    return d;
                foreach (var m in root.MergedDictionaries)
                {
                    var found = FindThemeDict(m);
                    if (found is not null) return found;
                }
                return null;
            }
            if (FindThemeDict(Application.Current.Resources) is not ResourceDictionary dict)
                return $"nodict:{dictName}";
            var cardC = DictColor(dict, "CardBackgroundFillColorDefault");
            var pageC = DictColor(dict, "SolidBackgroundFillColorBase");
            var page = (pageC.R, pageC.G, pageC.B);
            var card = Over(cardC, page);
            // Live accent + live text off realized elements.
            byte ar = 0, ag = 0, ab2 = 0;
            byte tr = 0, tg = 0, tb = 0;
            var haveAccent = false;
            var haveText = false;
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                var (_, pill, _) = FindCardChrome(container);
                if (!haveAccent && pill?.Fill is SolidColorBrush pb)
                {
                    ar = pb.Color.R; ag = pb.Color.G; ab2 = pb.Color.B;
                    haveAccent = true;
                }
                if (!haveText && container.ContentTemplateRoot is Grid rootCard
                    && rootCard.FindName("ChipPanel") is LyricWrapPanel panel)
                {
                    foreach (var child in panel.Children)
                    {
                        if (child is Grid chip && chip.Children.Count >= 1
                            && chip.Children[0] is TextBox textBox
                            && textBox.Foreground is SolidColorBrush fgb)
                        {
                            tr = fgb.Color.R; tg = fgb.Color.G; tb = fgb.Color.B;
                            haveText = true;
                            break;
                        }
                    }
                }
                if (haveAccent && haveText) break;
            }
            if (!haveAccent) return "noaccent";
            if (!haveText) return "notext";
            var fr = (byte)Math.Round(card.R * 0.8 + ar * 0.2);
            var fg = (byte)Math.Round(card.G * 0.8 + ag * 0.2);
            var fb = (byte)Math.Round(card.B * 0.8 + ab2 * 0.2);
            var l1 = Lum(tr, tg, tb);
            var l2 = Lum(fr, fg, fb);
            var ratio = (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
            return $"theme={ActualTheme} dict={dictName} accent={Hex(ar, ag, ab2)} " +
                $"card={Hex(card.R, card.G, card.B)} text={Hex(tr, tg, tb)} " +
                $"fill={Hex(fr, fg, fb)} ratio={F2(ratio)}";
        }

        // Which look a realized card shows ("selected" = tint + bar on,
        // "default" = both off). The card Background itself is never touched.
        internal string Test_LineFill(int line)
        {
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            var (_, pill, tint) = FindCardChrome(container);
            if (pill is null || tint is null) return "nochrome";
            return pill.Visibility == Visibility.Visible && tint.Visibility == Visibility.Visible
                ? "selected" : "default";
        }

        // Outline state of one chip ("shown"/"hidden"/"nochip").
        internal string Test_ChipOutline(int line, int word)
        {
            if (_store is null) return "nostore";
            if (line < 0 || line >= _store.Document.Lines.Count) return "noline";
            var docLine = _store.Document.Lines[line];
            if (word < 0 || word >= docLine.Words.Count) return "noword";
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return "nopanel";
            var wordId = docLine.Words[word].Id;
            foreach (var child in panel.Children)
                if (child is Grid chip && chip.Tag is string tag && tag == wordId
                    && chip.Children.Count >= 4 && chip.Children[3] is Border outline)
                    return outline.Visibility == Visibility.Visible ? "shown" : "hidden";
            return "nochip";
        }

        // Chip contrast probe: text vs 15%-accent wash over the chip base.
        // Mirrors Test_SelectionContrast math so the word wash (thinner than
        // the 20% line tint) is pinned to the same >= 4.5:1 bar.
        internal string Test_ChipContrast(int line, int word)
        {
            static double Lin(byte ch)
            {
                var s = ch / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            static double Lum(byte r, byte g, byte b) =>
                0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
            if (_store is null) return "nostore";
            if (line < 0 || line >= _store.Document.Lines.Count) return "noline";
            var docLine = _store.Document.Lines[line];
            if (word < 0 || word >= docLine.Words.Count) return "noword";
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return "nopanel";
            var wordId = docLine.Words[word].Id;
            foreach (var child in panel.Children)
            {
                if (child is not Grid chip || chip.Tag is not string tag || tag != wordId) continue;
                if (chip.Children.Count < 6) return "nochildren";
                if (chip.Children[0] is not TextBox tb || tb.Foreground is not SolidColorBrush fg) return "notext";
                if (chip.Children[5] is not Border wash || wash.Background is not SolidColorBrush wb) return "nowash";
                // Chip base = the card fill as rendered: the card dictionary
                // color is TRANSLUCENT (theme-debug shows alpha 0x0D — a layer
                // over the page), so composite card-over-page first (same as
                // Test_SelectionContrast), then the 15% wash over that.
                static (byte R, byte G, byte B) Over(Windows.UI.Color fg, (byte R, byte G, byte B) bg)
                {
                    var a = fg.A / 255.0;
                    return ((byte)Math.Round(fg.R * a + bg.R * (1 - a)),
                        (byte)Math.Round(fg.G * a + bg.G * (1 - a)),
                        (byte)Math.Round(fg.B * a + bg.B * (1 - a)));
                }
                var cardC = ResolveThemeColor("CardBackgroundFillColorDefault");
                var pageC = ResolveThemeColor("SolidBackgroundFillColorBase");
                var page = (pageC.R, pageC.G, pageC.B);
                var cardBase = Over(cardC, page);
                var fr = (byte)Math.Round(cardBase.R * 0.85 + wb.Color.R * 0.15);
                var fg2 = (byte)Math.Round(cardBase.G * 0.85 + wb.Color.G * 0.15);
                var fb = (byte)Math.Round(cardBase.B * 0.85 + wb.Color.B * 0.15);
                var l1 = Lum(fg.Color.R, fg.Color.G, fg.Color.B);
                var l2 = Lum(fr, fg2, fb);
                var ratio = (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
                return $"theme={ActualTheme} text=#{fg.Color.R:X2}{fg.Color.G:X2}{fg.Color.B:X2} " +
                    $"wash=#{wb.Color.R:X2}{wb.Color.G:X2}{wb.Color.B:X2} " +
                    $"card=#{cardBase.R:X2}{cardBase.G:X2}{cardBase.B:X2} ratio={F2(ratio)}";
            }
            return "nochip";
        }

        // Edit-state probe: where focus sits ("chipbox li:wi:M/R",
        // "linebox li:T/R", "item li", "box", "none").
        internal string Test_FocusState()
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot);
            if (focused is TextBox box)
            {
                if (box.Tag is string wordId && _store is not null)
                {
                    var lines = _store.Document.Lines.ToList();
                    for (var li = 0; li < lines.Count; li++)
                    {
                        var wi = lines[li].Words.ToList().FindIndex(w => w.Id == wordId);
                        if (wi >= 0)
                            return $"chipbox {li}:{wi}:{(Grid.GetRow(box) == 1 ? "R" : "M")}";
                    }
                }
                if (FindListViewItem(box) is ListViewItem container)
                {
                    var li = LineList.IndexFromContainer(container);
                    return $"linebox {li}";
                }
                return "box";
            }
            if (focused is ListViewItem item)
                return $"item {LineList.IndexFromContainer(item)}";
            return "none";
        }

        internal bool Test_EnterWordEdit(int line, int word, bool roman)
        {
            if (_store is null) return false;
            if (line < 0 || line >= _store.Document.Lines.Count) return false;
            var docLine = _store.Document.Lines[line];
            if (word < 0 || word >= docLine.Words.Count) return false;
            return EnterWordEdit(docLine.Words[word].Id, roman);
        }



        // Right-click simulation (hook): runs the same select-first +
        // menu-build as the ContextFlyout Opening handler. Zones cover the
        // six line-card positions (all share the card handler; the zone is
        // recorded for coverage): 0 number, 1 chip gap, 2 trailing area,
        // 3 badges, 4 next to X, 5 card edge.
        internal string Test_LineContext(int line, int zone)
        {
            if (_store is null || _selection is null) return "nostore";
            if (line < 0 || line >= _store.Document.Lines.Count) return "noline";
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            EnsureLineSelectedForContext(card);
            var selIdx = _store.Document.Lines
                .Select((l, i) => (l, i))
                .Where(t => _selection.SelectedLineIds.Contains(t.l.Id))
                .Select(t => t.i);
            var labels = BuildLineMenuItems(line, _store.Document.Lines[line].Id)
                .OfType<MenuFlyoutItem>().Select(i => i.Text);
            return $"zone:{zone};sel:{string.Join(",", selIdx)};items:{string.Join("|", labels)}";
        }

        internal string Test_WordContext(int line, int word)
        {
            if (_store is null || _selection is null) return "nostore";
            if (line < 0 || line >= _store.Document.Lines.Count) return "noline";
            var docLine = _store.Document.Lines[line];
            if (word < 0 || word >= docLine.Words.Count) return "noword";
            var lineId = docLine.Id;
            var wordId = docLine.Words[word].Id;
            EnsureWordSelectedForContext(lineId, wordId);
            var selW = Test_SelectedWordIdx();
            var labels = BuildWordMenuItems(lineId, wordId)
                .OfType<MenuFlyoutItem>().Select(i => i.Text);
            return $"sel:{selW};items:{string.Join("|", labels)}";
        }

        // Hit-testability: Transparent (not null) backgrounds on the card
        // root and the chip panel, so number/gaps/trailing/badges all open
        // the line menu ("card:1;panel:1").
        internal string Test_CardHittestable(int line)
        {
            if (LineList.ContainerFromIndex(line) is not ListViewItem container) return "unrealized";
            if (container.ContentTemplateRoot is not Grid card) return "nocard";
            var cardHit = card.Background is not null ? 1 : 0;
            var panelHit = card.FindName("ChipPanel") is LyricWrapPanel panel
                && panel.Background is not null ? 1 : 0;
            return $"card:{cardHit};panel:{panelHit}";
        }

        // Line gestures for the hook (service-only; SelectionMode=None so no
        // native ListView pipeline exists — the same code the real pointer
        // release path runs through ShiftSelectLineRange).
        internal bool Test_LineGesture(int index, bool ctrl, bool shift)
        {
            if (_store is null || _selection is null) return false;
            if (index < 0 || index >= _store.Document.Lines.Count) return false;
            CommitActiveBox();
            var line = _store.Document.Lines[index];
            if (!ctrl && !shift)
            {
                _selection.PinLine(line.Id, LineOfWord);
            }
            else if (ctrl)
            {
                // Converge now (see SelectLines: the event repeats this).
                _selection.ToggleLine(line.Id);
                _selection.RetainWords(LineOfWord);
            }
            else
            {
                var anchor = _selection.AnchorLineId ?? _selection.SelectedLineId;
                var anchorIdx = anchor is string aid
                    ? _store.Document.Lines.ToList().FindIndex(l => l.Id == aid)
                    : -1;
                if (anchorIdx < 0)
                {
                    _selection.PinLine(line.Id, LineOfWord);
                    if (LineList.ContainerFromIndex(index) is ListViewItem container0)
                        container0.Focus(FocusState.Programmatic);
                    return true;
                }
                var lo = Math.Min(anchorIdx, index);
                var hi = Math.Max(anchorIdx, index);
                _selection.SelectLineRange(
                    _store.Document.Lines.Skip(lo).Take(hi - lo + 1).Select(l => l.Id),
                    line.Id);
            }
            if (LineList.ContainerFromIndex(index) is ListViewItem container)
                container.Focus(FocusState.Programmatic);
            return true;
        }

        private string? LineOfWord(string wordId) =>
            _store?.Document.Lines
                .FirstOrDefault(l => l.Words.Any(w => w.Id == wordId))?.Id;

        // Hook for list-key shortcuts (same HandleListKey path as real keys).
        internal bool Test_HandleListKey(Windows.System.VirtualKey key, bool ctrl, bool alt, bool shift) =>
            HandleListKey(key, ctrl, alt, shift);

        // Step-4 regression pin: NO element inside the Edit page subtree may
        // own a KeyboardAccelerator (a container-owned accelerator with the
        // default PlacementMode=Auto is exactly what sprayed "Ctrl+O"
        // tooltips over chips/cards/ribbon fields). The 8 real accelerators
        // live on ShellPage with PlacementMode=Hidden (see
        // Test_AcceleratorPlacements). Returns "ok:N" (N nodes walked) or
        // "leak:<type>@<name>" for the first offender.
        internal string Test_NoLocalAccelerators()
        {
            var count = 0;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(LineList);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                count++;
                if (current is UIElement el && el.KeyboardAccelerators.Count > 0)
                {
                    var name = (current as FrameworkElement)?.Name ?? "";
                    return $"leak:{current.GetType().Name}@{name}";
                }
                var n = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < n; i++)
                    queue.Enqueue(VisualTreeHelper.GetChild(current, i));
            }
            return $"ok:{count}";
        }

        // With SelectionMode=None the ListView never raises SelectionChanged:
        // this stays only so old hook callers fail loudly at compile time if
        // re-added. All selection flows through the SelectionService.
        private void OnLineSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        // List-scoped keys (KeyDown, NOT accelerators: a focused TextBox
        // marks text-editing keys handled, so typing never triggers these).
        private void OnLineListKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (HandleListKey(e.Key, IsCtrlDown(), IsAltDown(), IsShiftDown()))
                e.Handled = true;
        }

        // Factored for the Test_ hook so every list shortcut is covered
        // deterministically (real key routing is framework behavior).
        internal bool HandleListKey(Windows.System.VirtualKey key, bool ctrl, bool alt, bool shift = false)
        {
            if (_store is null || _selection is null) return false;
            // Esc mid-drag cancels the drag (it must not clear selection).
            if (key == Windows.System.VirtualKey.Escape && _drag is not null && _drag.Started)
            {
                CancelDrag();
                return true;
            }
            if (FocusManager.GetFocusedElement(XamlRoot) is TextBox) return false;
            if (key == Windows.System.VirtualKey.Delete)
            {
                DeleteSelected();
                return true;
            }
            if (key == Windows.System.VirtualKey.Escape)
            {
                // Clear both (the service alone would leave a stale
                // ListView selection behind for multi-ops to trip on).
                ClearEditSelection();
                return true;
            }
            if (key == Windows.System.VirtualKey.D && ctrl)
            {
                DuplicateSelectedLines();
                return true;
            }
            if (key == Windows.System.VirtualKey.A && ctrl)
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
                return true;
            }
            if ((key == Windows.System.VirtualKey.Up
                || key == Windows.System.VirtualKey.Down) && alt)
            {
                MoveSelectedLines(key == Windows.System.VirtualKey.Up ? -1 : 1);
                return true;
            }
            // Shift+Up/Down extends the line selection from the anchor
            // (native range behavior; pointer Shift+click path converges
            // through the same SelectLineRange).
            if ((key == Windows.System.VirtualKey.Up
                || key == Windows.System.VirtualKey.Down) && shift && !ctrl && !alt)
            {
                ExtendLineSelection(key == Windows.System.VirtualKey.Up ? -1 : 1);
                return true;
            }
            // F2 / Enter enter EDIT on the primary word (double-click path
            // for the keyboard; original has no keyboard edit-entry, so F2
            // — free, since the original binds Ctrl+F2 — is the port key).
            // Enter must not steal button activation (focused Add/Delete
            // buttons keep their default Enter-to-click).
            if (key == Windows.System.VirtualKey.F2)
            {
                return EnterPrimaryWordEdit();
            }
            if (key == Windows.System.VirtualKey.Enter
                && FocusManager.GetFocusedElement(XamlRoot) is not Button)
            {
                return EnterPrimaryWordEdit();
            }
            return false;
        }

        // Shift+Up/Down keyboard range: moves the primary one line and the
        // set becomes anchor..primary in document order (same convergence as
        // Shift+click via SelectLineRange). Anchor defaults to the primary.
        internal bool ExtendLineSelection(int delta)
        {
            if (_store is null || _selection is null) return false;
            var lines = _store.Document.Lines;
            if (lines.Count == 0) return false;
            var ids = lines.Select(l => l.Id).ToList();
            var anchorId = _selection.AnchorLineId ?? _selection.SelectedLineId ?? ids[0];
            var primaryId = _selection.SelectedLineId ?? anchorId;
            var primaryIdx = ids.IndexOf(primaryId);
            if (primaryIdx < 0) primaryIdx = ids.IndexOf(anchorId);
            if (primaryIdx < 0) primaryIdx = 0;
            var next = Math.Clamp(primaryIdx + delta, 0, ids.Count - 1);
            var anchorIdx = ids.IndexOf(anchorId);
            if (anchorIdx < 0) anchorIdx = next;
            var lo = Math.Min(anchorIdx, next);
            var hi = Math.Max(anchorIdx, next);
            var range = ids.GetRange(lo, hi - lo + 1);
            _selection.SelectLineRange(range, ids[next]);
            if (LineList.ContainerFromIndex(next) is ListViewItem container)
                container.Focus(FocusState.Programmatic);
            return true;
        }

        // Focuses the primary word's box for editing (primary line's first
        // word when only lines are selected). Selection is left alone.
        internal bool EnterPrimaryWordEdit()
        {
            if (_store is null || _selection is null) return false;
            var lines = _store.Document.Lines;
            var lineIdx = lines.ToList().FindIndex(l => l.Id == _selection.SelectedLineId);
            if (lineIdx < 0) return false;
            var line = lines[lineIdx];
            if (line.Words.Count == 0) return false;
            var wordIdx = line.Words.ToList().FindIndex(w => w.Id == _selection.SelectedWordId);
            if (wordIdx < 0) wordIdx = 0;
            return EnterWordEdit(line.Words[wordIdx].Id, roman: false);
        }

        // --- structure ops (each Execute = one history step) ----------------

        // Realized line-id -> Y position snapshot (for move FLIP).
        private Dictionary<string, double> SnapshotLineTops()
        {
            var tops = new Dictionary<string, double>();
            if (_store is null) return tops;
            var lines = _store.Document.Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                try
                {
                    tops[lines[i].Id] = container.TransformToVisual(LineList)
                        .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                }
                catch { }
            }
            return tops;
        }

        // Composition-thread FLIP for a line move/drop: affected VISIBLE
        // cards glide from their old offset to the new layout in ~175ms with
        // the Fluent decelerate (no per-frame layout work). More than ~12
        // moving cards snap + flash instead. Animations-off snaps. The
        // ListView RepositionThemeTransition is parked during the op so the
        // heavy TextBox cards are never layout-animated.
        private void RunWithMoveFlip(Action op)
        {
            if (_store is null) { op(); return; }
            var animate = AnimationsOn() && _compositor is not null && _decelerate is not null;
            var saved = LineList.ItemContainerTransitions;
            var before = SnapshotLineTops();
            try
            {
                if (animate) LineList.ItemContainerTransitions = new TransitionCollection();
                op();
                LineList.UpdateLayout();
            }
            finally
            {
                LineList.ItemContainerTransitions = saved;
            }
            if (!animate) return;
            var moved = new List<(FrameworkElement El, float Dy)>();
            var lines = _store.Document.Lines.ToList();
            foreach (var (id, oldY) in before)
            {
                var at = lines.FindIndex(l => l.Id == id);
                if (at < 0 || LineList.ContainerFromIndex(at) is not ListViewItem container) continue;
                double newY;
                try
                {
                    newY = container.TransformToVisual(LineList)
                        .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                }
                catch { continue; }
                var dy = (float)(oldY - newY);
                if (Math.Abs(dy) >= 0.5) moved.Add((container, dy));
            }
            if (moved.Count == 0) return;
            if (moved.Count > 12)
            {
                // Snap + brief highlight pulse on the moved cards.
                foreach (var (el, _) in moved)
                {
                    var visual = ElementCompositionPreview.GetElementVisual(el);
                    visual.StopAnimation("Opacity");
                    var pulse = _compositor!.CreateScalarKeyFrameAnimation();
                    pulse.Duration = TimeSpan.FromMilliseconds(200);
                    pulse.InsertKeyFrame(0f, 1f);
                    pulse.InsertKeyFrame(0.5f, 0.55f, _decelerate);
                    pulse.InsertKeyFrame(1f, 1f, _decelerate);
                    visual.StartAnimation("Opacity", pulse);
                }
                return;
            }
            foreach (var (el, dy) in moved)
            {
                var visual = ElementCompositionPreview.GetElementVisual(el);
                visual.StopAnimation("Offset");
                var baseOff = visual.Offset;
                visual.Offset = new Vector3(baseOff.X, baseOff.Y + dy, baseOff.Z);
                var glide = _compositor!.CreateVector3KeyFrameAnimation();
                glide.Duration = TimeSpan.FromMilliseconds(175);
                glide.InsertKeyFrame(1f, baseOff, _decelerate);
                visual.StartAnimation("Offset", glide);
            }
        }

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
                ClearEditSelection();
                return;
            }
            var lineIds = _selection.SelectedLineIds.ToList();
            if (lineIds.Count == 0) return;
            var firstIndex = _store.Document.Lines
                .Select((l, i) => (l, i)).Where(t => lineIds.Contains(t.l.Id))
                .Select(t => t.i).DefaultIfEmpty(0).Min();
            _store.Execute(new RemoveLinesCommand(lineIds));
            ClearEditSelection();
            // Selection moves to the neighboring line.
            var neighbor = Math.Min(firstIndex, _store.Document.Lines.Count - 1);
            if (neighbor >= 0) _selection.SelectLine(_store.Document.Lines[neighbor].Id);
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
            ClearEditSelection();
            var neighbor = Math.Min(index, _store.Document.Lines.Count - 1);
            if (neighbor >= 0) _selection.SelectLine(_store.Document.Lines[neighbor].Id);
        }

        internal void InsertLineBefore(int index)
        {
            if (_store is null || _selection is null) return;
            CommitActiveBox();
            index = Math.Clamp(index, 0, _store.Document.Lines.Count);
            var line = EditLine.New();
            _store.Execute(new AddLineCommand(index, line));
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

        internal void MovePrimaryLine(int delta) => MoveSelectedLines(delta);

        // Moves the whole selected block (one undo step). Single selection
        // behaves exactly like the old primary-only move. Sequential moves
        // in a safe order (top-first up, bottom-first down) keep precomputed
        // targets valid: removing a line shifts its followers back, and its
        // own insert shifts them forward again (net same index).
        internal void MoveSelectedLines(int delta)
        {
            if (_store is null || _selection is null) return;
            if (delta != -1 && delta != 1) return;
            CommitActiveBox();
            var lines = _store.Document.Lines.ToList();
            var idx = lines
                .Select((l, i) => (l, i))
                .Where(t => _selection.SelectedLineIds.Contains(t.l.Id))
                .Select(t => t.i).OrderBy(i => i).ToList();
            if (idx.Count == 0) return;
            if (delta < 0 && idx[0] == 0) return;
            if (delta > 0 && idx[^1] == lines.Count - 1) return;
            if (idx.Count == 1)
            {
                var id = lines[idx[0]].Id;
                var to = Math.Clamp(idx[0] + delta, 0, lines.Count - 1);
                if (to == idx[0]) return;
                RunWithMoveFlip(() => _store.Execute(new MoveLineCommand(id, to)));
                LineList.ScrollIntoView(_store.Document.Lines[to]);
                return;
            }
            var order = delta < 0 ? idx : idx.AsEnumerable().Reverse().ToList();
            var cmds = order
                .Select(i => (ILyricCommand)new MoveLineCommand(lines[i].Id, i + delta))
                .ToList();
            RunWithMoveFlip(() => _store.Execute(new CompositeCommand("Move lines", cmds)));
            if (_selection.SelectedLineId is string primary)
            {
                var at = _store.Document.Lines.ToList().FindIndex(l => l.Id == primary);
                if (at >= 0) LineList.ScrollIntoView(_store.Document.Lines[at]);
            }
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
            // Service-only (see CommitInsertBox: no phantom lines).
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
