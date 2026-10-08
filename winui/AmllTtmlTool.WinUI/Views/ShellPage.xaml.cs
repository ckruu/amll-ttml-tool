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
            EditModeButton.IsChecked = true;
            SidePane.SelectedItem = SidePane.MenuItems[0];
        }

        private void SetupWindow()
        {
            var window = ((App)Application.Current).MainWindow;
            window.SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(TitleBarArea);
            if (window.AppWindow is AppWindow appWindow)
            {
                appWindow.Title = "AMLL TTML Tool";
            }
        }

        private void OnModeChecked(object sender, RoutedEventArgs args)
        {
            if (sender is RadioButton button)
            {
                ModeSwitch_Checked(button);
            }
        }

        private void ModeSwitch_Checked(RadioButton button)
        {
            var index = button == SyncModeButton ? 1 : button == PreviewModeButton ? 2 : 0;
            EditBar.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncBar.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            PreviewBar.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
            _ = ContentFrame.Navigate(_pages[index]);
        }

        private void OnSideTabChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            {
                SideContent.Text = tag switch
                {
                    "Outline" => "Outline lands here in Phase 1.",
                    "BPM" => "BPM panel lands here in Phase 1.",
                    _ => "Mini preview lands here in Phase 1.",
                };
            }
        }
    }
}
