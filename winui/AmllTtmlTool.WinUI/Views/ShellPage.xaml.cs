using AmllTtmlTool.Core.Models;
using AmllTtmlTool.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
            EditModeButton.IsChecked = true;
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
            }
            this.Loaded += (_, _) =>
            {
                UpdateCaptionInsets();
                SetupMotion();
                PaintAllToggles(animate: false);
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
            UpdateModeSlide(animate: true);
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

        private void PaintSegment(RadioButton button, bool on)
        {
        }

        private void PaintAllToggles(bool animate)
        {
            foreach (var tb in new ToggleButton[] { RomanToggle, TransToggle, WordRomanToggle })
            {
                PaintPillToggle(tb, tb.IsChecked == true);
                if (animate && tb.IsChecked == true) PopPill(tb);
            }
            PaintSegment(EditModeButton, EditModeButton.IsChecked == true);
            PaintSegment(SyncModeButton, SyncModeButton.IsChecked == true);
            PaintSegment(PreviewModeButton, PreviewModeButton.IsChecked == true);
            UpdateModeSlide(animate: animate);
        }

        private void OnPillCheckChanged(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton tb) PaintPillToggle(tb, tb.IsChecked == true);
        }

        private void OnSegmentCheckChanged(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb) PaintSegment(rb, rb.IsChecked == true);
        }

        private Compositor? _compositor;
        private CubicBezierEasingFunction? _decelerate;
        private bool _motionEnabled = true;
        private double _pillX = double.NaN;
        private bool _slideHooked;
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
            _compositor = ElementCompositionPreview.GetElementVisual(ModeIndicatorCanvas).Compositor;
            _decelerate = _compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(0, 1));
            foreach (var tb in new ToggleButton[] { RomanToggle, TransToggle, WordRomanToggle })
            {
                tb.AddHandler(PointerPressedEvent, new PointerEventHandler(OnPillPointerPressed), true);
                tb.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPillPointerReleased), true);
                tb.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnPillPointerReleased), true);
                tb.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnPillPointerReleased), true);
            }
            foreach (var rb in new RadioButton[] { EditModeButton, SyncModeButton, PreviewModeButton })
            {
                rb.AddHandler(PointerPressedEvent, new PointerEventHandler(OnPillPointerPressed), true);
                rb.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnPillPointerReleased), true);
                rb.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnPillPointerReleased), true);
                rb.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnPillPointerReleased), true);
            }
            if (!_slideHooked)
            {
                _slideHooked = true;
                ModeButtonRow.SizeChanged += (_, _) => UpdateModeSlide(animate: false);
            }
            UpdateModeSlide(animate: false);
        }

        private RadioButton CurrentModeButton() =>
            SyncModeButton.IsChecked == true ? SyncModeButton
            : PreviewModeButton.IsChecked == true ? PreviewModeButton
            : EditModeButton;

        private void UpdateModeSlide(bool animate)
        {
            if (ModeSlidePill is null || ModeButtonRow.ActualWidth <= 0) return;
            var button = CurrentModeButton();
            var transform = button.TransformToVisual(ModeIndicatorCanvas);
            var bounds = transform.TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            var targetX = bounds.X + (bounds.Width - ModeSlidePill.Width) / 2;
            var targetY = Math.Max(0, ModeIndicatorCanvas.ActualHeight - ModeSlidePill.Height - 5);
            var first = double.IsNaN(_pillX);
            var delta = first ? 0 : _pillX - targetX;
            _pillX = targetX;
            Canvas.SetLeft(ModeSlidePill, targetX);
            Canvas.SetTop(ModeSlidePill, targetY);
            ModeSlidePill.Visibility = Visibility.Visible;
            if (!animate || first || delta == 0 || !_motionEnabled || _compositor is null) return;
            var visual = ElementCompositionPreview.GetElementVisual(ModeSlidePill);
            visual.StopAnimation("Offset");
            visual.Offset = new Vector3((float)delta, 0, 0);
            visual.StartAnimation("Offset", SpringTo(Vector3.Zero, new Vector3((float)delta, 0, 0), 0.75f, TimeSpan.FromMilliseconds(180)));
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

        private SpringVector3NaturalMotionAnimation SpringTo(Vector3 finalValue, Vector3 initialValue, float dampingRatio, TimeSpan period)
        {
            var spring = _compositor!.CreateSpringVector3Animation();
            spring.FinalValue = finalValue;
            spring.InitialValue = initialValue;
            spring.InitialVelocity = Vector3.Zero;
            spring.DampingRatio = dampingRatio;
            spring.Period = period;
            spring.StopBehavior = AnimationStopBehavior.SetToFinalValue;
            return spring;
        }

        private void PopPill(ToggleButton button)
        {
            if (!_motionEnabled || _compositor is null) return;
            var visual = FindPill(button);
            if (visual is null) return;
            visual.CenterPoint = new Vector3(8, 1.5f, 0);
            visual.StopAnimation("Scale");
            visual.StopAnimation("Opacity");
            visual.Scale = new Vector3(0.4f, 1, 1);
            visual.Opacity = 0;
            visual.StartAnimation("Scale", SpringTo(new Vector3(1, 1, 1), new Vector3(0.4f, 1, 1), 0.55f, TimeSpan.FromMilliseconds(220)));
            var fade = _compositor.CreateScalarKeyFrameAnimation();
            fade.Duration = TimeSpan.FromMilliseconds(150);
            fade.StopBehavior = AnimationStopBehavior.SetToFinalValue;
            fade.InsertKeyFrame(1f, 1f, _decelerate);
            visual.StartAnimation("Opacity", fade);
        }

        private void OnPillPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_motionEnabled || _compositor is null) return;
            if (sender is not FrameworkElement fe || fe.ActualWidth <= 0) return;
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2, (float)fe.ActualHeight / 2, 0);
            visual.StopAnimation("Scale");
            var press = _compositor.CreateVector3KeyFrameAnimation();
            press.Duration = TimeSpan.FromMilliseconds(100);
            press.StopBehavior = AnimationStopBehavior.SetToFinalValue;
            press.InsertKeyFrame(1f, new Vector3(0.96f, 0.96f, 1), _decelerate);
            visual.StartAnimation("Scale", press);
        }

        private void OnPillPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_motionEnabled || _compositor is null) return;
            if (sender is not FrameworkElement fe) return;
            var visual = ElementCompositionPreview.GetElementVisual(fe);
            visual.StopAnimation("Scale");
            visual.Scale = new Vector3(0.96f, 0.96f, 1);
            visual.StartAnimation("Scale", SpringTo(new Vector3(1, 1, 1), new Vector3(0.96f, 0.96f, 1), 0.5f, TimeSpan.FromMilliseconds(180)));
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
