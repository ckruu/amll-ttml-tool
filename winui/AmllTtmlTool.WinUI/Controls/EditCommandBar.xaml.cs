using AmllTtmlTool.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AmllTtmlTool.WinUI.Controls
{
    public sealed partial class EditCommandBar : UserControl
    {
        public event Action? NewLineRequested;
        public event Action? ImportRequested;

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
            var transVisibility = showTrans ? Visibility.Visible : Visibility.Collapsed;
            var romanVisibility = showRoman ? Visibility.Visible : Visibility.Collapsed;
            TransLabel.Visibility = TransBox.Visibility = transVisibility;
            RomanLabel.Visibility = RomanBox.Visibility = romanVisibility;
            // The flyout holds separate copies (same text via TwoWay binding),
            // so mirror the same visibility here. This also runs while the
            // flyout is open, since DisplayChanged fires on toggle change.
            FlyoutTransLabel.Visibility = FlyoutTransBox.Visibility = transVisibility;
            FlyoutRomanLabel.Visibility = FlyoutRomanBox.Visibility = romanVisibility;
            var showSection = showTrans || showRoman;
            // The tier setters collapse SecondaryGroup below Wide, but this
            // handler re-runs on every toggle change and tier change, so the
            // group is only ever shown in the Wide tier. The overflow button
            // is the single owner of its own visibility: shown exactly when
            // the section has content but lives in the flyout.
            var wide = WidthTiers?.CurrentState?.Name is null or "Wide";
            SecondaryGroup.Visibility = showSection && wide ? Visibility.Visible : Visibility.Collapsed;
            SecondaryOverflowButton.Visibility =
                showSection && !wide ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnWidthTierChanged(object sender, VisualStateChangedEventArgs e) =>
            ApplyMasterToggles();

        private void OnNewLineClick(object sender, RoutedEventArgs e) =>
            NewLineRequested?.Invoke();

        private void OnImportClick(object sender, RoutedEventArgs e) =>
            ImportRequested?.Invoke();

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
