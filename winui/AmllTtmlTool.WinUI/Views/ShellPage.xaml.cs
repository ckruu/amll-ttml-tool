using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;

namespace AmllTtmlTool.WinUI.Views
{
    public sealed partial class ShellPage : Page
    {
        private readonly Type[] _pages = [typeof(EditPage), typeof(SyncPage), typeof(PreviewPage)];

        public ShellPage()
        {
            this.InitializeComponent();
            SetupWindow();
            NavView.SelectedItem = NavView.MenuItems[0];
            _ = ContentFrame.Navigate(typeof(EditPage));
        }

        private void SetupWindow()
        {
            var window = ((App)Application.Current).MainWindow;
            window.SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
            window.ExtendsContentIntoTitleBar = true;
            if (window.AppWindow is AppWindow appWindow)
            {
                appWindow.Title = "AMLL TTML Tool";
            }
        }

        private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                var index = tag switch { "Edit" => 0, "Sync" => 1, "Preview" => 2, _ => 0 };
                _ = ContentFrame.Navigate(_pages[index]);
            }
        }
    }
}
