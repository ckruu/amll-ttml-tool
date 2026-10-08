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
                OutlineToggle.IsChecked = AppSettings.ShowOutline;
                BpmToggle.IsChecked = AppSettings.ShowBpm;
                PreviewToggle.IsChecked = AppSettings.ShowPreview;
                FollowToggle.IsChecked = AppSettings.FollowPlayback;
                if (ObsceneToggle.IsChecked == true) ObsceneToggle.Content = "Yes";
                else { ObsceneToggle.IsChecked = false; ObsceneToggle.Content = "No"; }
                ApplyMasterToggles();
                AppSettings.DisplayChanged += ApplyMasterToggles;
            };
            this.Unloaded += (_, _) => AppSettings.DisplayChanged -= ApplyMasterToggles;
        }

        private void ApplyMasterToggles()
        {
            var showTrans = AppSettings.ShowTranslation;
            var showRoman = AppSettings.ShowRomanization;
            TransRow.Visibility = showTrans ? Visibility.Visible : Visibility.Collapsed;
            RomanRow.Visibility = showRoman ? Visibility.Visible : Visibility.Collapsed;
            var showSection = showTrans || showRoman;
            SecondaryGroup.Visibility = showSection ? Visibility.Visible : Visibility.Collapsed;
            SecondaryCaption.Visibility = showSection ? Visibility.Visible : Visibility.Collapsed;
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
            AppSettings.ShowPreview = PreviewToggle.IsChecked == true;
            AppSettings.FollowPlayback = FollowToggle.IsChecked == true;
        }
    }
}
