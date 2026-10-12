using AmllTtmlTool.Core.Lyrics;
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

        private void OnStoreChanged()
        {
            if (DispatcherQueue.HasThreadAccess) RefreshEmptyState();
            else DispatcherQueue.TryEnqueue(RefreshEmptyState);
        }

        private void RefreshEmptyState()
        {
            var empty = _store is null || _store.Document.Lines.Count == 0;
            EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            LineList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnImportClick(object sender, RoutedEventArgs e) =>
            ImportRequested?.Invoke();

        // Line numbers, chip units, per-chip automation and the trailing "+"
        // are applied here (they need container indices, which templates
        // cannot see). Render-only: text is set once per realization; no
        // edits happen in this phase, so no live rebinding is needed.
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
            AutomationProperties.SetName(args.ItemContainer, $"Line {index + 1}");
            if (card.FindName("ChipPanel") is not LyricWrapPanel panel) return;
            panel.Children.Clear();
            for (var i = 0; i < line.Words.Count; i++)
                panel.Children.Add(BuildChip(line.Words[i], i));
            var add = new Button
            {
                Content = new FontIcon { FontSize = 14, Glyph = "\uE710" },
                IsEnabled = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(add, "Add word (editing arrives in the next update)");
            AutomationProperties.SetName(add, $"Add word to line {index + 1}");
            panel.Children.Add(add);
            if (card.FindName("DeleteButton") is Button delete)
                AutomationProperties.SetName(delete, $"Delete line {index + 1}");
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
            var showTrans = _viewState.ShowTranslation;
            var showRoman = _viewState.ShowRomanization;
            var showWordRoman = _viewState.ShowWordRomanization;
            for (var i = 0; i < _store.Document.Lines.Count; i++)
            {
                if (LineList.ContainerFromIndex(i) is not ListViewItem container) continue;
                if (container.ContentTemplateRoot is not Grid card) continue;
                ApplyCardVisibility(card, showTrans, showRoman, showWordRoman, animate, collapseNow: false);
            }
            AnchorSelected(animate: false);
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
            foreach (var el in hiding) el.Visibility = Visibility.Collapsed;
            AnchorSelected(animate: false);
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

        private Grid BuildChip(EditWord word, int index)
        {
            var chip = new Grid
            {
                MinWidth = 44,
                Tag = word.Id,
            };
            chip.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chip.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var isSpace = word.EmptyBeat > 0 && string.IsNullOrWhiteSpace(word.Text);
            var text = new TextBox
            {
                Text = isSpace ? $"Space \u00D7{word.EmptyBeat}" : word.Text,
                IsReadOnly = true,
                IsTabStop = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var roman = new TextBox
            {
                Text = word.RomanWord,
                IsReadOnly = true,
                IsTabStop = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            Grid.SetRow(roman, 1);
            if (isSpace) chip.Opacity = 0.55;
            AutomationProperties.SetName(text, $"Word {index + 1}: {text.Text}");
            AutomationProperties.SetName(roman, $"Romanization of word {index + 1}");
            // handledEventsToo: read-only TextBoxes mark pointer input handled,
            // which would otherwise starve both this handler and item select.
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
