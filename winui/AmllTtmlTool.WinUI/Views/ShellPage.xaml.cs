using AmllTtmlTool.Core.Models;
using AmllTtmlTool.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
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
            WindowSizing.SetMinSize(window, 600, 480);
            window.SizeChanged += (_, _) => UpdateCaptionInsets();
            TitleBarArea.SizeChanged += (_, _) => UpdateCaptionInsets();
            // Centering recompute runs after every layout pass (not just resizes):
            // multi-pass cascades (star redistribution after a collapse, ellipsis
            // settling) only reach final numbers on the LAST pass, and only
            // LayoutUpdated fires for pure-reposition passes. The handler is
            // read-only plus a layout-neutral transform set, so it cannot loop.
            RootGrid.LayoutUpdated += (_, _) => UpdateCenterOffset();
            // Per-frame position display (throttled to hidden windows) and
            // activation tracking for the same throttle.
            CompositionTarget.Rendering += OnRendering;
            window.Activated += (_, args) =>
                _windowActive = args.WindowActivationState != WindowActivationState.Deactivated;
            // Drag-and-drop is registered with handledEventsToo: child
            // controls (TextBox, ribbon cards, list views) mark drag events
            // handled, which would otherwise stop them before RootGrid.
            // XAML attributes cannot set handledEventsToo, hence code here.
            RootGrid.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(OnDragEnter), true);
            RootGrid.AddHandler(UIElement.DragOverEvent, new DragEventHandler(OnDragOver), true);
            RootGrid.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(OnDragLeave), true);
            RootGrid.AddHandler(UIElement.DropEvent, new DragEventHandler(OnDrop), true);
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
                if (SidePane.SelectedItem is null)
                    SidePane.SelectedItem = SidePane.MenuItems[0];
                ApplyTitleState(TitleStates.CurrentState);
                // First paint: layout is already complete, so a direct read is
                // fresh. Later state changes go through RequestNavWidthPass.
                SizeNavToItems();
#if DEBUG
                // Layout-test harness (kept for ribbon/separator/animation items).
                // Env var or --layout-test arg; either avoids startup-file handling.
                if (string.Equals(Environment.GetEnvironmentVariable("AMLL_SELF_CHECK"), "1",
                        StringComparison.OrdinalIgnoreCase)
                    || Environment.GetCommandLineArgs().Contains("--layout-test"))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            string? State() => VisualStateManager.GetVisualStateGroups(RootGrid)
                                .OfType<VisualStateGroup>().FirstOrDefault(g => g.Name == "TitleStates")?.CurrentState?.Name;
                            await Services.TitleCenteringCheck.RunAsync(win,
                                new Services.TitleProbe(TitleBarArea, CenterTitleBlock, ModeNavView, RightFullPanel, RightCompactSplit, State));
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.TitleCenteringCheck.ReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                // Audio-test harness: --audio-test <folder> [reportPath].
                // Drives the real AudioEngine path per file (silent volume-0
                // playback, seeks), writes a report, self-closes. DEBUG-only.
                var audioArgs = Environment.GetCommandLineArgs();
                var audioIdx = Array.IndexOf(audioArgs, "--audio-test");
                if (audioIdx >= 0)
                {
                    var audioFolder = audioIdx + 1 < audioArgs.Length ? audioArgs[audioIdx + 1] : "";
                    var audioReport = audioIdx + 2 < audioArgs.Length ? audioArgs[audioIdx + 2] : null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            await Services.AudioTestCheck.RunAsync(win, _audio, audioFolder, audioReport);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.AudioTestCheck.DefaultReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                // Drop-test harness: --drop-test [reportPath]. Structural
                // drag-and-drop verification (real OLE drags cannot be
                // synthesized headless). Self-closes. DEBUG-only.
                var dropIdx = Array.IndexOf(audioArgs, "--drop-test");
                if (dropIdx >= 0)
                {
                    var dropReport = dropIdx + 1 < audioArgs.Length ? audioArgs[dropIdx + 1] : null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            await Services.DropCheck.RunAsync(win, dropReport);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.DropCheck.DefaultReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                // Stretch-test harness: --stretch-test [reportPath]. Offline
                // time-stretch verification (no device): sine length/pitch/
                // position checks plus the 60-second CPU benchmark.
                // Self-closes. DEBUG-only.
                var stretchIdx = Array.IndexOf(audioArgs, "--stretch-test");
                if (stretchIdx >= 0)
                {
                    var stretchReport = stretchIdx + 1 < audioArgs.Length ? audioArgs[stretchIdx + 1] : null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            await Services.StretchTestCheck.RunAsync(win, stretchReport);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.StretchTestCheck.DefaultReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                // Seek-test harness: --seek-test <folder> [reportPath] (engine
                // seeks) and --seek-ui-target <file> (loads one file paused
                // at 0 and stays open for the winapp-ui driver; watchdog
                // closes it). Self-closing. DEBUG-only.
                var seekIdx = Array.IndexOf(audioArgs, "--seek-test");
                if (seekIdx >= 0)
                {
                    var seekFolder = seekIdx + 1 < audioArgs.Length ? audioArgs[seekIdx + 1] : "";
                    var seekReport = seekIdx + 2 < audioArgs.Length ? audioArgs[seekIdx + 2] : null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            await Services.SeekTestCheck.RunAsync(win, _audio, seekFolder, seekReport);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.SeekTestCheck.DefaultReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                var seekTargetIdx = Array.IndexOf(audioArgs, "--seek-ui-target");
                if (seekTargetIdx >= 0)
                {
                    var seekTargetFile = seekTargetIdx + 1 < audioArgs.Length ? audioArgs[seekTargetIdx + 1] : "";
                    var seekTargetReport = seekTargetIdx + 2 < audioArgs.Length ? audioArgs[seekTargetIdx + 2] : null;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(800);
                        var win = ((App)Application.Current).MainWindow;
                        await Services.SeekTestCheck.RunTargetAsync(win, _audio, seekTargetFile, seekTargetReport);
                    });
                }
                var seekHoldIdx = Array.IndexOf(audioArgs, "--seek-ui-hold");
                if (seekHoldIdx >= 0)
                {
                    var seekHoldFile = seekHoldIdx + 1 < audioArgs.Length ? audioArgs[seekHoldIdx + 1] : "";
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(800);
                        var win = ((App)Application.Current).MainWindow;
                        await Services.SeekTestCheck.RunHoldAsync(win, _audio, seekHoldFile);
                    });
                }
                // Clock-test harness: --clock-test <file> [reportPath].
                // Volume-0 playback while sampling the UI position clock on
                // every rendered frame. Self-closes. DEBUG-only.
                var clockIdx = Array.IndexOf(audioArgs, "--clock-test");
                if (clockIdx >= 0)
                {
                    var clockFile = clockIdx + 1 < audioArgs.Length ? audioArgs[clockIdx + 1] : "";
                    var clockReport = clockIdx + 2 < audioArgs.Length ? audioArgs[clockIdx + 2] : null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            await Services.ClockTestCheck.RunAsync(win, _audio, clockFile, clockReport);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.ClockTestCheck.DefaultReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
#endif
            };
        }

        private void UpdateCaptionInsets()
        {
            if (XamlRoot is null) return;
            var window = ((App)Application.Current).MainWindow;
            if (window.AppWindow is not AppWindow appWindow) return;
            var scale = WindowSizing.GetScale(window);
            // Physical -> logical. No hardcoded floor: the system strip width is
            // whatever the OS reports, at whatever DPI the monitor is at.
            var right = appWindow.TitleBar.RightInset / scale;
            var left = appWindow.TitleBar.LeftInset / scale;
            if (Math.Abs(RightInsetCol.Width.Value - right) > 0.5)
                RightInsetCol.Width = new GridLength(right);
            if (Math.Abs(LeftInsetCol.Width.Value - left) > 0.5)
                LeftInsetCol.Width = new GridLength(left);
            UpdatePassthroughRegions(appWindow, scale);
        }

        // Centers the pill+tabs unit in the visible gap between the left and
        // right clusters (gap center, not window center: unequal clusters
        // would otherwise leave unequal gaps). Cheap by design: only reads
        // measured widths and sets a layout-neutral RenderTransform. No layout
        // writes, no timers, no Task.Delay, so there is no feedback loop.
        private string? _centerTier;
        private bool _centerFirst = true;
        private Storyboard? _centerStoryboard;
        private double _centerAppliedShift;
        private VisualStateGroup? _titleStateGroup;

        private void UpdateCenterOffset()
        {
            double RectLeft(FrameworkElement element) =>
                element.TransformToVisual(TitleBarArea).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            double RectRight(FrameworkElement element) => RectLeft(element) + element.ActualWidth;
            FrameworkElement? LeftClusterGrid()
            {
                foreach (var child in TitleBarArea.Children)
                {
                    if (child is FrameworkElement fe && Grid.GetColumn(fe) == 1) return fe;
                }
                return null;
            }
            try
            {
                if (XamlRoot is null) return;
                var areaW = TitleBarArea.ActualWidth;
                if (areaW <= 0 || CenterNudge is null) return;
                var tier = (_titleStateGroup ??= VisualStateManager.GetVisualStateGroups(RootGrid)
                    .OfType<VisualStateGroup>()
                    .FirstOrDefault(g => g.Name == "TitleStates"))?.CurrentState?.Name;
                var leftCluster = LeftClusterGrid();
                if (leftCluster is null) return;
                var leftRight = RectRight(leftCluster);
                FrameworkElement rightPanel = RightFullPanel.Visibility == Visibility.Visible ? RightFullPanel : RightCompactSplit;
                var rightLeft = RectLeft(rightPanel);
                var unitLeft = RectLeft(CenterTitleBlock);
                var unitW = CenterTitleBlock.ActualWidth;
                if (unitW <= 0) return;
                // All rects below include any previously applied shift, so the new
                // absolute shift is computed relative to the unshifted layout.
                // Target is the gap center between the clusters, so the left
                // and right gaps come out equal wherever the unit fits.
                var gapCenter = (leftRight + rightLeft) / 2;
                var target = gapCenter - (unitLeft + unitW / 2) + _centerAppliedShift;
                var lo = -(unitLeft - leftRight - 2) + _centerAppliedShift;
                var hi = +(rightLeft - (unitLeft + unitW) - 2) + _centerAppliedShift;
                var shift = lo > hi ? 0 : Math.Clamp(target, lo, hi);
                var tierChanged = tier != _centerTier;
                _centerTier = tier;
                // Converged (or already gliding there): leave everything alone.
                // Restarting here is what made offsets flicker mid-cascade.
                if (Math.Abs(shift - _centerAppliedShift) < 0.5) return;
                if (!_motionEnabled || _centerFirst || !tierChanged)
                {
                    _centerStoryboard?.Stop();
                    _centerStoryboard = null;
                    CenterNudge.X = shift;
                }
                else
                {
                    try
                    {
                        _centerStoryboard?.Stop();
                        var animation = new DoubleAnimation
                        {
                            From = _centerAppliedShift,
                            To = shift,
                            Duration = TimeSpan.FromMilliseconds(180),
                            EnableDependentAnimation = true,
                        };
                        // Held in a field: a collected Storyboard drops its values
                        // and the offset snaps back mid-glide.
                        _centerStoryboard = new Storyboard();
                        _centerStoryboard.Children.Add(animation);
                        Storyboard.SetTarget(animation, CenterNudge);
                        Storyboard.SetTargetProperty(animation, "X");
                        var board = _centerStoryboard;
                        board.Completed += (_, _) =>
                        {
                            if (ReferenceEquals(_centerStoryboard, board)) _centerStoryboard = null;
                            UpdateCaptionInsets();
                        };
                        board.Begin();
                    }
                    catch
                    {
                        // Motion is enhancement-only: a failed glide must still
                        // land on the correct offset, instantly.
                        _centerStoryboard = null;
                        CenterNudge.X = shift;
                    }
                }
                _centerAppliedShift = shift;
                _centerFirst = false;
            }
            catch { }
        }

        // Interactive elements inside SetTitleBar's drag region need explicit
        // Passthrough rects or the title bar swallows their input.
        private void UpdatePassthroughRegions(AppWindow appWindow, double scale)
        {
            try
            {
                var source = InputNonClientPointerSource.GetForWindowId(appWindow.Id);
                var passthrough = new List<Windows.Graphics.RectInt32>();
                foreach (var child in TitleBarArea.Children)
                    CollectPassthroughRegions(child, passthrough, scale);
                source.SetRegionRects(NonClientRegionKind.Passthrough, passthrough.ToArray());
            }
            catch
            {
                // Non-customized title bars (rare OS configs) don't expose this.
            }
        }

        private void CollectPassthroughRegions(
            DependencyObject current,
            List<Windows.Graphics.RectInt32> passthrough,
            double scale)
        {
            if (current is FrameworkElement element)
            {
                if (element.Visibility != Visibility.Visible ||
                    element.ActualWidth <= 0 ||
                    element.ActualHeight <= 0)
                    return;
                if (element is Control control && !control.IsEnabled)
                    return;
                if (IsInteractive(element))
                    passthrough.Add(ToPhysical(element, scale, TitleBarArea));
            }

            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++)
                CollectPassthroughRegions(VisualTreeHelper.GetChild(current, i), passthrough, scale);
        }

        // A Button/SplitButton/ToggleButton/MenuBar child is interactive; a
        // Border or plain layout panel is inert and stays draggable.
        private static bool IsInteractive(FrameworkElement element) =>
            element is ButtonBase or MenuBar or NavigationView or TextBox or NumberBox;

        private static Windows.Graphics.RectInt32 ToPhysical(FrameworkElement element, double scale, FrameworkElement host)
        {
            var transform = element.TransformToVisual(host);
            var origin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            return new Windows.Graphics.RectInt32
            {
                X = (int)Math.Round(origin.X * scale),
                Y = (int)Math.Round(origin.Y * scale),
                Width = (int)Math.Round(element.ActualWidth * scale),
                Height = (int)Math.Round(element.ActualHeight * scale),
            };
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
            FileInfoFlyoutName.Text = _store.FileName;
            FileInfoFlyoutDirty.Text = _store.IsDirty ? "● Unsaved changes" : string.Empty;
            ToolTipService.SetToolTip(FileInfoButton, _store.FileName);
            UndoButton.IsEnabled = _store.CanUndo;
            RedoButton.IsEnabled = _store.CanRedo;
        }

        private static string Stamp(TimeSpan t) =>
            $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";

        private void RefreshAudioChrome()
        {
            // PositionText and the slider thumb move on every rendered frame
            // (OnRendering, same clock); everything else here changes rarely.
            DurationText.Text = Stamp(_audio.Duration);
            PlayButton.Icon = new SymbolIcon(_audio.IsPlaying ? Symbol.Pause : Symbol.Play);
            PlayButton.IsEnabled = _audio.HasAudio;
            SeekSlider.IsEnabled = _audio.HasAudio;
            if (_audio.HasAudio && _audio.Duration > TimeSpan.Zero)
                SeekSlider.Maximum = _audio.Duration.TotalSeconds;
            UpdateSmtc();
        }

        private long _lastShownMs = -1;
        private long _lastSliderMs = -1;
        private bool _windowActive = true;

        // Per-frame position display: extrapolate the published snapshot,
        // repaint only when the millisecond text actually changes. The thumb
        // follows at most every 100ms (it needs no ms precision, and every
        // thumb write costs layout). Gated work only; allocations happen on
        // actual change, never per frame.
        private void OnRendering(object? sender, object? e)
        {
            if (!_windowActive || !_audio.HasAudio) return;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Services.RenderStats.Frames++;
            var pos = Services.PositionClock.ExtrapolateNow();
            var ms = (long)Math.Round(pos.TotalMilliseconds);
            // Strictly monotonic display: a rebase that corrects QPC-vs-hardware
            // skew can land a hair behind the last shown millisecond. The
            // snapshot underneath stays truthful (stamping reads it raw);
            // only the painted text never steps back.
            if (ms < _lastShownMs) ms = _lastShownMs;
            if (ms != _lastShownMs)
            {
                _lastShownMs = ms;
                PositionText.Text = Stamp(pos);
                Services.RenderStats.TextUpdates++;
            }
            if (ms - _lastSliderMs >= 100 && !_scrubbing && _audio.Duration > TimeSpan.Zero)
            {
                _lastSliderMs = ms;
                _syncingSlider = true;
                SeekSlider.Value = Math.Max(0, Math.Min(pos.TotalSeconds, _audio.Duration.TotalSeconds));
                _syncingSlider = false;
            }
            Services.RenderStats.HandlerTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
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

        private void OnRatePresetClick(object sender, RoutedEventArgs e)
        {
            if (sender is Microsoft.UI.Xaml.Controls.Button button
                && double.TryParse(button.Tag as string,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var rate))
                RateSlider.Value = rate;
        }

        private void OnKeepPitchChanged(object sender, RoutedEventArgs e)
        {
            var keep = KeepPitchCheck.IsChecked == true;
            _audio.SetKeepPitch(keep);
            RateSlider.Header = keep ? "Rate (pitch preserved)" : "Rate (varispeed: pitch follows rate)";
        }

        private void OnSeekPointerPressed(object sender, PointerRoutedEventArgs e) => BeginScrub();

        private void OnSeekPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            EndScrub();
            _audio.SeekTo(SeekSlider.Value);
            RefreshAudioChrome();
        }

        internal void BeginScrub() => _scrubbing = true;

        internal void EndScrub() => _scrubbing = false;

        private long _lastLiveSeekMs;

        private void OnSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_syncingSlider) return;
            // User intent comes from two paths: pointer drag (_scrubbing) or
            // keyboard arrows on the focused slider. Programmatic clock
            // writes are always bracketed by _syncingSlider above. (Arrow
            // keys previously fell through here and did nothing.)
            if (!_scrubbing && SeekSlider.FocusState == FocusState.Unfocused) return;
            // Live seek while dragging, throttled: every ValueChanged would
            // restart the output dozens of times per second. Release always
            // seeks exactly (see above).
            var now = Environment.TickCount64;
            if (now - _lastLiveSeekMs < 100) return;
            _lastLiveSeekMs = now;
            _audio.SeekTo(e.NewValue);
        }

        private void OnLocateClick(object sender, RoutedEventArgs e) =>
            Notify("Line locate arrives with the lyric list.");

        private void OnSpectroClick(object sender, RoutedEventArgs e)
        {
            Notify("Spectrogram arrives in Phase 2.");
            if (SpectroToggle.IsChecked == true) SpectroToggle.IsChecked = false;
        }

        private void OnDragEnter(object sender, DragEventArgs e) =>
            _ = HandleDragAsync(e);

        private void OnDragOver(object sender, DragEventArgs e) =>
            _ = HandleDragAsync(e);

        // Shows the overlay immediately on enter and refines the caption and
        // accepted operation once the file names arrive (async, via deferral
        // so the OS keeps the right cursor while we inspect).
        private async Task HandleDragAsync(DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.Handled = true;
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var names = items.OfType<StorageFile>().Select(f => f.Name).ToList();
                var kind = AmllTtmlTool.Core.DropFiles.Classify(
                    names, Services.LyricStore.AudioExtensions, Services.LyricStore.LyricExtensions);
                var accept = kind is AmllTtmlTool.Core.DropKind.Lyrics
                    or AmllTtmlTool.Core.DropKind.Audio
                    or AmllTtmlTool.Core.DropKind.Both;
                e.AcceptedOperation = accept
                    ? DataPackageOperation.Copy
                    : DataPackageOperation.None;
                DropHintText.Text = AmllTtmlTool.Core.DropFiles.Caption(kind);
                DropOverlay.Visibility = Visibility.Visible;
            }
            catch
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            e.Handled = true;
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
            if (_intake is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count == 0) return;
                var files = items.OfType<StorageFile>().ToList();
                var lyrics = files.Where(f => Services.LyricStore.IsLyricFile(f.Name)).ToList();
                var audios = files.Where(f => Services.LyricStore.IsAudioFile(f.Name)).ToList();
                if (lyrics.Count == 0 && audios.Count == 0)
                {
                    Notify($"Unsupported file{(files.Count == 1 ? string.Empty : "s")}: " +
                        string.Join(", ", files.Select(f => f.Name).DefaultIfEmpty(items[0].Name)));
                    return;
                }
                // One dirty-confirm for the lyric side; a cancelled lyric
                // open still lets a co-dropped audio file through.
                var openLyric = lyrics.Count > 0 && await ConfirmDirtyAsync("Open");
                if (openLyric) await _intake.OpenLyricFileAsync(lyrics[0]);
                if (audios.Count > 0) await _intake.OpenAudioFileAsync(audios[0]);
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
        private bool _pendingSlideFromRight;

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
            // Built-in Frame slide is near-invisible on the sparse Phase 1 pages,
            // so motion is driven by composition (same system as the ribbon).
            // The slide plays on the incoming Page visual in ContentFrame_Navigated,
            // never on the persistent Frame container, so a stuck state can never
            // strand the container off-center (it dies with the old Page).
            _pendingSlideFromRight = forward;
            _ = ContentFrame.Navigate(_pages[index], null, new SuppressNavigationTransitionInfo());
        }

        private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
        {
            if (ContentFrame.Content is FrameworkElement page)
                AnimateIncomingPage(page, fromRight: _pendingSlideFromRight);
        }

        private void AnimateIncomingPage(FrameworkElement page, bool fromRight)
        {
            Visual? visual;
            try
            {
                visual = ElementCompositionPreview.GetElementVisual(page);
            }
            catch { return; }
            if (!_motionEnabled || _compositor is null || _decelerate is null)
            {
                // Base values are never mutated, so just clear any animation.
                try
                {
                    visual.StopAnimation("Offset");
                    visual.StopAnimation("Opacity");
                }
                catch { }
                return;
            }
            try
            {
                // Base values are never mutated: from/to lives entirely in the
                // keyframes, so no failure path can strand content off-center.
                visual.StopAnimation("Offset");
                visual.StopAnimation("Opacity");
                const float offset = 64f;
                var slide = _compositor.CreateVector3KeyFrameAnimation();
                slide.Duration = TimeSpan.FromMilliseconds(250);
                slide.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                slide.InsertKeyFrame(0f, new Vector3(fromRight ? offset : -offset, 0, 0), _decelerate);
                slide.InsertKeyFrame(1f, Vector3.Zero, _decelerate);
                visual.StartAnimation("Offset", slide);
                var fade = _compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = TimeSpan.FromMilliseconds(150);
                fade.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                fade.InsertKeyFrame(0f, 0f, _decelerate);
                fade.InsertKeyFrame(1f, 1f, _decelerate);
                visual.StartAnimation("Opacity", fade);
            }
            catch
            {
                // Base values were never mutated: clear animations to rest centered.
                try
                {
                    visual.StopAnimation("Offset");
                    visual.StopAnimation("Opacity");
                }
                catch { }
            }
        }

        private void AnimateRibbonBar(FrameworkElement bar, bool fromRight)
        {
            if (!_motionEnabled || _compositor is null || _decelerate is null) return;
            try
            {
                // Base values are never mutated: from/to lives entirely in the
                // keyframes, so an interrupted switch can't strand the bar offset.
                var visual = ElementCompositionPreview.GetElementVisual(bar);
                visual.StopAnimation("Offset");
                visual.StopAnimation("Opacity");
                const float offset = 24f;
                var slide = _compositor.CreateVector3KeyFrameAnimation();
                slide.Duration = TimeSpan.FromMilliseconds(200);
                slide.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                slide.InsertKeyFrame(0f, new Vector3(fromRight ? offset : -offset, 0, 0), _decelerate);
                slide.InsertKeyFrame(1f, Vector3.Zero, _decelerate);
                visual.StartAnimation("Offset", slide);
                var fade = _compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = TimeSpan.FromMilliseconds(150);
                fade.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                fade.InsertKeyFrame(0f, 0f, _decelerate);
                fade.InsertKeyFrame(1f, 1f, _decelerate);
                visual.StartAnimation("Opacity", fade);
            }
            catch
            {
                try
                {
                    var visual = ElementCompositionPreview.GetElementVisual(bar);
                    visual.StopAnimation("Offset");
                    visual.StopAnimation("Opacity");
                }
                catch { }
            }
        }

        private readonly Dictionary<NavigationViewItem, object?> _modeLabels = new();

        private void OnTitleStateChanged(object sender, VisualStateChangedEventArgs e) =>
            ApplyTitleState(e.NewState);

        private int _titleStateGeneration;

        private void ApplyTitleState(VisualState? state) => ApplyTitleStateCore(state);

        private void ApplyTitleStateCore(VisualState? state)
        {
            _titleStateGeneration++;
            var level = state?.Name switch
            {
                "CompactTitle" => 1,
                "NarrowTitle" => 2,
                "ToggleOverflowTitle" => 3,
                _ => 0,
            };
            _lastTitleStateName = state?.Name ?? "null";
            System.Diagnostics.Debug.WriteLine($"[TitleBar] State={_lastTitleStateName} level={level}");
            // Synchronous visibility: both right-hand panels share
            // RightStar, so a delayed collapse would let them overdraw each
            // other straight under the caption buttons.
            ShowCollapse(FileInfoGrid, level == 0);
            ShowCollapse(FileInfoButton, level >= 1);
            ShowCollapse(MenuBarFull, level == 0);
            ShowCollapse(MenuDivider, level == 0);
            ShowCollapse(MenuCompactButton, level >= 1);
            ShowCollapse(UndoRedoPanel, level == 0);
            ShowCollapse(UndoDivider, level == 0);
            ShowCollapse(RightFullPanel, level == 0);
            ShowCollapse(RightCompactSplit, level >= 1);
            ShowCollapse(MasterTogglePanel, level <= 2);
            ShowCollapse(CenterToggleOverflowButton, level >= 3);
            UpdateModeNavCompact(level >= 2);
            CollapseDeadNavParts();
            RequestNavWidthPass();
            // Visibility changes here move interactive rects without changing
            // TitleBarArea's size, so refresh the OS passthrough regions now:
            // otherwise the toggle overflow button can be keyboard-reachable
            // but dead to the mouse until some later resize.
            UpdateCaptionInsets();
        }

        // Applies the deterministic nav width once per layout-state change,
        // after layout has settled any in-flight transition widths. A
        // synchronous read here would see pre-compact widths, so this waits
        // for the next layout pass (one-shot, generation-guarded).
        private void RequestNavWidthPass()
        {
            var gen = _titleStateGeneration;
            void Handler(object? s, object? e)
            {
                RootGrid.LayoutUpdated -= Handler;
                if (gen != _titleStateGeneration) return;
                SizeNavToItems();
            }
            RootGrid.LayoutUpdated += Handler;
        }

        // Sizes the nav from the items' UNCONSTRAINED widths plus a small
        // chrome allowance, once per layout-state change. Never reads the
        // items host: measuring constrained layout ratchets (a host that is
        // already overflowed measures small, the nav shrinks to it, and the
        // tabs stay stranded in the internal overflow forever, in both
        // directions). A manual Measure here is synchronous and exact, so no
        // post-settle wait is needed; the one-shot pass is kept so the width
        // still lands after any in-flight transition.
        private void SizeNavToItems()
        {
            try
            {
                if (EditNavItem is null || SyncNavItem is null || PreviewNavItem is null || ModeNavView is null)
                    return;
                var total = 0.0;
                foreach (var item in new[] { EditNavItem, SyncNavItem, PreviewNavItem })
                {
                    if (item is null) return;
                    item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    total += item.DesiredSize.Width;
                }
                var target = total + 12;
                // NaN (auto) never satisfies the tolerance check, so an
                // uninitialized Width would stick forever. Treat it as changed.
                if (double.IsNaN(ModeNavView.Width) || Math.Abs(ModeNavView.Width - target) > 0.5)
                    ModeNavView.Width = target;
            }
            catch { }
        }

        private static DependencyObject? FindNavPart(DependencyObject root, string name)
        {
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is FrameworkElement fe && fe.Name == name) return current;
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
            }
            return null;
        }

        // The app never sets pane custom content, but the Top-mode template
        // still reserves layout width for its host column (51px full, ~176px
        // when the items shrink): collapsing the content alone does not shrink
        // the column, so the column itself is zeroed. The internal top-nav
        // overflow button is collapsed for the same reason in reverse: the
        // mode tabs must never live in any overflow, and the nav is always
        // given its full deterministic width, so the button can never be
        // needed. If it ever reappears, tabs would strand in it silently, so
        // the harness asserts it stays idle at every width. Runs on
        // layout-state change only, never per frame. Re-applied per state
        // because template parts can be recreated.
        private void CollapseDeadNavParts()
        {
            try
            {
                var foundCustomContent = false;
                var foundOverflowButton = false;
                var queue = new Queue<DependencyObject>();
                queue.Enqueue(ModeNavView);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (current is FrameworkElement fe)
                    {
                        if (!foundCustomContent && fe.Name == "PaneCustomContentOnTopPane")
                        {
                            foundCustomContent = true;
                            if (fe.Visibility == Visibility.Visible)
                                fe.Visibility = Visibility.Collapsed;
                            var parent = VisualTreeHelper.GetParent(fe) as Grid;
                            var col = parent is not null ? Grid.GetColumn(fe) : -1;
                            if (parent is not null && col >= 0 && col < parent.ColumnDefinitions.Count)
                            {
                                var cd = parent.ColumnDefinitions[col];
                                cd.MinWidth = 0;
                                cd.Width = new GridLength(0);
                            }
                        }
                        else if (!foundOverflowButton && fe.Name == "TopNavOverflowButton")
                        {
                            foundOverflowButton = true;
                            if (fe.Visibility == Visibility.Visible)
                                fe.Visibility = Visibility.Collapsed;
                        }
                        if (foundCustomContent && foundOverflowButton) return;
                    }
                    var count = VisualTreeHelper.GetChildrenCount(current);
                    for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
                }
            }
            catch { }
        }

        private void ShowCollapse(FrameworkElement panel, bool show)
        {
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private string _lastTitleStateName = "?";

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
                    // Direct width beats the Top-pane presenter style, which
                    // stomps MenuItemContainerStyle (labels hid, width didn't).
                    item.Width = 48;
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
                    item.ClearValue(FrameworkElement.WidthProperty);
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

        private void OnCenterToggleMenuOpening(object sender, object e)
        {
            SyncCenterToggleMenuItem(RomanOverflowItem, RomanToggle);
            SyncCenterToggleMenuItem(TransOverflowItem, TransToggle);
            SyncCenterToggleMenuItem(WordRomanOverflowItem, WordRomanToggle);
        }

        private static void SyncCenterToggleMenuItem(ToggleMenuFlyoutItem item, ToggleButton toggle)
        {
            item.IsChecked = toggle.IsChecked == true;
        }

        private void OnCenterToggleMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleMenuFlyoutItem item) return;
            ToggleButton? toggle = (item.Tag as string) switch
            {
                "RomanToggle" => RomanToggle,
                "TransToggle" => TransToggle,
                "WordRomanToggle" => WordRomanToggle,
                _ => null,
            };
            if (toggle is null) return;
            toggle.IsChecked = item.IsChecked;
            OnMasterToggleClick(toggle, e);
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

        private void OnCompactSavePicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string tag)
                _ = SaveWithFormatAsync(tag);
        }

        private void OnCompactCopyPicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string tag)
                _ = CopyWithFormatAsync(tag);
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

