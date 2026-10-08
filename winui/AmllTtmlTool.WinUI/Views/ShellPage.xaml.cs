using AmllTtmlTool.Core.Models;
using AmllTtmlTool.WinUI.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media;
using Windows.Storage;

namespace AmllTtmlTool.WinUI.Views
{
    public sealed partial class ShellPage : Page
    {
        private readonly Type[] _pages = [typeof(EditPage), typeof(SyncPage), typeof(PreviewPage)];
        private readonly LyricStore _store = new();
        private readonly AudioEngine _audio;
        private FileIntake? _intake;
        private bool _scrubbing;
        private bool _syncingSlider;
        private SystemMediaTransportControls? _smtc;

        public ShellPage()
        {
            this.InitializeComponent();
            _audio = new AudioEngine();
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
            }
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

        private async void OnSaveClick(object sender, RoutedEventArgs e) => await SaveAsync(false);
        private void OnSaveAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
            _ = SaveAsync(false);

        private async void OnSaveAppleClick(object sender, RoutedEventArgs e) => await SaveAsync(true);
        private void OnSaveAppleAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
            _ = SaveAsync(true);

        private async void OnCopyClick(object sender, RoutedEventArgs e) =>
            await (_intake?.CopyTtmlAsync(false) ?? Task.CompletedTask);

        private async void OnCopyAppleClick(object sender, RoutedEventArgs e) =>
            await (_intake?.CopyTtmlAsync(true) ?? Task.CompletedTask);

        private async void OnPasteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (_intake is not null) await _intake.PasteAsync();
        }

        private void OnUndoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
            _store.Undo();

        private void OnRedoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) =>
            _store.Redo();

        private Task SaveAsync(bool apple) => _intake?.SaveAsync(apple) ?? Task.CompletedTask;

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
