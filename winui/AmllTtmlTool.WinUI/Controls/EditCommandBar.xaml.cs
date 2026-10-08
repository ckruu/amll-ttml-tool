using AmllTtmlTool.WinUI.Services;

namespace AmllTtmlTool.WinUI.Controls
{
    public sealed partial class EditCommandBar : UserControl
    {
        public event Action? NewLineRequested;

        public EditCommandBar()
        {
            this.InitializeComponent();
            this.Loaded += (_, _) =>
            {
                ApplyMasterToggles();
                AppSettings.DisplayChanged += ApplyMasterToggles;
            };
            this.Unloaded += (_, _) => AppSettings.DisplayChanged -= ApplyMasterToggles;
            OutlineToggle.IsChecked = AppSettings.ShowOutline;
            BpmToggle.IsChecked = AppSettings.ShowBpm;
            PreviewToggle.IsChecked = true;
            FollowToggle.IsChecked = AppSettings.FollowPlayback;
        }

        private void ApplyMasterToggles()
        {
            TransSection.Visibility = AppSettings.ShowTranslation ? Visibility.Visible : Visibility.Collapsed;
            RomanSection.Visibility = AppSettings.ShowRomanization ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnNewLineClick(object sender, RoutedEventArgs e) =>
            NewLineRequested?.Invoke();

        private void OnStubClick(object sender, RoutedEventArgs e)
        {
        }

        private void OnObsceneClick(object sender, RoutedEventArgs e) =>
            ObsceneToggle.Content = ObsceneToggle.IsChecked == true ? "Yes" : "No";

        private void OnViewToggleClick(object sender, RoutedEventArgs e)
        {
            AppSettings.ShowOutline = OutlineToggle.IsChecked == true;
            AppSettings.ShowBpm = BpmToggle.IsChecked == true;
            AppSettings.FollowPlayback = FollowToggle.IsChecked == true;
        }
    }
}
