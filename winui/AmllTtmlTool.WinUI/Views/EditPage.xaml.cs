using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.WinUI.Controls;
using AmllTtmlTool.WinUI.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AmllTtmlTool.WinUI.Views
{
    public sealed partial class EditPage : Page
    {
        public event Action? ImportRequested;

        private LyricStore? _store;
        private SelectionService? _selection;

        public EditPage()
        {
            this.InitializeComponent();
            this.Unloaded += (_, _) => Detach();
        }

        // Called by ShellPage on navigation (fresh instance each time).
        public void Attach(LyricStore store, SelectionService selection)
        {
            Detach();
            _store = store;
            _selection = selection;
            _store.Changed += OnStoreChanged;
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
            _selection = null;
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
