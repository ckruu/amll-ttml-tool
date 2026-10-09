using AmllTtmlTool.Core.Models;
using AmllTtmlTool.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Media;
using Windows.Storage;

namespace AmllTtmlTool.WinUI.Views
{
    public sealed partial class ShellPage : Page
    {
        private readonly Type[] _pages = [typeof(EditPage), typeof(SyncPage), typeof(PreviewPage)];
        private readonly LyricStore _store = new();
        private readonly AudioEngine _audio = new();
        private FileIntake? _intake;
        private bool _scrubbing;
        private bool _syncingSlider;
        private SystemMediaTransportControls? _smtc;

        public ShellPage()
        {
            this.InitializeComponent();
            SetupWindow();
            WireStores();
            ModeNavView.SelectedItem = EditNavItem;
            SidePane.SelectedItem = SidePane.MenuItems[0];
            _ = HandleStartupFileAsync();
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
                appWindow.Closing += OnWindowClosing;
                appWindow.Changed += (_, _) => UpdateCaptionInsets();
                if (AppWindowTitleBar.IsCustomizationSupported())
                {
                    var titleBar = appWindow.TitleBar;
                    titleBar.ExtendsContentIntoTitleBar = true;
                    // Tall caption buttons match the 48px TitleBarArea row.
                    titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
                    // Transparent buttons let the Mica backdrop show through.
                    titleBar.ButtonBackgroundColor = Colors.Transparent;
                    titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                }
            }
            this.Loaded += (_, _) =>
            {
                UpdateCaptionInsets();
                SetupMotion();
                PaintPillToggles(animate: false);
                if (ModeNavView.SelectedItem is null)
                    ModeNavView.SelectedItem = EditNavItem;
                UpdateModeNavCompact(TitleStates.CurrentState == CompactLabels);
            };
        }

        private void UpdateCaptionInsets()
        {
            if (XamlRoot is null) return;
            var window = ((App)Application.Current).MainWindow;
            if (window.AppWindow is not AppWindow appWindow) return;
            var scale = XamlRoot.RasterizationScale;
            if (scale <= 0) return;
            CaptionSpacer.Width = appWindow.TitleBar.RightInset / scale;
        }

        private void WireStores()
        {
            var window = ((App)Application.Current).MainWindow;
            _intake = new FileIntake(_store, _audio, window, this, Notify);
            _store.Changed += RefreshChrome;
            _store.Notice += Notify;
            _audio.PositionChanged += RefreshAudioChrome;
            _audio.TrackEnded += RefreshAudioChrome;
            EditBar.NewLineRequested += () =>
                _store.Mutate(doc => doc with
                {
                    LyricLines = [.. doc.LyricLines, LyricLine.New()],
                });
            RefreshChrome();
            RefreshAudioChrome();
            LoadMasterToggles();
            SetupSmtc();
        }

        private void Notify(string message)
        {
            NoticeBar.Message = message;
            NoticeBar.Severity = InfoBarSeverity.Informational;
            NoticeBar.IsOpen = true;
        }

        private void RefreshChrome()
        {
            FileNameText.Text = _store.FileName + (_store.IsDirty ? " ●" : string.Empty);
            ToolTipService.SetToolTip(FileNameText, _store.FileName);
            UndoButton.IsEnabled = _store.CanUndo;
            RedoButton.IsEnabled = _store.CanRedo;
        }

        private static string Stamp(TimeSpan t) =>
            $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";

        private void RefreshAudioChrome()
        {
            PositionText.Text = Stamp(_audio.Position);
            DurationText.Text = Stamp(_audio.Duration);
            PlayButton.Icon = new SymbolIcon(_audio.IsPlaying ? Symbol.Pause : Symbol.Play);
            PlayButton.IsEnabled = _audio.HasAudio;
            SeekSlider.IsEnabled = _audio.HasAudio;
            if (_audio.HasAudio && _audio.Duration > TimeSpan.Zero && !_scrubbing)
            {
                _syncingSlider = true;
                SeekSlider.Maximum = _audio.Duration.TotalSeconds;
                SeekSlider.Value = Math.Max(0, Math.Min(_audio.Position.TotalSeconds, _audio.Duration.TotalSeconds));
                _syncingSlider = false;
            }
            UpdateSmtc();
        }

        private bool _closeConfirmed;

        private async void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (!_store.IsDirty || _closeConfirmed) return;
            args.Cancel = true;
            if (await FileDialogs.ConfirmAsync(this, "Unsaved changes",
                    "Close without saving?", "Close"))
            {
                _closeConfirmed = true;
                ((App)Application.Current).MainWindow.Close();
            }
        }

        private async Task HandleStartupFileAsync()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args.Length > 1 && File.Exists(args[1]) && _intake is not null)
                {
                    var file = await StorageFile.GetFileFromPathAsync(args[1]);
                    await _intake.OpenAnyAsync(file);
                }
            }
            catch { }
        }

        private async Task<bool> ConfirmDirtyAsync(string action)
        {
            if (!_store.IsDirty) return true;
            return await FileDialogs.ConfirmAsync(this, "Unsaved changes",
                $"{action} and discard unsaved lyric changes?", action);
        }

        private async void OnOpenClick(object sender, RoutedEventArgs e) => await OpenLyricAsync();
        private void OnOpenAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
            _ = OpenLyricAsync();

        private async Task OpenLyricAsync()
        {
            if (_intake is null) return;
            if (!await ConfirmDirtyAsync("Open")) return;
            var window = ((App)Application.Current).MainWindow;
            var file = await FileDialogs.PickOpenLyricAsync(window);
            if (file is not null) await _intake.OpenAnyAsync(file);
        }

        private async void OnNewClick(object sender, RoutedEventArgs e) => await NewFileAsync();
        private void OnNewAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
            _ = NewFileAsync();

        private async Task NewFileAsync()
        {
            if (!await ConfirmDirtyAsync("New file")) return;
            _store.NewDocument();
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e) =>
            await SaveWithFormatAsync(AppSettings.LastSaveFormat);

        private void OnSaveSplitClick(SplitButton sender, SplitButtonClickEventArgs e) =>
            _ = SaveWithFormatAsync(AppSettings.LastSaveFormat);
        private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
            _ = SaveWithFormatAsync(AppSettings.LastSaveFormat);

        private async void OnSaveAppleClick(object sender, RoutedEventArgs e) =>
            await SaveWithFormatAsync("apple");
        private void OnSaveAppleAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
            _ = SaveWithFormatAsync("apple");

        private async void OnCopyClick(object sender, RoutedEventArgs e) =>
            await CopyWithFormatAsync(AppSettings.LastCopyFormat);

        private void OnCopySplitClick(SplitButton sender, SplitButtonClickEventArgs e) =>
            _ = CopyWithFormatAsync(AppSettings.LastCopyFormat);

        private async void OnCopyAppleClick(object sender, RoutedEventArgs e) =>
            await CopyWithFormatAsync("apple");

        private async void OnPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (_intake is not null) await _intake.PasteAsync();
        }

        private void OnUndoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
            _store.Undo();

        private void OnRedoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
            _store.Redo();

        private Task SaveAsync(bool apple) => _intake?.SaveAsync(apple) ?? Task.CompletedTask;

        private Task SaveWithFormatAsync(string format) =>
            SaveAsync(format == "apple");

        private Task CopyWithFormatAsync(string format) =>
            _intake?.CopyTtmlAsync(format == "apple") ?? Task.CompletedTask;

        private async void OnLoadAudioClick(object sender, RoutedEventArgs e)
        {
            if (_intake is null) return;
            var window = ((App)Application.Current).MainWindow;
            var file = await FileDialogs.PickOpenAudioAsync(window);
            if (file is not null) await _intake.OpenAudioFileAsync(file);
        }

        private void OnPlayPauseClick(object sender, RoutedEventArgs e)
        {
            if (_audio.IsPlaying) _audio.Pause();
            else _audio.Play();
            RefreshAudioChrome();
        }

        private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e) =>
            _audio.SetVolume(e.NewValue);

        private void OnRateChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _audio.SetPlaybackRate(e.NewValue);
            RateButton.Content = $"{e.NewValue:0.##}x";
        }

        private void OnSeekPointerPressed(object sender, PointerRoutedEventArgs e) => _scrubbing = true;

        private void OnSeekPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _scrubbing = false;
            _audio.Seek(SeekSlider.Value);
            RefreshAudioChrome();
        }

        private void OnSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncingSlider || !_scrubbing) return;
            _audio.Seek(e.NewValue);
        }

        private void OnLocateClick(object sender, RoutedEventArgs e) =>
            Notify("Line locate arrives with the lyric list.");

        private void OnSpectroClick(object sender, RoutedEventArgs e)
        {
            Notify("Spectrogram arrives in Phase 2.");
            if (SpectroToggle.IsChecked == true) SpectroToggle.IsChecked = false;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                DropOverlay.Visibility = Visibility.Visible;
            }
        }

        private void OnDragLeave(object sender, DragEventArgs e) =>
            DropOverlay.Visibility = Visibility.Collapsed;

        private async void OnDrop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (_intake is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count == 0) return;
                if (items[0] is StorageFile file)
                {
                    if (LyricStore.IsLyricFile(file.Name) && !await ConfirmDirtyAsync("Open")) return;
                    await _intake.OpenAnyAsync(file);
                }
            }
            catch { }
        }

        private void SetupSmtc()
        {
            try
            {
                _smtc = SystemMediaTransportControls.GetForCurrentView();
                _smtc.IsPlayEnabled = true;
                _smtc.IsPauseEnabled = true;
                _smtc.ButtonPressed += (_, args) =>
                {
                    if (args.Button == SystemMediaTransportControlsButton.Play) _audio.Play();
                    else if (args.Button == SystemMediaTransportControlsButton.Pause) _audio.Pause();
                    DispatcherQueue.TryEnqueue(RefreshAudioChrome);
                };
            }
            catch { }
        }

        private void UpdateSmtc()
        {
            if (_smtc is null) return;
            try
            {
                _smtc.PlaybackStatus = !_audio.HasAudio
                    ? MediaPlaybackStatus.Closed
                    : _audio.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                if (_audio.HasAudio && _audio.Duration > TimeSpan.Zero)
                {
                    _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
                    {
                        StartTime = TimeSpan.Zero,
                        EndTime = _audio.Duration,
                        Position = _audio.Position,
                    });
                }
            }
            catch { }
        }

        private void OnModeNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag &&
                int.TryParse(tag, out var index) && index >= 0 && index < _pages.Length)
            {
                SwitchMode(index);
            }
        }

        private int _currentModeIndex;

        private void SwitchMode(int index)
        {
            FrameworkElement incomingBar = index switch
            {
                1 => SyncBar,
                2 => PreviewBar,
                _ => EditBar,
            };
            var forward = index > _currentModeIndex;
            var samePage = _currentModeIndex == index &&
                ContentFrame.CurrentSourcePageType == _pages[index];
            _currentModeIndex = index;

            EditBar.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            SyncBar.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            PreviewBar.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

            if (samePage) return;
            AnimateRibbonBar(incomingBar, fromRight: forward);
            var firstNavigation = ContentFrame.CurrentSourcePageType is null;
            NavigationTransitionInfo info = !_motionEnabled || firstNavigation
                ? new SuppressNavigationTransitionInfo()
                : new SlideNavigationTransitionInfo
                {
                    Effect = forward
                        ? SlideNavigationTransitionEffect.FromRight
                        : SlideNavigationTransitionEffect.FromLeft,
                };
            _ = ContentFrame.Navigate(_pages[index], null, info);
        }

        private void AnimateRibbonBar(FrameworkElement bar, bool fromRight)
        {
            if (!_motionEnabled || _compositor is null || _decelerate is null) return;
            try
            {
                var visual = ElementCompositionPreview.GetElementVisual(bar);
                visual.StopAnimation("Offset");
                visual.StopAnimation("Opacity");
                const float offset = 24f;
                visual.Offset = new Vector3(fromRight ? offset : -offset, 0, 0);
                visual.Opacity = 0f;
                var slide = _compositor.CreateVector3KeyFrameAnimation();
                slide.Duration = TimeSpan.FromMilliseconds(200);
                slide.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                slide.InsertKeyFrame(1f, Vector3.Zero, _decelerate);
                visual.StartAnimation("Offset", slide);
                var fade = _compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = TimeSpan.FromMilliseconds(150);
                fade.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                fade.InsertKeyFrame(1f, 1f, _decelerate);
                visual.StartAnimation("Opacity", fade);
            }
            catch { }
        }

        private readonly Dictionary<NavigationViewItem, object?> _modeLabels = new();

        private void OnTitleStateChanged(object sender, VisualStateChangedEventArgs e) =>
            UpdateModeNavCompact(e.NewState == CompactLabels);

        private void UpdateModeNavCompact(bool compact)
        {
            if (EditNavItem is null || ModeNavView is null) return;
            var items = new[] { EditNavItem, SyncNavItem, PreviewNavItem };
            if (compact)
            {
                foreach (var item in items)
                {
                    if (item is null) continue;
                    if (!_modeLabels.ContainsKey(item))
                        _modeLabels[item] = item.Content;
                    item.Content = null;
                }
                if (Resources.TryGetValue("ModeNavCompactItemStyle", out var style) && style is Style compactStyle)
                    ModeNavView.MenuItemContainerStyle = compactStyle;
            }
            else
            {
                foreach (var item in items)
                {
                    if (item is null) continue;
                    if (_modeLabels.TryGetValue(item, out var label))
                        item.Content = label;
                }
                ModeNavView.ClearValue(NavigationView.MenuItemContainerStyleProperty);
            }
        }

        private static Border? FindTogglePill(ToggleButton button)
        {
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(button);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(current, i);
                    if (child is Border border && border.Name == "SelectionPill") return border;
                    queue.Enqueue(child);
                }
            }
            return null;
        }

        private void PaintPillToggle(ToggleButton button, bool on)
        {
            var pill = FindTogglePill(button);
            if (pill is not null) pill.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void PaintPillToggles(bool animate)
        {
            foreach (var tb in new ToggleButton[] { RomanToggle, TransToggle, WordRomanToggle })
            {
                PaintPillToggle(tb, tb.IsChecked == true);
                if (animate && tb.IsChecked == true) PopPill(tb);
            }
        }

        private void OnPillCheckChanged(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tb) PaintPillToggle(tb, tb.IsChecked == true);
        }

        private Compositor? _compositor;
        private CubicBezierEasingFunction? _decelerate;
        private bool _motionEnabled = true;
        private readonly Dictionary<ToggleButton, Visual> _popVisuals = new();

        private void SetupMotion()
        {
            try
            {
                _motionEnabled = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            }
            catch
            {
                _motionEnabled = true;
            }
            _compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _decelerate = _compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));
        }

        private Visual? FindPill(ToggleButton button)
        {
            if (_popVisuals.TryGetValue(button, out var cached)) return cached;
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(button);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(current, i);
                    if (child is FrameworkElement fe && fe.Name == "SelectionPill")
                    {
                        var visual = ElementCompositionPreview.GetElementVisual(fe);
                        _popVisuals[button] = visual;
                        return visual;
                    }
                    queue.Enqueue(child);
                }
            }
            return null;
        }

        private void PopPill(ToggleButton button)
        {
            if (!_motionEnabled || _compositor is null) return;
            var visual = FindPill(button);
            if (visual is null) return;
            visual.StopAnimation("Opacity");
            visual.Opacity = 0;
            var fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(150);
            fade.StopBehavior = AnimationStopBehavior.SetToFinalValue;
            fade.InsertKeyFrame(1f, 1f, _decelerate);
            visual.StartAnimation("Opacity", fade);
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

        private void LoadMasterToggles()
        {
            RomanToggle.IsChecked = AppSettings.ShowRomanization;
            TransToggle.IsChecked = AppSettings.ShowTranslation;
            WordRomanToggle.IsChecked = AppSettings.ShowWordRomanization;
        }

        private void OnMasterToggleClick(object sender, RoutedEventArgs e)
        {
            AppSettings.ShowRomanization = RomanToggle.IsChecked == true;
            AppSettings.ShowTranslation = TransToggle.IsChecked == true;
            AppSettings.ShowWordRomanization = WordRomanToggle.IsChecked == true;
            AppSettings.NotifyDisplayChanged();
            if (sender is ToggleButton tb && tb.IsChecked == true)
                PopPill(tb);
        }

        private void OnStubClick(object sender, RoutedEventArgs e) =>
            Notify("This arrives in a later slice.");

        private async void OnGitHubClick(object sender, RoutedEventArgs e) =>
            _ = await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/amll-dev/amll-ttml-tool"));

        private async void OnDocsClick(object sender, RoutedEventArgs e) =>
            _ = await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/amll-dev/amll-ttml-tool/wiki"));

        private void OnUndoClick(object sender, RoutedEventArgs e) => _store.Undo();
        private void OnRedoClick(object sender, RoutedEventArgs e) => _store.Redo();

        private async void OnPasteClick(object sender, RoutedEventArgs e)
        {
            if (_intake is not null) await _intake.PasteAsync();
        }

        private void OnSaveMenuOpening(object sender, object e)
        {
            SaveTtmlItem.IsChecked = AppSettings.LastSaveFormat != "apple";
            SaveAppleItem.IsChecked = AppSettings.LastSaveFormat == "apple";
        }

        private void OnCopyMenuOpening(object sender, object e)
        {
            CopyTtmlItem.IsChecked = AppSettings.LastCopyFormat != "apple";
            CopyAppleItem.IsChecked = AppSettings.LastCopyFormat == "apple";
        }

        private void OnSaveFormatPicked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioMenuFlyoutItem item && item.Tag is string tag)
                AppSettings.LastSaveFormat = tag;
        }

        private void OnCopyFormatPicked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioMenuFlyoutItem item && item.Tag is string tag)
                AppSettings.LastCopyFormat = tag;
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            if (_intake is null) return;
            var nameBox = new TextBox
            {
                Text = System.IO.Path.GetFileNameWithoutExtension(_store.FileName),
                Header = "File name",
            };
            var formatBox = new ComboBox { Header = "Format", SelectedIndex = 0 };
            formatBox.Items.Add("TTML (.ttml)");
            formatBox.Items.Add("Apple TTML (.ttml)");
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(nameBox);
            panel.Children.Add(formatBox);
            var dialog = new ContentDialog
            {
                Title = "Export lyrics",
                Content = panel,
                PrimaryButtonText = "Export",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var window = ((App)Application.Current).MainWindow;
            var file = await FileDialogs.PickSaveFileAsync(window, nameBox.Text.Trim());
            if (file is null) return;
            await _intake.SaveToFileAsync(file, formatBox.SelectedIndex == 1);
        }
    }
}
