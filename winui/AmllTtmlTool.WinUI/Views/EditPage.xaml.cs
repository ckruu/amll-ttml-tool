using AmllTtmlTool.Core.Lyrics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AmllTtmlTool.WinUI.Views
{
    public sealed partial class EditPage : Page
    {
        public event Action? ImportRequested;

        private LyricStore? _store;

        public EditPage()
        {
            this.InitializeComponent();
            this.Unloaded += (_, _) => Detach();
        }

        // Called by ShellPage on navigation (fresh instance each time).
        public void Attach(LyricStore store)
        {
            Detach();
            _store = store;
            _store.Changed += OnStoreChanged;
            RefreshEmptyState();
        }

        public void Detach()
        {
            if (_store is not null)
            {
                _store.Changed -= OnStoreChanged;
                _store = null;
            }
        }

        public bool IsEmptyStateVisible => EmptyStatePanel.Visibility == Visibility.Visible;

        private void OnStoreChanged()
        {
            if (DispatcherQueue.HasThreadAccess) RefreshEmptyState();
            else DispatcherQueue.TryEnqueue(RefreshEmptyState);
        }

        private void RefreshEmptyState()
        {
            EmptyStatePanel.Visibility =
                _store is null || _store.Document.Lines.Count == 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void OnImportClick(object sender, RoutedEventArgs e) =>
            ImportRequested?.Invoke();
    }
}
