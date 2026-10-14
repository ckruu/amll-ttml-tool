using AmllTtmlTool.Core.Lyrics;
using AmllTtmlTool.Core.Lyrics.Commands;
using AmllTtmlTool.Core.Models;
using AmllTtmlTool.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace AmllTtmlTool.WinUI.Views
{
    public sealed partial class ShellPage : Page
    {
        private readonly Type[] _pages = [typeof(EditPage), typeof(SyncPage), typeof(PreviewPage)];
        private readonly LyricStore _store = new();
        private readonly SelectionService _selection = new();
        private readonly LyricViewStateService _viewState = new();
        private readonly AudioEngine _audio = new();
        private FileIntake? _intake;
        private bool _scrubbing;
        private bool _syncingSlider;
        private readonly SmtcService _smtcSvc = new();
        // True once InitializeComponent has returned. XAML change events
        // fire DURING the load for initial values that differ from the
        // default (TipVolumeSlider Value="50", SpeedSlider Value="1"):
        // fields declared later in the file (TipVolumeStateGlyph, the
        // presets) are still null then, so every change handler below
        // bails until the tree is complete. (The old bar survived this
        // only because VolumeGlyph was declared before VolumeSlider.)
        private bool _xamlLoaded;
#if DEBUG
        internal static string DebugXamlLog { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "playerbar-xaml-error.log");
#endif
        private Services.IFilePickerService? _picker;
        private Services.IShellService? _shell;
        private bool _syncingTipVolume;
        // Null until the first paint: the empty-track key is "", so a ""
        // initial value would skip the first (disabling) paint entirely.
        private string? _trackTipKey;

        public ShellPage()
        {
#if DEBUG
            // Diagnostic trap: a bare "XAML parsing failed." hides the
            // useful part (missing key, line/position). Log the full chain
            // with HResult to a temp file, the debugger output, and the
            // harness report, then rethrow.
            try
            {
                this.InitializeComponent();
            }
            catch (Exception ex)
            {
                try
                {
                    var text = "INITCOMPONENT-FAIL: " + ex.ToString()
                        + "\nHResult=0x" + ex.HResult.ToString("X8");
                    System.IO.File.AppendAllText(DebugXamlLog, text + "\n");
                    System.Diagnostics.Debug.WriteLine(text);
                    if (Services.HarnessHost.IsTest && Services.HarnessHost.Report is not null)
                        System.IO.File.AppendAllText(Services.HarnessHost.Report, text + "\n");
                }
                catch { }
                throw;
            }
#else
            this.InitializeComponent();
#endif
            SetupWindow();
            WireStores();
            ModeNavView.SelectedItem = EditNavItem;
            SidePane.SelectedItem = SidePane.MenuItems[0];
            var window = ((App)Application.Current).MainWindow;
            ModeNavView.SelectedItem = EditNavItem;
            SidePane.SelectedItem = SidePane.MenuItems[0];
            _ = HandleStartupFileAsync();
        }

        private void SetupWindow()
        {
            _xamlLoaded = true;
            var window = ((App)Application.Current).MainWindow;
            _windowHwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            window.SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
#if DEBUG
            // Screenshot hook: AMLL_REQUESTED_THEME=Light|Dark forces the
            // app theme for Light/Dark evidence runs (HighContrast stays a
            // manual OS-level check — it cannot be forced per-app).
            var themeEnv = Environment.GetEnvironmentVariable("AMLL_REQUESTED_THEME")?.Trim().ToLowerInvariant();
            if ((themeEnv is "light" or "dark") && window.Content is FrameworkElement themeRoot)
                themeRoot.RequestedTheme = themeEnv == "light"
                    ? Microsoft.UI.Xaml.ElementTheme.Light
                    : Microsoft.UI.Xaml.ElementTheme.Dark;
#endif
            window.SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(TitleBarArea);
            // Min-track-size subclass (verified safe while minimized: the
            // subclass only answers WM_GETMINMAXINFO and never measures the
            // client area, so a 0x0 minimized window cannot reach it).
            WindowSizing.SetMinSize(window, 600, 480);
            window.SizeChanged += (_, _) => UpdateCaptionInsets();
            TitleBarArea.SizeChanged += (_, _) => UpdateCaptionInsets();
            // Centering recompute runs after every layout pass (not just resizes):
            // multi-pass cascades (star redistribution after a collapse, ellipsis
            // settling) only reach final numbers on the LAST pass, and only
            // LayoutUpdated fires for pure-reposition passes. The handler is
            // read-only plus a layout-neutral transform set, so it cannot loop.
            RootGrid.LayoutUpdated += (_, _) => UpdateCenterOffset();
            // Slider input must survive the capture boundary. Pressing the
            // thumb captures the pointer; releasing OUTSIDE the slider (or the
            // OS canceling the capture) never raises the Slider's
            // PointerReleased, which used to leave _scrubbing true forever —
            // the clock then stopped moving the thumb and the final seek was
            // dropped, so dragging to the beginning moved the thumb while play
            // did not start from the beginning. All four events end in the same
            // commit. Registered with handledEventsToo because the Slider marks
            // these handled itself.
        SeekSlider.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnSeekPointerPressed), true);
        SeekSlider.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(OnSeekPointerMoved), true);
            SeekSlider.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler(OnSeekPointerReleased), true);
            SeekSlider.AddHandler(UIElement.PointerCaptureLostEvent,
                new PointerEventHandler(OnSeekPointerCaptureLost), true);
            SeekSlider.AddHandler(UIElement.PointerCanceledEvent,
                new PointerEventHandler(OnSeekPointerCanceled), true);
            // Right click / Shift+F10 / touch long-press on the track button
            // opens the picker immediately (never the tip) and swallows any
            // default context menu.
            TrackButton.ContextRequested += OnTrackContextRequested;
            // Esc closes either tip from any focus (tunneling PreviewKeyDown
            // so a focused slider/button can't swallow it). Verified by UIA:
            // with IsLightDismissEnabled=false the tip does NOT close on Esc
            // by itself.
            RootGrid.PreviewKeyDown += OnRootPreviewKeyDown;
            // Speed button: right click cycles presets (ContextRequested),
            // middle click resets to 1x. The Button swallows pointer events,
            // so the middle-button pair registers handledEventsToo and acts
            // on release over the button only.
            RateButton.ContextRequested += OnSpeedContextRequested;
            RateButton.AddHandler(UIElement.PointerPressedEvent,
                new PointerEventHandler(OnSpeedPointerPressed), true);
            RateButton.AddHandler(UIElement.PointerReleasedEvent,
                new PointerEventHandler(OnSpeedPointerReleased), true);
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
                // Re-armed. Necessary: without it the caption insets go stale on
                // every move/resize. Safe now because UpdateCaptionInsets and
                // UpdateCenterOffset both bail on HasRealSize(), which covers
                // the minimize stub and the 160x28 transitional state where
                // IsIconic alone reported false and killed the process.
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
                            Services.HarnessHost.Finish();
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                // Audio-test harness: --audio-test <folder> [reportPath].
                // Drives the real AudioEngine path per file (silent volume-0
                // playback, seeks), writes a report, self-closes. DEBUG-only.
                // Mode/report come from the environment when set (launchers
                // are not trusted to forward args); the host injects them as
                // args so this dispatch chain stays unchanged.
                var audioArgs = Services.HarnessHost.ResolveArgs(Environment.GetCommandLineArgs());
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
                            Services.HarnessHost.Finish();
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
                            Services.HarnessHost.Finish();
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
                            Services.HarnessHost.Finish();
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
                            Services.HarnessHost.Finish();
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
                var minHoldIdx = Array.IndexOf(audioArgs, "--minimize-ui-hold");
                if (minHoldIdx >= 0)
                {
                    var minHoldFile = minHoldIdx + 1 < audioArgs.Length ? audioArgs[minHoldIdx + 1] : "";
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(800);
                        var win = ((App)Application.Current).MainWindow;
                        await Services.MinimizeTestCheck.RunHoldAsync(win, _audio, minHoldFile);
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
                            Services.HarnessHost.Finish();
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                // Gain-test harness: --gain-test [reportPath]. Offline gain
                // verification (sine RMS, mute zeros, ramp zipper guard) plus
                // the read-only proof that the OS endpoint volume is never
                // touched. Self-closes. DEBUG-only.
                var gainIdx = Array.IndexOf(audioArgs, "--gain-test");
                if (gainIdx >= 0)
                {
                    var gainReport = gainIdx + 1 < audioArgs.Length ? audioArgs[gainIdx + 1] : null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            await Services.GainTestCheck.RunAsync(win, _audio, gainReport);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.GainTestCheck.DefaultReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            Services.HarnessHost.Finish();
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                // Seek-drag harness: --seek-drag <file> [reportPath]. External
                // driver performs a continuous 1px drag while the app records
                // per-frame (pointer X, text, thumb value). Target mode: stays
                // open until the driver signals done. DEBUG-only.
                var seekDragIdx = Array.IndexOf(audioArgs, "--seek-drag");
                if (seekDragIdx >= 0)
                {
                    var seekDragFile = seekDragIdx + 1 < audioArgs.Length ? audioArgs[seekDragIdx + 1] : "";
                    var seekDragReport = seekDragIdx + 2 < audioArgs.Length ? audioArgs[seekDragIdx + 2] : null;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(800);
                        var win = ((App)Application.Current).MainWindow;
                        await Services.SeekDragCheck.RunTargetAsync(win, _audio, seekDragFile, seekDragReport);
                    });
                }
                // Focus-test harness: --focus-test [reportPath]. Measures how
                // often the per-frame paint path runs in four window states
                // (focused / unfocused / covered / minimized) by sampling
                // CompositionTarget.Rendering callbacks only. DEBUG-only.
                var focusIdx = Array.IndexOf(audioArgs, "--focus-test");
                if (focusIdx >= 0)
                {
                    // Two forms:
                    //   --focus-test [reportPath]            four-state cadence
                    //   --focus-test rate <secs> [reportPath] unfocused text rate
                    // The runner appends the report path last (via env), so in
                    // "rate" mode it lands AFTER the seconds number, never right
                    // after the mode flag.
                    var rateMode = focusIdx + 1 < audioArgs.Length &&
                        audioArgs[focusIdx + 1] == "rate";
                    var focusReport = rateMode
                        ? (focusIdx + 3 < audioArgs.Length ? audioArgs[focusIdx + 3] : null)
                        : (focusIdx + 1 < audioArgs.Length ? audioArgs[focusIdx + 1] : null);
                    var secs = 10;
                    if (rateMode && focusIdx + 2 < audioArgs.Length &&
                        int.TryParse(audioArgs[focusIdx + 2], out var parsedSecs)) secs = parsedSecs;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            if (rateMode)
                                await Services.FocusTestCheck.RunUnfocusedAsync(win, _audio, focusReport, secs);
                            else
                                await Services.FocusTestCheck.RunAsync(win, _audio, focusReport);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.FocusTestCheck.DefaultReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            Services.HarnessHost.Finish();
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                // SMTC-test harness: --smtc-test <file> [reportPath].
                // End-to-end OS transport surface verification via the
                // system session manager (metadata, status, timeline).
                // Self-closes. DEBUG-only.
                var smtcIdx = Array.IndexOf(audioArgs, "--smtc-test");
                if (smtcIdx >= 0)
                {
                    var smtcFile = smtcIdx + 1 < audioArgs.Length ? audioArgs[smtcIdx + 1] : "";
                    var smtcReport = smtcIdx + 2 < audioArgs.Length ? audioArgs[smtcIdx + 2] : null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            await Services.SmtcTestCheck.RunAsync(win, _audio, _smtcSvc, smtcFile, smtcReport);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.SmtcTestCheck.DefaultReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            Services.HarnessHost.Finish();
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                // Minimize-test harness: --minimize-test [reportPath].
                // Minimize/restore crash hunt across tiers, states, and OS
                // paths (presenter, ShowWindow, WM_SYSCOMMAND). Self-closes.
                // DEBUG-only.
                var minIdx = Array.IndexOf(audioArgs, "--minimize-test");
                if (minIdx >= 0)
                {
                    var minReport = minIdx + 1 < audioArgs.Length ? audioArgs[minIdx + 1] : null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            await Services.MinimizeTestCheck.RunAsync(win, _audio, minReport);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.MinimizeTestCheck.DefaultReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            Services.HarnessHost.Finish();
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                // Playerbar-test harness: --playerbar-test <folder>
                // [reportPath] (self-driving: real controls on the UI thread
                // via the ShellPage test hooks) and --playerbar-ui-hold
                // <file> (passive target for the external winapp-ui driver:
                // loads one file paused at 0 and stays open; the watchdog
                // closes it) plus --playerbar-synth-hold <case> (synthetic
                // track, tip left open for screenshots). Self-closing /
                // watchdog-closed. DEBUG-only.
                var playerbarIdx = Array.IndexOf(audioArgs, "--playerbar-test");
                if (playerbarIdx >= 0)
                {
                    var playerbarFolder = playerbarIdx + 1 < audioArgs.Length ? audioArgs[playerbarIdx + 1] : "";
                    var playerbarReport = playerbarIdx + 2 < audioArgs.Length ? audioArgs[playerbarIdx + 2] : null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            // The page is captured directly: touching
                            // win.Content here would run on the pool thread
                            // (RPC_E_WRONG_THREAD). All UI access inside the
                            // harness goes through OnUiAsync.
                            await Services.PlayerbarTestCheck.RunAsync(win, _audio, this, playerbarFolder, playerbarReport);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.PlayerbarTestCheck.DefaultReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            Services.HarnessHost.Finish();
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                var playerbarHoldIdx = Array.IndexOf(audioArgs, "--playerbar-ui-hold");
                if (playerbarHoldIdx >= 0)
                {
                    var playerbarHoldFile = playerbarHoldIdx + 1 < audioArgs.Length ? audioArgs[playerbarHoldIdx + 1] : "";
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(800);
                        var win = ((App)Application.Current).MainWindow;
                        await Services.PlayerbarTestCheck.RunHoldAsync(win, _audio, playerbarHoldFile);
                    });
                }
                // Synthetic tip hold for screenshots: --playerbar-synth-hold
                // <caseIndex> loads a synthetic track (no files) and leaves
                // the track tip open. DEBUG-only.
                var playerbarSynthIdx = Array.IndexOf(audioArgs, "--playerbar-synth-hold");
                if (playerbarSynthIdx >= 0)
                {
                    var synthArg = playerbarSynthIdx + 1 < audioArgs.Length ? audioArgs[playerbarSynthIdx + 1] : "0";
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(800);
                        var win = ((App)Application.Current).MainWindow;
                        await Services.PlayerbarTestCheck.RunSynthHoldAsync(win, _audio, this, synthArg);
                    });
                }
                // Lyrics hold for the Step-5 driver: --load-lyrics <file>
                // imports through the SAME FileIntake.ImportFileAsync funnel
                // as picker/drop/startup/paste, then holds for inspection.
                // DEBUG-only.
                var loadLyricsIdx = Array.IndexOf(audioArgs, "--load-lyrics");
                if (loadLyricsIdx >= 0)
                {
                    var lyricFile = loadLyricsIdx + 1 < audioArgs.Length ? audioArgs[loadLyricsIdx + 1] : "";
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(800);
                        var win = ((App)Application.Current).MainWindow;
                        if (_intake is not null && !string.IsNullOrEmpty(lyricFile))
                            await Services.LyricsCheck.RunHoldAsync(win, _intake, _store, lyricFile);
                    });
                }
                // Self-driving edit verification: --edit-test <file>
                // [report]. Imports, drives chip commits/structure/ribbon/
                // undo-routing/perf/stress/edge through the Test_ hooks,
                // writes the verdict, self-closes. DEBUG-only.
                var editTestIdx = Array.IndexOf(audioArgs, "--edit-test");
                if (editTestIdx >= 0)
                {
                    var editTestFile = editTestIdx + 1 < audioArgs.Length ? audioArgs[editTestIdx + 1] : "";
                    var editTestReport = editTestIdx + 2 < audioArgs.Length ? audioArgs[editTestIdx + 2] : null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            if (_intake is not null)
                                await Services.EditCheck.RunAsync(win, this, _store, _intake, editTestFile, editTestReport);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.EditCheck.DefaultReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            Services.HarnessHost.Finish();
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
                // Self-driving lyrics verification: --lyrics-test <file>
                // [report]. Imports, asserts model/combos/widths/errors/perf,
                // writes the verdict, self-closes. DEBUG-only.
                var lyricsTestIdx = Array.IndexOf(audioArgs, "--lyrics-test");
                if (lyricsTestIdx >= 0)
                {
                    var lyricsTestFile = lyricsTestIdx + 1 < audioArgs.Length ? audioArgs[lyricsTestIdx + 1] : "";
                    var lyricsTestReport = lyricsTestIdx + 2 < audioArgs.Length ? audioArgs[lyricsTestIdx + 2] : null;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(800);
                            var win = ((App)Application.Current).MainWindow;
                            if (_intake is not null)
                                await Services.LyricsCheck.RunAsync(win, this, _store, _intake, lyricsTestFile, lyricsTestReport);
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                System.IO.File.AppendAllText(Services.LyricsCheck.DefaultReportFile,
                                    "HARNESS-ERROR: " + ex.ToString() + "\n");
                            }
                            catch { }
                        }
                        finally
                        {
                            var win = ((App)Application.Current).MainWindow;
                            Services.HarnessHost.Finish();
                            win.DispatcherQueue.TryEnqueue(() => win.Close());
                        }
                    });
                }
#endif
            };
        }

        private nint _windowHwnd;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsIconic(nint hWnd);
#if DEBUG
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(nint hWnd, int nCmdShow);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(nint hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint hWnd, out uint pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint a, uint b, bool c);

        // Foregrounds the app window for frame sampling (headless runs are
        // occluded and CompositionTarget.Rendering barely ticks). Same
        // AttachThreadInput trick as the ps1 drivers; test-only.
        internal bool Test_BringToFront()
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(
                    ((App)Application.Current).MainWindow);
                ShowWindow(hwnd, 9);
                if (GetForegroundWindow() == hwnd) return true;
                var us = GetCurrentThreadId();
                var fg = GetForegroundWindow();
                uint fgTid = 0, fgPid = 0;
                if (fg != nint.Zero) fgTid = GetWindowThreadProcessId(fg, out fgPid);
                var appTid = GetWindowThreadProcessId(hwnd, out _);
                try
                {
                    if (fgTid != 0) AttachThreadInput(us, fgTid, true);
                    AttachThreadInput(us, appTid, true);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    try { AttachThreadInput(us, appTid, false); } catch { }
                    try { if (fgTid != 0) AttachThreadInput(us, fgTid, false); } catch { }
                }
                return GetForegroundWindow() == hwnd;
            }
            catch { return false; }
        }
#endif

        // True while the window is minimized. Windows reports a minimized
        // window as zero-sized, which the title-bar layout math must never
        // consume (and which used to reach AppWindow.TitleBar and hard-kill
        // the process inside Microsoft.UI.Windowing.dll).
        // NOTE: this alone is NOT sufficient. During a restore the OS clears
        // the iconic flag BEFORE the window is re-laid out, so AppWindow.
        // Changed can fire with iconic=false while the size is still the
        // 160x28 minimized stub — that is exactly when the inset read killed
        // the process natively (SEH exit, no managed exception, silent in the
        // crash log). Whatever consumes the size must validate the size too.
        private bool IsMinimized() => !HasRealSize();

        // True when the window has a real, non-degenerate size. A minimized
        // window, and the transient stub states on the way in and out of one,
        // all collapse to the same 160x28 rect. This is the invariant the
        // title-bar math requires: scale, insets and passthrough rects are
        // meaningless for a window the user cannot see.
        private bool HasRealSize()
        {
            try
            {
                var size = ((App)Application.Current).MainWindow.AppWindow?.Size;
                // 160x28 is the documented caption-only minimize stub; anything
                // at or below it carries no client area to lay out.
                return size is { Width: > 160, Height: > 28 };
            }
            catch
            {
                // An AppWindow that cannot be read is not a layoutable window.
                return false;
            }
        }

        private void UpdateCaptionInsets()
        {
            if (XamlRoot is null) return;
            // Guard the boundary: while minimized the window measures a 160x28
            // caption-only stub (and stays there mid-transition), for which
            // inset/region geometry is meaningless. Bail instead of computing
            // degenerate sizes.
            if (IsMinimized()) return;
            var window = ((App)Application.Current).MainWindow;
            if (window.AppWindow is not AppWindow appWindow) return;
            var scale = WindowSizing.GetScale(window);
            // Scale 0 would divide by zero below (never measured yet).
            if (!(scale > 0)) return;
            // Physical -> logical. No hardcoded floor: the system strip width is
            // whatever the OS reports, at whatever DPI the monitor is at.
            var right = appWindow.TitleBar.RightInset / scale;
            var left = appWindow.TitleBar.LeftInset / scale;
            // Negative insets are not a caption strip: Windows deliberately
            // reports negative border values while a customized-titlebar window
            // transitions (observed RightInset = -8 px on every restore from
            // minimize), meaning "draw no border here yet". Feeding that into
            // GridLength and InputNonClientPointerSource.SetRegionRects
            // produced invalid non-client rects and hard-killed the process
            // inside the OS (SEH 0xE0464645: no managed exception, so nothing
            // reached the crash log). Skip the pass; the next Changed with
            // settled geometry applies the real insets.
            if (right < 0 || left < 0) return;
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
                // Guard the boundary: a minimized window measures 0x0, and
                // the clamp math below assumes positive sizes.
                if (IsMinimized()) return;
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
            SetupSmtc();
            _intake = new FileIntake(_store, _audio, window, this, Notify, _smtcSvc, SetImportBusy, AutoEnableToggles);
#if DEBUG
            if (Services.HarnessHost.IsTest)
            {
                _picker = new Services.TestFilePickerService();
                _shell = new Services.TestShellService();
            }
            else
            {
                _picker = new Services.FilePickerService(window);
                _shell = new Services.ShellService();
            }
#else
            _picker = new Services.FilePickerService(window);
            _shell = new Services.ShellService();
#endif
            _store.Changed += RefreshChrome;
            _store.Notice += Notify;
            // Unfocused/occluded/minimized: CompositionTarget.Rendering stops
            // firing, so the per-frame paint dies. The engine's own 250ms clock
            // keeps running on this dispatcher (proven while minimized by the
            // minimize harness); riding its PositionChanged keeps the text
            // alive. Text ONLY (see RefreshPositionDisplayUnfocused): while
            // unfocused, an invisible thumb must not cost layout.
            _audio.PositionChanged += () =>
            {
                if (!_windowActive) RefreshPositionDisplayUnfocused();
            };
            _audio.PositionChanged += RefreshAudioChrome;
            _audio.TrackEnded += RefreshAudioChrome;
            EditBar.NewLineRequested += () =>
            {
                // Inserts after the selected line (or at the end) and opens
                // the new line's insert-word box, like the original New.
                if (_attachedEditPage is not null)
                    _attachedEditPage.AddLineAfterSelected();
                else
                    _store.Execute(new AddLineCommand(
                        _store.Document.Lines.Count, EditLine.New()));
            };
            EditBar.ImportRequested += () => _ = OpenLyricAsync();
            EditBar.BindSelection(_selection, _store, _viewState);
            RefreshChrome();
            RefreshAudioChrome();
            LoadMasterToggles();
            _viewState.Changed += SyncPillsFromViewState;
            ApplyRememberedVolume();
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
            UndoMenuItem.IsEnabled = _store.CanUndo;
            RedoMenuItem.IsEnabled = _store.CanRedo;
            CompactUndoMenuItem.IsEnabled = _store.CanUndo;
            CompactRedoMenuItem.IsEnabled = _store.CanRedo;
        }

        private static string Stamp(TimeSpan t) =>
            $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";

        private void RefreshAudioChrome()
        {
            // PositionText and the slider thumb move on every rendered frame
            // (OnRendering, same clock); everything else here changes rarely.
            DurationText.Text = Stamp(_audio.Duration);
            // Single source of truth: the engine's playback state (which also
            // drives the Windows media controls) owns the glyph, the tooltip
            // and the automation name. Paused/stopped/ended -> Play; playing
            // -> Pause. No global Space binding: Space is reserved for word
            // stamping. Media-key presses land here through the SMTC
            // ButtonPressed -> play()/pause() -> RefreshAudioChrome path.
            PlayGlyph.Glyph = _audio.IsPlaying ? "\uE769" : "\uE768";
            var playName = _audio.IsPlaying ? "Pause" : "Play";
            ToolTipService.SetToolTip(PlayButton, playName);
            AutomationProperties.SetName(PlayButton, playName);
            PlayButton.IsEnabled = _audio.HasAudio;
            SeekSlider.IsEnabled = _audio.HasAudio;
            if (_audio.HasAudio && _audio.Duration > TimeSpan.Zero)
            {
                // Slider domain is milliseconds (see the XAML note): the
                // thumb's double ms value maps 1:1 onto the displayed value, so
                // nothing is lost to rounding or to a per-pixel scale.
                SeekSlider.Maximum = _audio.Duration.TotalMilliseconds;
            }
            // New track (or unload): deterministic reset. The engine raises
            // no event for a load by itself, so without this the old Value
            // and anchors linger until the next thumb-pixel change paints
            // over them.
            var chromePath = _audio.HasAudio ? _audio.AudioPath : "";
            if (chromePath != _lastChromePath)
            {
                _lastChromePath = chromePath;
                _lastShownMs = -1;
                _lastThumbPixel = -1;
                _syncingSlider = true;
                SeekSlider.Value = 0;
                _syncingSlider = false;
            }
            RefreshTrackTip();
            RefreshSpeedTip();
            UpdateSmtc();
        }

        private long _lastShownMs = -1;
        private bool _windowActive = true;
        private string _lastChromePath = "\0";

        // Per-frame position display: extrapolate the published snapshot,
        // repaint only when the millisecond text actually changes. The thumb
        // follows at most every 100ms (it needs no ms precision, and every
        // thumb write costs layout). Gated work only; allocations happen on
        // actual change, never per frame.
        //
        // Per-frame position display. Measured: CompositionTarget.Rendering
        // fires at ~200/s whether the window is focused, merely unfocused,
        // fully covered, or minimized, so it is the ONLY painter in every
        // state and no focus gate is needed for correctness. The gate that
        // used to sit here (_windowActive) is what throttled the text to 4
        // updates/second without focus; the unfocused PositionChanged leg
        // below stays as a cheap fallback for platforms where Rendering does
        // stop, and both write through the same anchors so they cannot fight.
        //
        // Painting at full rate in every state does add UI-thread work, which
        // historically perturbed the 30s clock drift when the engine's 250ms
        // tick shared this dispatcher. That is fixed at the source rather than
        // by throttling the display: the engine tick now runs on a dedicated
        // thread (see AudioEngine), so UI work can no longer touch snapshot
        // cadence.
        private void OnRendering(object? sender, object? e)
        {
            if (!_audio.HasAudio) return;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Services.RenderStats.Frames++;
            // A drag in flight is painted straight from the thumb, never from
            // the clock: the clock publishes the last ~100ms-throttled seek,
            // and while playing each live SeekTo restarts the output and arms
            // the startup gate (a zero-slope snapshot), so the clock would
            // freeze at the pre-drag value until release. The thumb is the
            // user's intent and the newest truth available, so it leads.
            if (_scrubbing)
            {
                PaintScrubPosition();
                Services.RenderStats.HandlerTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                return;
            }
            var pos = Services.PositionClock.ExtrapolateNow();
            var ms = (long)Math.Round(pos.TotalMilliseconds);
            // The display may only be held flat against clock WOBBLE, never
            // against a real backward seek. A rebase that corrects
            // QPC-vs-hardware skew can land a few milliseconds behind the last
            // painted value, but a user seek to the beginning is a jump of
            // minutes backwards — clamping that away froze the position text at
            // the pre-seek value, so dragging the slider to the start moved the
            // thumb while the clock (and playback) appeared to stay near the
            // end. Anything past the wobble window is a genuine seek: repaint
            // it and re-anchor immediately.
            const long wobbleMs = 200;
            var steppedBack = _lastShownMs >= 0 && ms < _lastShownMs;
            if (steppedBack && _lastShownMs - ms <= wobbleMs)
            {
                ms = _lastShownMs;
                steppedBack = false;
            }
            if (_lastShownMs < 0 || ms != _lastShownMs)
            {
                _lastShownMs = ms;
                PositionText.Text = Stamp(pos);
                Services.RenderStats.TextUpdates++;
            }
            // Thumb: write it only when its rounded DEVICE-PIXEL position
            // actually changes, in every window state (focused, unfocused,
            // covered, minimized — Rendering fires in all of them). On a
            // 4-minute track across ~800px that is ~4px/second, so this is
            // ~4 writes/second instead of one per frame - the thumb needs no
            // ms precision and every write costs a layout pass. Computing it
            // from geometry (rather than a fixed 100ms interval) means the
            // write happens exactly when the visible position would change,
            // never sooner.
            // NOTE: this block must NOT reuse the `ms != _lastShownMs` text
            // gate above. The text anchor is already settled there, so that
            // condition is always false here — sharing it froze the thumb
            // at 0 while the text kept moving.
            if (!_scrubbing && _audio.Duration > TimeSpan.Zero)
            {
                var pixel = ThumbPixel(ms);
                if (pixel >= 0 && pixel != _lastThumbPixel)
                {
                    _lastThumbPixel = pixel;
                    _syncingSlider = true;
                    SeekSlider.Value = ms;
                    _syncingSlider = false;
                }
            }
            Services.RenderStats.HandlerTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        private int _lastThumbPixel = -1;

        // Rounded track-pixel position of a time. -1 when unmeasurable, which
        // makes the caller treat the next value as a change.
        private int ThumbPixel(long ms)
        {
            var width = SeekSlider.ActualWidth;
            var total = _audio.Duration.TotalMilliseconds;
            if (!(width > 0) || !(total > 0)) return -1;
            return (int)Math.Round(ms / total * width);
        }

        // Text only, for the unfocused case. Writing SeekSlider.Value while the
        // window is not visible invalidates layout for a thumb the user cannot
        // see, and that extra layout work on the UI thread is what the 30s
        // clock drift integrates (5-10ms perturbation vs 1.4ms clean): the
        // position text alone keeps the clock honest at 4Hz with no layout
        // churn. The focused path keeps painting the thumb per frame.
        private void RefreshPositionDisplayUnfocused()
        {
            if (!_audio.HasAudio) return;
            var pos = Services.PositionClock.ExtrapolateNow();
            var ms = (long)Math.Round(pos.TotalMilliseconds);
            if (_lastShownMs >= 0 && ms < _lastShownMs)
            {
                if (ms > _lastShownMs - 2000) ms = _lastShownMs;
                else _lastShownMs = ms;
            }
            if (_lastShownMs < 0 || ms != _lastShownMs)
            {
                _lastShownMs = ms;
                PositionText.Text = Stamp(pos);
            }
        }

        // Paints an in-flight drag straight from the thumb. The slider domain
        // is milliseconds, so the thumb value IS the displayed value - there is
        // no conversion, no rounding, and nothing waits on the throttled audio
        // seek. Both display anchors are set here so the handover at release is
        // seamless: CommitScrub publishes a snapshot at exactly this position,
        // so the first post-release paint comes out equal and the wobble clamp
        // does not reject it, and the thumb gate is not re-armed. Never writes
        // the slider, so it cannot feed back into itself.
        private void PaintScrubPosition()
        {
            var thumbMs = (long)Math.Round(Math.Clamp(
                SeekSlider.Value, 0, _audio.Duration.TotalMilliseconds));
            if (thumbMs != _lastShownMs)
            {
                _lastShownMs = thumbMs;
                PositionText.Text = Stamp(TimeSpan.FromMilliseconds(thumbMs));
                Services.RenderStats.TextUpdates++;
            }
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
            if (file is not null) await _intake.ImportFileAsync(file);
        }

        private void SetImportBusy(bool busy)
        {
            ImportProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ImportProgress.IsActive = busy;
        }

        // Deviation from the original (noted in PORTING-NOTES): the original
        // never changes these toggles on import. We switch a toggle ON once
        // per import when the document actually carries that data and the
        // toggle is off, so the imported content is visible, not a surprise.
        private void AutoEnableToggles(EditDocument doc)
        {
            if (doc.Lines.Any(l => !string.IsNullOrWhiteSpace(l.TranslatedLyric))
                && !_viewState.ShowTranslation)
                _viewState.ShowTranslation = true;
            if (doc.Lines.Any(l => !string.IsNullOrWhiteSpace(l.RomanLyric))
                && !_viewState.ShowRomanization)
                _viewState.ShowRomanization = true;
            if (doc.Lines.Any(l => l.Words.Any(w => !string.IsNullOrWhiteSpace(w.RomanWord)))
                && !_viewState.ShowWordRomanization)
                _viewState.ShowWordRomanization = true;
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

        // Step-4 focus semantics (decided Phase 1): a TextBox holding
        // UNCOMMITTED text owns Ctrl+Z/Y (its own text undo) — the
        // accelerator stays unhandled so the key reaches the box. Once the
        // text is committed (or equals the model), the accelerator runs the
        // document undo/redo and is marked handled.
        private void OnUndoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsFocusDirty()) return;
            if (DoUndo()) args.Handled = true;
        }

        private void OnRedoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            if (IsFocusDirty()) return;
            if (DoRedo()) args.Handled = true;
        }

        private bool IsFocusDirty() =>
            (_attachedEditPage?.IsFocusedBoxDirty() ?? false) || EditBar.IsFocusedDirty();

        private bool DoUndo()
        {
            var beforeId = _selection.SelectedLineId;
            var beforeIndex = _attachedEditPage?.SelectedLineIndex() ?? -1;
            if (!_store.Undo()) return false;
            RevealAfterHistory(beforeId, beforeIndex);
            return true;
        }

        private bool DoRedo()
        {
            var beforeId = _selection.SelectedLineId;
            var beforeIndex = _attachedEditPage?.SelectedLineIndex() ?? -1;
            if (!_store.Redo()) return false;
            RevealAfterHistory(beforeId, beforeIndex);
            return true;
        }

        // After a document undo/redo, select and scroll to the line that
        // changed so the effect is visible: the pre-step primary line when
        // it still exists, else the same index (covers delete/add undo).
        private void RevealAfterHistory(string? beforeId, int beforeIndex)
        {
            if (_attachedEditPage is null) return;
            var lines = _store.Document.Lines;
            if (beforeId is not null && lines.Any(l => l.Id == beforeId))
            {
                var at = lines.ToList().FindIndex(l => l.Id == beforeId);
                _attachedEditPage.SelectLineAt(at);
                _attachedEditPage.ScrollLineIntoView(at);
                // Anchor the primary only when it moved (preserves a surviving
                // word selection: SelectLine would clear it).
                if (_selection.SelectedLineId != beforeId)
                    _selection.SelectLine(beforeId);
                return;
            }
            if (lines.Count == 0) return;
            var at2 = Math.Clamp(beforeIndex < 0 ? 0 : beforeIndex, 0, lines.Count - 1);
            _attachedEditPage.SelectLineAt(at2);
            _attachedEditPage.ScrollLineIntoView(at2);
            if (_selection.SelectedLineId != lines[at2].Id)
                _selection.SelectLine(lines[at2].Id);
        }

        private Task SaveAsync(bool apple) => _intake?.SaveAsync(apple) ?? Task.CompletedTask;

        private Task SaveWithFormatAsync(string format) =>
            SaveAsync(format == "apple");

        private Task CopyWithFormatAsync(string format) =>
            _intake?.CopyTtmlAsync(format == "apple") ?? Task.CompletedTask;

        private void OnTrackButtonClick(object sender, RoutedEventArgs e)
        {
            // Opening one tip closes the other; the trigger toggles. The X
            // and Esc are the tip's own (IsLightDismissEnabled=false).
            if (TrackTip.Target is null) TrackTip.Target = TrackButton;
            if (!TrackTip.IsOpen) SpeedTip.IsOpen = false;
            TrackTip.IsOpen = !TrackTip.IsOpen;
            if (TrackTip.IsOpen)
            {
                RefreshTrackTip();
                EnqueueArtistFit();
            }
        }

        private void OnTrackContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            args.Handled = true;
            _ = OpenAudioViaPickerAsync();
        }

        private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape
                && (TrackTip.IsOpen || SpeedTip.IsOpen))
            {
                TrackTip.IsOpen = false;
                SpeedTip.IsOpen = false;
                e.Handled = true;
            }
        }

        private async Task OpenAudioViaPickerAsync()
        {
            if (_picker is null || _intake is null) return;
            var path = await _picker.PickOpenAudioAsync();
            if (string.IsNullOrEmpty(path)) return;
            StorageFile? file;
            try
            {
                file = await StorageFile.GetFileFromPathAsync(path);
            }
            catch
            {
                return;
            }
            await _intake.OpenAudioFileAsync(file);
            // The tip stays open and updates in place for the new file.
            RefreshAudioChrome();
            RefreshTrackTip();
        }

        private async void OnTipOpenFileClick(object sender, RoutedEventArgs e) =>
            await OpenAudioViaPickerAsync();

        private void OnTipExplorerClick(object sender, RoutedEventArgs e)
        {
            if (_shell is null || string.IsNullOrEmpty(_audio.AudioPath)) return;
            _shell.ShowInExplorer(_audio.AudioPath);
        }

        private void OnTipVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_xamlLoaded || _syncingTipVolume) return;
            // The slider position IS the app's own level (0-100). It is
            // applied as in-app gain only; the OS endpoint volume is never
            // read or written. Dragging while muted unmutes at that level
            // (SetVolumePercent clears mute above 0); dragging to 0 shows
            // the muted glyph while LastNonZeroVolume is kept for restore.
            _audio.SetVolumePercent(e.NewValue);
            if (!Services.AppSettings.SilentMode && e.NewValue > 0)
                Services.AppSettings.LastNonZeroVolume = e.NewValue;
            PersistVolume();
            UpdateTipVolumeGlyph();
        }

        // Mute is the state button: click mutes an audible level, click
        // restores exactly the last non-zero level (no jumping). A 0 slider
        // shows muted and also restores the last non-zero level.
        private void OnTipVolumeStateClick(object sender, RoutedEventArgs e)
        {
            var audible = !_audio.IsMuted && _audio.VolumePercent > 0;
            if (audible)
            {
                _audio.SetMuted(true);
            }
            else
            {
                var restore = Services.AppSettings.LastNonZeroVolume;
                _syncingTipVolume = true;
                try
                {
                    TipVolumeSlider.Value = restore;
                }
                finally
                {
                    _syncingTipVolume = false;
                }
                _audio.SetVolumePercent(restore);
                _audio.SetMuted(false);
            }
            PersistVolume();
            UpdateTipVolumeGlyph();
        }

        // Glyph comes from Core.Audio.VolumeGlyph (pure, unit-tested). The
        // mapping was VERIFIED BY RENDERING (glyph-chart screenshot), not by
        // doc names: E74F mute, E993 one wave, E994 two, E995 three. Glyph is
        // a string property — assign REAL codepoints, never XAML entities.
        private void UpdateTipVolumeGlyph()
        {
            var glyph = Core.Audio.VolumeGlyph.ForLevel(_audio.VolumePercent, _audio.IsMuted);
            if (TipVolumeStateGlyph.Glyph != glyph) TipVolumeStateGlyph.Glyph = glyph;
            var muted = _audio.IsMuted || _audio.VolumePercent <= 0;
            ToolTipService.SetToolTip(TipVolumeStateButton, muted ? "Unmute" : "Mute");
            AutomationProperties.SetName(TipVolumeStateButton, muted ? "Unmute" : "Mute");
        }

        // Repaints the tip from engine state. Key-guarded: text/cover/enabled
        // state only rebuild when the track changes; the slider and glyph
        // follow the live level on every audio-chrome refresh.
        private void RefreshTrackTip()
        {
            var has = _audio.HasAudio;
            var key = has
                ? _audio.AudioPath + "\0" + _audio.CodecName + "\0"
                    + string.Join(";", _audio.ProbeTags.Values) + "\0"
                    + (_audio.Cover?.Bytes.Length ?? 0)
                : "";
            if (key != _trackTipKey)
            {
                _trackTipKey = key;
                if (!has)
                {
                    TrackTitleText.Text = "-";
                    TrackArtistText.Text = "-";
                    TrackCodecText.Text = "-";
                    TrackCoverArt.Visibility = Visibility.Collapsed;
                    TrackCoverPlaceholder.Visibility = Visibility.Visible;
                }
                else
                {
                    var raw = new Dictionary<string, string>(
                        _audio.ProbeTags, StringComparer.OrdinalIgnoreCase);
                    var fileName = System.IO.Path.GetFileNameWithoutExtension(_audio.AudioPath);
                    var meta = Core.Audio.AudioMetadata.ParseAudioTrackMetadata(raw, fileName);
                    TrackTitleText.Text = Core.Audio.TrackDisplay.TitleFor(meta);
                    var artist = Core.Audio.TrackDisplay.ArtistOrUnknown(meta);
                    var container = System.IO.Path.GetExtension(_audio.AudioPath).TrimStart('.');
                    if (string.IsNullOrEmpty(container)) container = "?";
                    TrackArtistText.Text = artist;
                    TrackCodecText.Text = "- " + container.ToUpperInvariant() + " "
                        + Core.Audio.TrackDisplay.CodecLabel(_audio.CodecName);
                    FitArtistLine();
                    _ = LoadTipCoverAsync(_audio.Cover?.Bytes, key);
                }
                TipOpenFileButton.IsEnabled = true;
                TipExplorerButton.IsEnabled = has;
                TipVolumeStateButton.IsEnabled = has;
                TipVolumeSlider.IsEnabled = has;
            }
            _syncingTipVolume = true;
            try
            {
                TipVolumeSlider.Value = has ? _audio.VolumePercent : 0;
            }
            finally
            {
                _syncingTipVolume = false;
            }
            UpdateTipVolumeGlyph();
        }

        // Artist/codec hug: the artist ellipsizes to a computed MaxWidth so
        // the codec sits right after it and stays fully visible. Two-phase:
        // phase 1 caps to a safe 80px so the first layout pass can never
        // overflow the 336px-capped presenter; phase 2 (one layout pass
        // later) measures the real content width and opens the cap to fit.
        // No SizeChanged watcher and no per-frame work: the window min
        // width is 600px, so the capped tip always fits and only the track
        // change (plus tip open) matters.
        private double _lastArtistMax = double.NaN;

        private void FitArtistLine()
        {
            TrackArtistText.MaxWidth = 80;
            _lastArtistMax = 80;
            EnqueueArtistFit();
        }

        private void EnqueueArtistFit()
        {
            var key = _trackTipKey;
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (key != _trackTipKey) return;
                    // Synchronous layout first: without this the read below
                    // can see pre-text widths and the cap sticks too small
                    // (the next track then inherits a starved artist box).
                    TipContentRoot.UpdateLayout();
                    ApplyArtistMax(TipContentRoot.ActualWidth);
                }
                catch { }
            });
        }

        // Shared computation: cap the artist so artist + 6px gap + codec
        // fit the measured content width minus cover column (64 + 12 gap).
        // Returns false when there is nothing to apply yet (not laid out).
        private bool ApplyArtistMax(double rootW)
        {
            if (!(rootW > 0)) return false;
            TrackCodecText.Measure(new Windows.Foundation.Size(
                double.PositiveInfinity, double.PositiveInfinity));
            var max = Math.Max(40, rootW - 64.0 - 12.0 - 6.0
                - TrackCodecText.DesiredSize.Width);
            if (Math.Abs(max - _lastArtistMax) > 0.5)
            {
                _lastArtistMax = max;
                TrackArtistText.MaxWidth = max;
            }
            return true;
        }

        private async Task LoadTipCoverAsync(byte[]? bytes, string? key)
        {
            if (bytes is null || bytes.Length == 0)
            {
                TrackCoverArt.Visibility = Visibility.Collapsed;
                TrackCoverPlaceholder.Visibility = Visibility.Visible;
                return;
            }
            try
            {
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                // The track changed while decoding: drop the stale art.
                if (key != _trackTipKey) return;
                TrackCoverBrush.ImageSource = bitmap;
                TrackCoverPlaceholder.Visibility = Visibility.Collapsed;
                TrackCoverArt.Visibility = Visibility.Visible;
            }
            catch
            {
                TrackCoverArt.Visibility = Visibility.Collapsed;
                TrackCoverPlaceholder.Visibility = Visibility.Visible;
            }
        }

        private void OnPlayPauseClick(object sender, RoutedEventArgs e)
        {
            if (_audio.IsPlaying) _audio.Pause();
            else _audio.Play();
            RefreshAudioChrome();
        }

        // Restores the remembered level and mute state. Called once at startup
        // and re-applied after every audio load: the level belongs to the app,
        // not to a track, so nothing about opening a file may disturb it.
        // (Slice A: the bar has no volume UI; the engine level is still
        // applied. Slice B moves the slider + mute button into the track tip.)
        private void ApplyRememberedVolume()
        {
            var stored = Services.AppSettings.Volume;
            var muted = Services.AppSettings.Mute;
            _audio.SetMuted(false);
            _audio.SetVolumePercent(stored);
            _audio.SetMuted(muted);
        }

        private void PersistVolume()
        {
            // A harness run must never overwrite the user's remembered level.
            if (Services.AppSettings.SilentMode) return;
            Services.AppSettings.Volume = _audio.VolumePercent;
            Services.AppSettings.Mute = _audio.IsMuted;
        }

        // --- speed tip ------------------------------------------------------
        private bool _syncingSpeed;
        private bool _speedMiddleArmed;

        private void OnSpeedButtonClick(object sender, RoutedEventArgs e)
        {
            if (SpeedTip.Target is null) SpeedTip.Target = RateButton;
            if (!SpeedTip.IsOpen) TrackTip.IsOpen = false;
            SpeedTip.IsOpen = !SpeedTip.IsOpen;
            if (SpeedTip.IsOpen) RefreshSpeedTip();
        }

        private void OnSpeedContextRequested(UIElement sender, ContextRequestedEventArgs args)
        {
            // Right click / Shift+F10 / touch long-press: cycle presets
            // without opening the tip.
            args.Handled = true;
            CycleSpeedPreset();
        }

        private void OnSpeedPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (e.GetCurrentPoint(RateButton).Properties.IsMiddleButtonPressed)
                _speedMiddleArmed = true;
        }

        private void OnSpeedPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_speedMiddleArmed) return;
            _speedMiddleArmed = false;
            // Reset on release only while the pointer is still over the
            // button, and swallow the event so nothing else (autoscroll,
            // paste) fires.
            var at = e.GetCurrentPoint(RateButton).Position;
            if (at.X >= 0 && at.Y >= 0
                && at.X <= RateButton.ActualWidth && at.Y <= RateButton.ActualHeight)
            {
                ResetSpeedTo1x();
                e.Handled = true;
            }
        }

        // Right-click cycle target (same method the pointer, keyboard and
        // touch paths share, and the test hooks call).
        private void CycleSpeedPreset()
        {
            _audio.SetPlaybackRate(Core.Audio.SpeedDisplay.CycleFrom(_audio.PlaybackRate));
            SyncSpeedUI();
        }

        // Middle-click reset (shared by the release handler and the hooks).
        private void ResetSpeedTo1x()
        {
            _audio.SetPlaybackRate(1.0);
            SyncSpeedUI();
        }

        private void OnSpeedSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_xamlLoaded || _syncingSpeed) return;
            _audio.SetPlaybackRate(e.NewValue);
            SyncSpeedUI();
        }

        private void OnSpeedPresetClick(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton button
                && double.TryParse(button.Tag as string,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var rate))
                SpeedSlider.Value = rate;
        }

        private void OnKeepPitchChanged(object sender, RoutedEventArgs e)
        {
            // Empty-state display (unchecked while no track) must not touch
            // the engine setting.
            if (!_audio.HasAudio) return;
            _audio.SetKeepPitch(SpeedKeepPitch.IsChecked == true);
        }

        // Repaints the button text, the slider, its tooltip and the preset
        // states from the engine. The button uses the simplified form (".5x")
        // and never resizes (fixed MinWidth in XAML).
        private void SyncSpeedUI()
        {
            var rate = _audio.PlaybackRate;
            var text = Core.Audio.SpeedDisplay.Format(rate);
            if ((RateButton.Content as string) != text) RateButton.Content = text;
            // The automation name carries the live text (a Button exposes no
            // ValuePattern, so a static Name would hide the state from UIA).
            var spoken = "Playback speed " + text;
            if (AutomationProperties.GetName(RateButton) != spoken)
                AutomationProperties.SetName(RateButton, spoken);
            ToolTipService.SetToolTip(SpeedSlider, text);
            if (Math.Abs(SpeedSlider.Value - rate) > 0.0005)
            {
                _syncingSpeed = true;
                try
                {
                    SpeedSlider.Value = Math.Clamp(rate, SpeedSlider.Minimum, SpeedSlider.Maximum);
                }
                finally
                {
                    _syncingSpeed = false;
                }
            }
            var match = Core.Audio.SpeedDisplay.PresetIndex(rate);
            var presets = new ToggleButton[]
                { SpeedPreset025, SpeedPreset05, SpeedPreset1, SpeedPreset15, SpeedPreset125 };
            for (var i = 0; i < presets.Length; i++)
                presets[i].IsChecked = i == match;
        }

        // Enabled state follows the loaded track. Empty state: the slider,
        // the checkbox (shown unchecked) and all presets greyed out.
        private void RefreshSpeedTip()
        {
            var has = _audio.HasAudio;
            RateButton.IsEnabled = has;
            SpeedSlider.IsEnabled = has;
            SpeedKeepPitch.IsEnabled = has;
            var presets = new ToggleButton[]
                { SpeedPreset025, SpeedPreset05, SpeedPreset1, SpeedPreset15, SpeedPreset125 };
            foreach (var p in presets)
                p.IsEnabled = has;
            if (!has)
            {
                SpeedKeepPitch.IsChecked = false;
                return;
            }
            if (SpeedKeepPitch.IsChecked != _audio.KeepPitch)
                SpeedKeepPitch.IsChecked = _audio.KeepPitch;
            SyncSpeedUI();
        }

        // The single place a drag/scrub is committed: it always issues the
        // slider's CURRENT value. The throttled live seek during a drag can
        // only ever drop intermediate values, never the final one, and the
        // capture-lost/canceled paths below funnel here for exactly that
        // reason.
        private void CommitScrub()
        {
            if (!_scrubbing) return;
            EndScrub();
            // Slider domain is milliseconds; the engine speaks seconds.
            _audio.SeekTo(SeekSlider.Value / 1000.0);
            RefreshAudioChrome();
        }

        private void OnSeekPointerReleased(object sender, PointerRoutedEventArgs e) => CommitScrub();

        // Release outside the slider / OS capture cancel: no PointerReleased
        // arrives, but the thumb already shows the user's intent, so commit it
        // rather than abandoning the gesture.
        private void OnSeekPointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
            CommitScrub();

        private void OnSeekPointerCanceled(object sender, PointerRoutedEventArgs e) =>
            CommitScrub();

        internal void BeginScrub() => _scrubbing = true;

        internal void EndScrub() => _scrubbing = false;

        // --- fine scrub ---------------------------------------------------
        // 1 pixel is ~212ms on a 4-minute track across ~900px, so sub-pixel
        // aiming at ms level is impossible by hand. Holding Shift scales the
        // pointer travel by 10 (1px -> ~21ms); the arrow keys already step
        // whole milliseconds because the slider domain is ms, and Shift makes
        // them 10ms steps. One hand, no extra UI.
        private const double FineScrubScale = 0.1;
        private bool _fineScrub;
        private double _scrubAnchorMs;
        private double _scrubAnchorX;

        private bool IsFineScrubDown()
        {
            // Held Shift state, read at press so a drag that starts fine stays
            // fine even if the modifier is released mid-gesture.
            var state = Windows.UI.Core.CoreWindow.GetForCurrentThread()
                .GetKeyState(Windows.System.VirtualKey.Shift);
            return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        }

        // ms per track pixel, measured from the control itself rather than
        // assumed: a 4-minute track across ~900px is ~212ms/px, but that is
        // DPI- and layout-dependent, and fine scrub depends on it directly.
        private double MsPerPixel()
        {
            var w = SeekSlider.ActualWidth;
            var total = _audio.Duration.TotalMilliseconds;
            if (!(w > 0) || !(total > 0)) return 0;
            return total / w;
        }

        private void OnSeekPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            BeginScrub();
            _fineScrub = IsFineScrubDown();
            var point = e.GetCurrentPoint(SeekSlider);
            _scrubAnchorX = point.Position.X;
            _scrubAnchorMs = SeekSlider.Value;
        }

        // Fine scrub: while Shift was held at press, translate the pointer
        // delta through the measured ms-per-pixel and scale it by 1/10, then
        // claim the event so the Slider's own full-rate track-follow does not
        // fight it. Pointer travel therefore buys ~21ms per pixel instead of
        // ~212ms. Unshifted drags never reach this handler.
        private void OnSeekPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_scrubbing || !_fineScrub) return;
            var msPerPixel = MsPerPixel();
            if (msPerPixel <= 0) return;
            var x = e.GetCurrentPoint(SeekSlider).Position.X;
            var deltaMs = (x - _scrubAnchorX) * msPerPixel * FineScrubScale;
            var want = Math.Clamp(_scrubAnchorMs + deltaMs, 0, SeekSlider.Maximum);
            if (Math.Abs(want - SeekSlider.Value) >= 1) SeekSlider.Value = want;
            e.Handled = true;
        }

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
            // seeks exactly (see above). The DISPLAY never waits for this -
            // it is painted from the thumb in PaintScrubPosition.
            var now = Environment.TickCount64;
            if (now - _lastLiveSeekMs < 100) return;
            _lastLiveSeekMs = now;
            // Slider domain is milliseconds; the engine speaks seconds.
            _audio.SeekTo(SeekSlider.Value / 1000.0);
        }

        // Eye button ("Jump to current lyric line"): the hook for later.
        // The handler a future lyric list adds will receive the current
        // source-time from the position clock. No subscribers today, so the
        // invoke is a no-op; the stub notice below keeps current behavior and
        // no lyric lookup/scroll/selection is built here.
        public event Action<TimeSpan>? JumpToCurrentLineRequested;

        private void OnLocateClick(object sender, RoutedEventArgs e)
        {
            JumpToCurrentLineRequested?.Invoke(Services.PositionClock.GetPositionNow());
            Notify("Line locate arrives with the lyric list.");
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
                    names, LyricStore.AudioExtensions, LyricStore.LyricExtensions);
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
                var lyrics = files.Where(f => LyricStore.IsLyricFile(f.Name)).ToList();
                var audios = files.Where(f => LyricStore.IsAudioFile(f.Name)).ToList();
                if (lyrics.Count == 0 && audios.Count == 0)
                {
                    Notify($"Unsupported file{(files.Count == 1 ? string.Empty : "s")}: " +
                        string.Join(", ", files.Select(f => f.Name).DefaultIfEmpty(items[0].Name)));
                    return;
                }
                // One dirty-confirm for the lyric side; a cancelled lyric
                // open still lets a co-dropped audio file through.
                var openLyric = lyrics.Count > 0 && await ConfirmDirtyAsync("Open");
                if (openLyric) await _intake.ImportFileAsync(lyrics[0]);
                if (audios.Count > 0)
                {
                    await _intake.OpenAudioFileAsync(audios[0]);
                    // A paused drop-open must still light up play/seek/tips:
                    // the engine raises no event for a load by itself.
                    RefreshAudioChrome();
                }
            }
            catch { }
        }

        private void SetupSmtc()
        {
            var window = ((App)Application.Current).MainWindow;
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            _smtcSvc.Attach(window.DispatcherQueue, hwnd, () => _audio.Play(), () => _audio.Pause(), RefreshAudioChrome);
        }

        private void UpdateSmtc()
        {
            // Called on every audio chrome refresh (including each engine
            // tick via PositionChanged): the service pushes status on
            // change and throttles the timeline itself. Position comes from
            // the millisecond clock so the OS surface agrees with in-app.
            _smtcSvc.RefreshPlayback(_audio.HasAudio, _audio.IsPlaying,
                Services.PositionClock.ExtrapolateNow(), _audio.Duration);
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
            if (ContentFrame.Content is EditPage editPage)
            {
                if (_attachedEditPage is not null && !ReferenceEquals(_attachedEditPage, editPage))
                {
                    _attachedEditPage.ImportRequested -= OnEditImportRequested;
                    _attachedEditPage.Detach();
                }
                _attachedEditPage = editPage;
                editPage.ImportRequested += OnEditImportRequested;
                editPage.Attach(_store, _selection, _viewState);
            }
        }

        private EditPage? _attachedEditPage;

        private void OnEditImportRequested() => _ = OpenLyricAsync();

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
            if (sender is not ToggleButton tb) return;
            PaintPillToggle(tb, tb.IsChecked == true);
            // UIA TogglePattern flips raise Checked/Unchecked but NOT Click:
            // without this mirror, a pattern-driven flip changes only the
            // pill's IsChecked while the service (and the rows) stay stale
            // (observed: pills On, card rows hidden, no error). Idempotent
            // (service setters early-out on equal), so no loop with Click.
            var on = tb.IsChecked == true;
            if (tb == RomanToggle) _viewState.ShowRomanization = on;
            else if (tb == TransToggle) _viewState.ShowTranslation = on;
            else if (tb == WordRomanToggle) _viewState.ShowWordRomanization = on;
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
            RomanToggle.IsChecked = _viewState.ShowRomanization;
            TransToggle.IsChecked = _viewState.ShowTranslation;
            WordRomanToggle.IsChecked = _viewState.ShowWordRomanization;
        }

        private void SyncPillsFromViewState()
        {
            RomanToggle.IsChecked = _viewState.ShowRomanization;
            TransToggle.IsChecked = _viewState.ShowTranslation;
            WordRomanToggle.IsChecked = _viewState.ShowWordRomanization;
        }

        private void OnMasterToggleClick(object sender, RoutedEventArgs e)
        {
            _viewState.ShowRomanization = RomanToggle.IsChecked == true;
            _viewState.ShowTranslation = TransToggle.IsChecked == true;
            _viewState.ShowWordRomanization = WordRomanToggle.IsChecked == true;
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

        private void OnUndoClick(object sender, RoutedEventArgs e) => DoUndo();
        private void OnRedoClick(object sender, RoutedEventArgs e) => DoRedo();

        private void OnSelectAllClick(object sender, RoutedEventArgs e) =>
            _attachedEditPage?.SelectAllLines();
        private void OnDeselectAllClick(object sender, RoutedEventArgs e) =>
            _attachedEditPage?.ClearEditSelection();
        private void OnDeleteSelectionClick(object sender, RoutedEventArgs e) =>
            _attachedEditPage?.DeleteSelected();

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

#if DEBUG
        // --- lyrics test hooks (DEBUG only) ---------------------------------
        // Model/render facts for the Step-5 lyrics driver. Model hooks read
        // the store (UI thread via OnUiAsync in checks); render hooks read
        // the live EditPage.
        internal int Test_LyricLineCount() => _store.Document.Lines.Count;
        internal int Test_LyricWordCount(int line) => _store.Document.Lines[line].Words.Count;
        internal string Test_WordText(int line, int word) =>
            _store.Document.Lines[line].Words[word].Text;
        internal string Test_WordRoman(int line, int word) =>
            _store.Document.Lines[line].Words[word].RomanWord;
        internal int Test_WordEmptyBeat(int line, int word) =>
            _store.Document.Lines[line].Words[word].EmptyBeat;
        internal string Test_LineTimes(int line) =>
            $"{_store.Document.Lines[line].StartTimeMs},{_store.Document.Lines[line].EndTimeMs}";
        internal string Test_LineFlags(int line)
        {
            var l = _store.Document.Lines[line];
            return $"{l.IsBg},{l.IsDuet},{l.IgnoreSync}";
        }
        internal string Test_WordTimes(int line, int word)
        {
            var w = _store.Document.Lines[line].Words[word];
            return $"{w.StartTimeMs},{w.EndTimeMs}";
        }
        internal string Test_WordFlags(int line, int word)
        {
            var w = _store.Document.Lines[line].Words[word];
            return $"{w.Obscene},{w.EmptyBeat}";
        }
        internal double Test_TimedInsertLineAfter(int index)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _attachedEditPage?.InsertLineAfter(index);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
        internal double Test_TimedDeleteLineAt(int index)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _attachedEditPage?.DeleteLineAt(index);
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
        internal string Test_LineTranslation(int line) =>
            _store.Document.Lines[line].TranslatedLyric;
        internal string Test_LineRomanization(int line) =>
            _store.Document.Lines[line].RomanLyric;
        internal string Test_LyricFileName() => _store.FileName;
        internal bool Test_EmptyStateVisible() =>
            _attachedEditPage?.IsEmptyStateVisible == true;
        internal void Test_SelectLine(int index) => _attachedEditPage?.SelectLineAt(index);
        internal int Test_SelectedLine() => _attachedEditPage?.SelectedLineIndex() ?? -1;
        internal int Test_RealizedLineCount() => _attachedEditPage?.RealizedLineCount() ?? -1;
        internal int Test_RealizedTextBoxCount() => _attachedEditPage?.RealizedTextBoxCount() ?? -1;
        internal void Test_ScrollLineIntoView(int index) => _attachedEditPage?.ScrollLineIntoView(index);
        internal int Test_RowVisibility(int line, int which) =>
            _attachedEditPage?.RowVisibility(line, which) ?? -1;
        internal string Test_ChipWidths(int line, int word) =>
            _attachedEditPage?.ChipWidths(line, word) ?? "nopage";
        internal string Test_SpaceLabel(int line, int word) =>
            _attachedEditPage?.SpaceLabel(line, word) ?? "nopage";
        internal string Test_ChipUnitWidth(int line, int word) =>
            _attachedEditPage?.ChipUnitWidth(line, word) ?? "nopage";
        internal string Test_CardPads(int line) =>
            _attachedEditPage?.CardPads(line) ?? "nopage";
        internal string Test_AddButtonDy(int line) =>
            _attachedEditPage?.AddButtonDy(line) ?? "nopage";
        internal bool Test_EnterSpaceEdit(int line, int word) =>
            _attachedEditPage?.EnterSpaceEdit(Test_WordId(line, word) ?? "") ?? false;
        internal string Test_ChipGap(int line, int word) =>
            _attachedEditPage?.ChipGap(line, word) ?? "nopage";
        internal string Test_RowGap(int line) =>
            _attachedEditPage?.RowGap(line) ?? "nopage";
        internal Task<string> Test_ShotCard(int line, string path) =>
            _attachedEditPage?.ShotCardAsync(line, path) ?? Task.FromResult("nopage");
        internal Task<string> Test_ShotList(string path) =>
            _attachedEditPage?.ShotListAsync(path) ?? Task.FromResult("nopage");
        internal Task<string> Test_ShotPage(string path) =>
            _attachedEditPage?.ShotPageAsync(path) ?? Task.FromResult("nopage");
        internal Task<string> Test_ShotChip(int line, int word, string path) =>
            _attachedEditPage?.ShotChipAsync(line, word, path) ?? Task.FromResult("nopage");
        internal string Test_IndicatedIndices() =>
            _attachedEditPage?.IndicatedIndices() ?? "nopage";
        internal string Test_SelectedIndices() =>
            _attachedEditPage?.SelectedIndices() ?? "nopage";
        internal string Test_ServiceLineIdx() =>
            _attachedEditPage?.Test_ServiceLineIdx() ?? "nopage";
        internal string Test_RealizedNumbers() =>
            _attachedEditPage?.Test_RealizedNumbers() ?? "nopage";
        internal string Test_DocShape() =>
            _attachedEditPage?.Test_DocShape() ?? "nopage";
        internal string Test_DocFingerprint() =>
            _attachedEditPage?.Test_DocFingerprint() ?? "nopage";
        internal bool Test_DragBeginLines(int[] indices) =>
            _attachedEditPage?.Test_DragBeginLines(indices) ?? false;
        internal bool Test_DragBeginWords(int line, int[] words) =>
            _attachedEditPage?.Test_DragBeginWords(line, words) ?? false;
        internal string Test_DragToLineIndex(int index) =>
            _attachedEditPage?.Test_DragToLineIndex(index) ?? "nopage";
        internal string Test_DragToWord(int line, int word, bool after) =>
            _attachedEditPage?.Test_DragToWord(line, word, after) ?? "nopage";
        internal string Test_DragEnd(bool copy) =>
            _attachedEditPage?.Test_DragEnd(copy) ?? "nopage";
        internal string Test_DragCancel() =>
            _attachedEditPage?.Test_DragCancel() ?? "nopage";
        internal string Test_DragState() =>
            _attachedEditPage?.Test_DragState() ?? "nopage";
        internal void Test_BuildDragDoc(int lines, int wordsPer) =>
            _attachedEditPage?.Test_BuildDragDoc(lines, wordsPer);
        internal string Test_DragEdgeScroll(bool top, int ticks) =>
            _attachedEditPage?.Test_DragEdgeScroll(top, ticks) ?? "nopage";
        internal string Test_FirstChipX(int line) =>
            _attachedEditPage?.FirstChipX(line) ?? "nopage";
        internal string Test_CardHeight(int line) =>
            _attachedEditPage?.CardHeight(line) ?? "nopage";
        internal string Test_CardGaps() =>
            _attachedEditPage?.CardGaps() ?? "nopage";
        internal void Test_BuildSelectDoc()
        {
            // 80 lines cycling plain/BG/V2/both with identical content rows
            // (3 words + trans/roman), for indicator/badge/geometry checks.
            var lines = new List<EditLine>();
            for (var li = 0; li < 80; li++)
            {
                var line = EditLine.New();
                line.StartTimeMs = li * 2000;
                line.EndTimeMs = li * 2000 + 1800;
                line.IsBg = li % 4 == 1 || li % 4 == 3;
                line.IsDuet = li % 4 == 2 || li % 4 == 3;
                line.TranslatedLyric = $"trans {li}";
                line.RomanLyric = $"roman {li}";
                for (var wi = 0; wi < 3; wi++)
                {
                    var word = EditWord.New();
                    word.StartTimeMs = li * 2000 + wi * 500;
                    word.EndTimeMs = li * 2000 + wi * 500 + 400;
                    word.Text = $"w{li}_{wi}";
                    word.RomanWord = $"r{li}_{wi}";
                    line.Words.Add(word);
                }
                lines.Add(line);
            }
            _store.Execute(new BulkReplaceCommand(lines, new List<EditMetadata>()));
        }
        internal bool Test_FocusChip(int line, int word, bool roman) =>
            _attachedEditPage?.FocusChip(line, word, roman) ?? false;
        internal string Test_ChipBoxText(int line, int word, bool roman) =>
            _attachedEditPage?.ChipBoxText(line, word, roman) ?? "nopage";
        internal bool Test_SetChipBoxText(int line, int word, bool roman, string text) =>
            _attachedEditPage?.SetChipBoxText(line, word, roman, text) ?? false;
        internal double Test_TypeChipChar(int line, int word, bool roman, char ch) =>
            _attachedEditPage?.TypeChipChar(line, word, roman, ch) ?? -1;
        internal string Test_TypeBreakdown() =>
            _attachedEditPage?.TypeBreakdown() ?? "nopage";
        internal bool Test_CommitChip(int line, int word, bool roman) =>
            _attachedEditPage?.CommitChip(line, word, roman) ?? false;
        internal void Test_EscapeChip(int line, int word, bool roman) =>
            _attachedEditPage?.EscapeChip(line, word, roman);
        internal int Test_ChipCaret(int line, int word, bool roman) =>
            _attachedEditPage?.ChipCaret(line, word, roman) ?? -1;
        internal bool Test_ChipIsFocused(int line, int word, bool roman) =>
            _attachedEditPage?.ChipIsFocused(line, word, roman) ?? false;
        internal void Test_SetChipComposing(int line, int word, bool roman, bool composing) =>
            _attachedEditPage?.SetChipComposing(line, word, roman, composing);
        internal bool Test_HasUncommittedEdit() =>
            _attachedEditPage?.HasUncommittedEdit() ?? false;
        internal int Test_UndoDepth() => _store.Document.UndoDepth;
        internal string Test_LineFieldText(int line, bool translation) =>
            _attachedEditPage?.LineFieldText(line, translation) ?? "nopage";
        internal void Test_SetLineFieldText(int line, bool translation, string text) =>
            _attachedEditPage?.SetLineFieldText(line, translation, text);
        internal void Test_CommitLineField(int line, bool translation) =>
            _attachedEditPage?.CommitLineField(line, translation);
        internal void Test_EscapeLineField(int line, bool translation) =>
            _attachedEditPage?.EscapeLineField(line, translation);
        internal bool Test_Undo() => _store.Undo();
        internal bool Test_Redo() => _store.Redo();
        internal string Test_UndoRoute() => IsFocusDirty() ? "textbox" : "document";
        internal bool Test_RouteUndo()
        {
            if (IsFocusDirty()) return false;
            return DoUndo();
        }
        internal bool Test_RouteRedo()
        {
            if (IsFocusDirty()) return false;
            return DoRedo();
        }
        internal bool Test_FocusRibbon(string which) => EditBar.FocusRibbon(which);
        internal int Test_SelectedLineCount() =>
            _attachedEditPage?.SelectedLineCount() ?? -1;
        internal int Test_SelectedWordCount() =>
            _attachedEditPage?.SelectedWordCount() ?? -1;
        internal void Test_SelectLines(int[] indices) =>
            _attachedEditPage?.SelectLines(indices);
        internal void Test_SelectWord(int line, int word) =>
            _attachedEditPage?.SelectWord(line, word);
        internal double Test_TimedAddWord(int line)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var l = _store.Document.Lines[line];
            _attachedEditPage?.AddWordToLine(l.Id, "x");
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
        internal void Test_SelectAllLines() => _attachedEditPage?.SelectAllLines();
        internal void Test_ClearEditSelection() => _attachedEditPage?.ClearEditSelection();
        internal bool Test_ToggleChipWord(int line, int word) =>
            _attachedEditPage?.ToggleChipWord(line, word) ?? false;
        internal bool Test_WordGesture(int line, int word, bool ctrl, bool shift) =>
            _attachedEditPage?.Test_WordGesture(line, word, ctrl, shift) ?? false;
        internal bool Test_LineGesture(int index, bool ctrl, bool shift) =>
            _attachedEditPage?.Test_LineGesture(index, ctrl, shift) ?? false;
        internal string Test_Primary() => _attachedEditPage?.Test_Primary() ?? "nopage";
        internal string Test_SelectedWordIdx() =>
            _attachedEditPage?.Test_SelectedWordIdx() ?? "nopage";
        internal string Test_WordIndicated() =>
            _attachedEditPage?.Test_WordIndicated() ?? "nopage";
        internal string Test_LineFill(int line) =>
            _attachedEditPage?.Test_LineFill(line) ?? "nopage";
        internal string Test_SelectionContrast() =>
            _attachedEditPage?.Test_SelectionContrast() ?? "nopage";
        internal string Test_ChipOutline(int line, int word) =>
            _attachedEditPage?.Test_ChipOutline(line, word) ?? "nopage";
        internal string Test_FocusState() => _attachedEditPage?.Test_FocusState() ?? "nopage";
        internal int Test_GotFocusCount() => _attachedEditPage?.Test_GotFocusCount() ?? -1;
        internal bool Test_EnterWordEdit(int line, int word, bool roman) =>
            _attachedEditPage?.Test_EnterWordEdit(line, word, roman) ?? false;
        internal bool Test_AnchorBox(int line, int word, bool roman) =>
            _attachedEditPage?.Test_AnchorBox(line, word, roman) ?? false;

        internal string Test_LineContext(int line, int zone) =>
            _attachedEditPage?.Test_LineContext(line, zone) ?? "nopage";
        internal string Test_WordContext(int line, int word) =>
            _attachedEditPage?.Test_WordContext(line, word) ?? "nopage";
        internal string Test_CardHittestable(int line) =>
            _attachedEditPage?.Test_CardHittestable(line) ?? "nopage";
        internal bool Test_EnterPrimaryWordEdit() =>
            _attachedEditPage?.EnterPrimaryWordEdit() ?? false;
        internal void Test_MoveSelectedLines(int delta) =>
            _attachedEditPage?.MoveSelectedLines(delta);
        internal void Test_DeleteSelected() => _attachedEditPage?.DeleteSelected();
        internal void Test_DeleteLineAt(int index) => _attachedEditPage?.DeleteLineAt(index);
        internal void Test_InsertLineBefore(int index) =>
            _attachedEditPage?.InsertLineBefore(index);
        internal void Test_InsertLineAfter(int index) =>
            _attachedEditPage?.InsertLineAfter(index);
        internal void Test_AddLineAfterSelected() =>
            _attachedEditPage?.AddLineAfterSelected();
        internal void Test_DuplicateSelectedLines() =>
            _attachedEditPage?.DuplicateSelectedLines();
        internal void Test_MovePrimaryLine(int delta) =>
            _attachedEditPage?.MovePrimaryLine(delta);
        internal bool Test_CombineSelectedLines() =>
            _attachedEditPage?.CombineSelectedLines() ?? false;
        internal bool Test_MergeSelectedWords(string lineId) =>
            _attachedEditPage?.MergeSelectedWords(lineId) ?? false;
        internal bool Test_SplitWordAt(string lineId, string wordId, int[] indices) =>
            _attachedEditPage?.SplitWordAt(lineId, wordId, indices) ?? false;
        internal void Test_ReplaceWord(string lineId, string wordId, string target, string replacement) =>
            _attachedEditPage?.ReplaceWord(lineId, wordId, target, replacement);
        internal bool Test_MoveSelectedWordsToNewLine(string anchorLineId) =>
            _attachedEditPage?.MoveSelectedWordsToNewLine(anchorLineId) ?? false;
        internal bool Test_MoveFollowingWordsToNewLine(string lineId, string wordId) =>
            _attachedEditPage?.MoveFollowingWordsToNewLine(lineId, wordId) ?? false;
        internal void Test_AddWordToLine(string lineId, string text) =>
            _attachedEditPage?.AddWordToLine(lineId, text);
        internal bool Test_OpenInsertBox(int lineIndex) =>
            _attachedEditPage?.OpenInsertBox(lineIndex) ?? false;
        internal bool Test_InsertBoxOpen() =>
            _attachedEditPage?.InsertBoxOpen() ?? false;
        internal void Test_SetInsertText(string text) =>
            _attachedEditPage?.SetInsertText(text);
        internal bool Test_ListKey(int key, bool ctrl, bool alt) =>
            _attachedEditPage?.HandleListKey((Windows.System.VirtualKey)key, ctrl, alt) ?? false;
        internal string Test_InsertText() => _attachedEditPage?.InsertText() ?? "nopage";
        internal bool Test_CommitInsertBox() =>
            _attachedEditPage?.CommitInsertBox() ?? false;
        internal bool Test_EscapeInsertBox() =>
            _attachedEditPage?.EscapeInsertBox() ?? false;
        internal bool Test_IsLineRealized(int index) =>
            _attachedEditPage?.IsLineRealized(index) ?? false;
        internal void Test_NewDocument() => _store.NewDocument();
        internal void Test_BuildPerfDoc()
        {
            // Deterministic 300 lines x 10 words for perf probes.
            var lines = new List<EditLine>();
            for (var li = 0; li < 300; li++)
            {
                var line = EditLine.New();
                line.StartTimeMs = li * 2000;
                line.EndTimeMs = li * 2000 + 1800;
                line.TranslatedLyric = $"trans {li}";
                line.RomanLyric = $"roman {li}";
                for (var wi = 0; wi < 10; wi++)
                {
                    var word = EditWord.New();
                    word.StartTimeMs = li * 2000 + wi * 180;
                    word.EndTimeMs = li * 2000 + wi * 180 + 170;
                    word.Text = $"w{li}_{wi}";
                    word.RomanWord = $"r{li}_{wi}";
                    line.Words.Add(word);
                }
                lines.Add(line);
            }
            _store.Execute(new BulkReplaceCommand(lines, new List<EditMetadata>()));
        }
        internal string Test_LineId(int index) => _store.Document.Lines[index].Id;
        internal string Test_WordId(int line, int word) =>
            _store.Document.Lines[line].Words[word].Id;
        internal string Test_RibbonText(string which) => EditBar.RibbonText(which);
        internal void Test_SetRibbonText(string which, string text) =>
            EditBar.SetRibbonText(which, text);
        internal void Test_CommitRibbon(string which) => EditBar.CommitRibbon(which);
        internal bool Test_RibbonEnabled(string which) => EditBar.RibbonEnabled(which);
        internal void Test_ClickRibbonToggle(string which) => EditBar.ClickRibbonToggle(which);
        internal bool Test_DurationMode() => EditBar.DurationMode();
        internal int Test_WordGroupRows() => EditBar.WordGroupRows();
        internal bool Test_WordRomanVisible() => EditBar.WordRomanVisible();
        internal string Test_WordCaption() => EditBar.WordCaption();
        internal double Test_WordGroupWidth() => EditBar.WordGroupWidth();
        internal string Test_MultiState(string which) => EditBar.MultiState(which);
        internal string Test_BarBaselines() => EditBar.BarBaselines();
        internal string Test_BarReport() => EditBar.BarReport();
        internal string Test_SetWindowWidth(int widthDip)
        {
            try
            {
                var window = ((App)Application.Current).MainWindow;
                if (window is null) return "nowindow";
                var scale = XamlRoot.RasterizationScale;
                var size = window.AppWindow.Size;
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)(widthDip * scale), size.Height));
                return $"ok:{widthDip}";
            }
            catch (Exception ex) { return "error:" + ex.GetType().Name; }
        }
        internal void Test_SetRibbonComposing(string which, bool composing) =>
            EditBar.SetComposing(which, composing);
        internal bool Test_ToggleState(string which) => which switch
        {
            "trans" => _viewState.ShowTranslation,
            "roman" => _viewState.ShowRomanization,
            "wordroman" => _viewState.ShowWordRomanization,
            _ => throw new ArgumentException($"Unknown toggle: {which}"),
        };
        internal long Test_FlipToggle(string which)
        {
            // Flip through the service (pill path) and time the sync UI work.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            switch (which)
            {
                case "trans": _viewState.ShowTranslation = !_viewState.ShowTranslation; break;
                case "roman": _viewState.ShowRomanization = !_viewState.ShowRomanization; break;
                case "wordroman": _viewState.ShowWordRomanization = !_viewState.ShowWordRomanization; break;
                default: throw new ArgumentException($"Unknown toggle: {which}");
            }
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        internal Task<string> Test_FrameStats(string which, int durationMs) =>
            _attachedEditPage is null
                ? throw new InvalidOperationException("No edit page.")
                : _attachedEditPage.SampleToggleFramesAsync(which, durationMs);
        internal Task<string> Test_StructuralFrames(string op, int durationMs) =>
            _attachedEditPage is null
                ? throw new InvalidOperationException("No edit page.")
                : _attachedEditPage.SampleStructuralFramesAsync(op, durationMs);
        internal void Test_SetToggle(string which, bool value)
        {
            switch (which)
            {
                case "trans": _viewState.ShowTranslation = value; break;
                case "roman": _viewState.ShowRomanization = value; break;
                case "wordroman": _viewState.ShowWordRomanization = value; break;
                default: throw new ArgumentException($"Unknown toggle: {which}");
            }
        }
        internal bool Test_ImportBusyVisible() =>
            ImportProgress.Visibility == Visibility.Visible;
        // --- player-bar test hooks (DEBUG only) -------------------------------
        // The self-driving harness drives the REAL controls on the UI thread
        // through these: the same handlers the pointer/keyboard paths use.
        internal string Test_PlayName() => AutomationProperties.GetName(PlayButton);
        internal bool Test_PlayEnabled() => PlayButton.IsEnabled;
        internal bool Test_RateEnabled() => RateButton.IsEnabled;
        internal string Test_SpeedText() => RateButton.Content as string ?? "";
        internal double Test_SpeedWidth() => RateButton.ActualWidth;
        internal void Test_SetSpeedSlider(double v) => SpeedSlider.Value = v;
        internal void Test_CycleSpeed() => CycleSpeedPreset();
        internal void Test_MiddleReset() => ResetSpeedTo1x();
        internal bool Test_PresetChecked(int i) => PresetAt(i).IsChecked == true;
        internal bool Test_PresetEnabled(int i) => PresetAt(i).IsEnabled;
        internal void Test_ClickPreset(int i) =>
            OnSpeedPresetClick(PresetAt(i), new RoutedEventArgs());
        internal void Test_SetKeepPitch(bool keep) => SpeedKeepPitch.IsChecked = keep;
        internal bool Test_KeepPitchChecked() => SpeedKeepPitch.IsChecked == true;
        internal bool Test_KeepPitchEnabled() => SpeedKeepPitch.IsEnabled;
        internal bool Test_EngineKeepPitch() => _audio.KeepPitch;
        internal double Test_EngineRate() => _audio.PlaybackRate;
        internal double Test_EngineVolume() => _audio.VolumePercent;
        internal bool Test_EngineMuted() => _audio.IsMuted;
        internal string Test_VolumeGlyph() => TipVolumeStateGlyph.Glyph;
        internal void Test_SetTipSlider(double v) => TipVolumeSlider.Value = v;
        internal double Test_TipSlider() => TipVolumeSlider.Value;
        internal void Test_ClickVolumeState() =>
            OnTipVolumeStateClick(TipVolumeStateButton, new RoutedEventArgs());
        internal void Test_OpenTrackTip()
        {
            // Deterministic open (not a toggle): the matrix polls for laid
            // out bounds right after, which a toggle race would flake.
            if (TrackTip.Target is null) TrackTip.Target = TrackButton;
            SpeedTip.IsOpen = false;
            TrackTip.IsOpen = true;
            RefreshTrackTip();
            EnqueueArtistFit();
        }
        internal void Test_OpenSpeedTip()
        {
            Test_CloseTips();
            OnSpeedButtonClick(RateButton, new RoutedEventArgs());
        }
        internal void Test_CloseTips()
        {
            TrackTip.IsOpen = false;
            SpeedTip.IsOpen = false;
        }
        internal void Test_RefreshChrome() => RefreshAudioChrome();
        // Drives the unfocused fallback paint directly: it must advance the
        // text without fighting the per-frame thumb writes.
        internal void Test_PaintUnfocused() => RefreshPositionDisplayUnfocused();
        internal bool Test_TrackTipOpen() => TrackTip.IsOpen;
        internal bool Test_SpeedTipOpen() => SpeedTip.IsOpen;
        internal string Test_TrackTitle() => TrackTitleText.Text;
        internal string Test_TrackArtist() => TrackArtistText.Text;
        internal string Test_TrackCodec() => TrackCodecText.Text;
        internal bool Test_CoverShown() => TrackCoverArt.Visibility == Visibility.Visible;
        internal bool Test_TipSliderEnabled() => TipVolumeSlider.IsEnabled;
        internal bool Test_OpenFileEnabled() => TipOpenFileButton.IsEnabled;
        internal bool Test_ExplorerEnabled() => TipExplorerButton.IsEnabled;
        internal bool Test_VolumeStateEnabled() => TipVolumeStateButton.IsEnabled;
        internal bool Test_SpeedSliderEnabled() => SpeedSlider.IsEnabled;
        // Tip button boxes for the shared-style asserts (item 2): heights
        // must be identical and the volume button square, loaded or empty.
        internal string Test_TipButtonSizes()
        {
            try
            {
                return $"state={TipVolumeStateButton.ActualWidth:F2}x{TipVolumeStateButton.ActualHeight:F2} "
                    + $"open={TipOpenFileButton.ActualWidth:F2}x{TipOpenFileButton.ActualHeight:F2} "
                    + $"explorer={TipExplorerButton.ActualWidth:F2}x{TipExplorerButton.ActualHeight:F2}";
            }
            catch (Exception ex)
            {
                return "FAIL harness-error " + ex.GetType().Name;
            }
        }
        internal void Test_ClickEye() => OnLocateClick(EyeButton, new RoutedEventArgs());
        // Item 3 row edges (element bounds, not text extents): title row,
        // slider row, buttons row, all in tip space.
        internal string Test_TipRowEdges()
        {
            try
            {
                string Box(FrameworkElement el)
                {
                    var o = el.TransformToVisual(TrackTip)
                        .TransformPoint(new Windows.Foundation.Point(0, 0));
                    return $"{o.X:F1}-{o.X + el.ActualWidth:F1}";
                }
                return $"titlerow=[{Box(TipTitleRow)}] sliderrow=[{Box(TipVolumeSlider)}] "
                    + $"buttonsrow=[{Box(TipButtonsRow)}]";
            }
            catch (Exception ex)
            {
                return "FAIL harness-error " + ex.GetType().Name;
            }
        }
        internal string Test_EyeName() => AutomationProperties.GetName(EyeButton);
        internal bool Test_EyeEnabled() => EyeButton.IsEnabled;
        internal string Test_BarBrush() => PlayerBarBorder.Background switch
        {
            Microsoft.UI.Xaml.Media.SolidColorBrush sb =>
                $"Solid color={sb.Color} opacity={sb.Opacity}",
            var b => (b?.GetType().Name ?? "null") + " UNEXPECTED-TYPE",
        };
        // Resolved background of an open tip's content root (empty string
        // when closed or not found). The template parts live in the popup
        // visual tree, NOT under the inline TeachingTip element — so walk
        // UP from our own content (which is inside the popup when open) to
        // the popup root, then back DOWN to the template's ContentRootGrid.
        internal string Test_TipBrush(string which)
        {
            try
            {
                if (which == "speed" ? !SpeedTip.IsOpen : !TrackTip.IsOpen) return "tip-closed";
                DependencyObject root = which == "speed" ? SpeedSlider : TipContentRoot;
                while (VisualTreeHelper.GetParent(root) is DependencyObject parent) root = parent;
                var queue = new Queue<DependencyObject>();
                queue.Enqueue(root);
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (current is FrameworkElement fe && fe.Name == "ContentRootGrid")
                    {
                        var bg = (fe as Microsoft.UI.Xaml.Controls.Grid)?.Background;
                        return bg switch
                        {
                            Microsoft.UI.Xaml.Media.SolidColorBrush sb =>
                                $"Solid color={sb.Color} opacity={sb.Opacity}",
                            null => "null-bg",
                            _ => bg.GetType().Name + " UNEXPECTED-TYPE",
                        };
                    }
                    var count = VisualTreeHelper.GetChildrenCount(current);
                    for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
                }
                return "no-content-root";
            }
            catch (Exception ex)
            {
                return "FAIL harness-error " + ex.GetType().Name;
            }
        }
        internal void Test_ResizeWindow(int widthDip)
        {
            var window = ((App)Application.Current).MainWindow;
            var scale = Services.WindowSizing.GetScale(window);
            if (!(scale > 0)) scale = 1.0;
            var heightPx = (int)(window.Bounds.Height * scale);
            window.AppWindow?.Resize(new Windows.Graphics.SizeInt32((int)(widthDip * scale), heightPx));
        }
        internal string Test_BarLayout()
        {
            // Bounding boxes in bar space: any pairwise overlap fails, the
            // slider must keep >= 120px, everything must fit inside the bar.
            try
            {
                FrameworkElement[] items =
                [
                    TrackButton, PlayButton, PositionText, SeekSlider,
                    DurationText, EyeButton, RateButton,
                ];
                var rects = new List<(string Name, double L, double T, double R, double B)>();
                foreach (var el in items)
                {
                    var origin = el.TransformToVisual(PlayerBarBorder)
                        .TransformPoint(new Windows.Foundation.Point(0, 0));
                    rects.Add((el.Name, origin.X, origin.Y,
                        origin.X + el.ActualWidth, origin.Y + el.ActualHeight));
                }
                var barW = PlayerBarBorder.ActualWidth;
                var sliderW = SeekSlider.ActualWidth;
                var detail = $"bar={barW:F0} slider={sliderW:F0} "
                    + string.Join(" ", rects.Select(r => $"{r.Name}=[{r.L:F0}-{r.R:F0}]"));
                if (sliderW < 120) return $"FAIL slider-width {sliderW:F0} < 120 ({detail})";
                for (var i = 0; i < rects.Count; i++)
                {
                    var a = rects[i];
                    if (a.L < -0.5 || a.R > barW + 0.5)
                        return $"FAIL {a.Name} outside bar [{a.L:F0},{a.R:F0}] ({detail})";
                    for (var j = i + 1; j < rects.Count; j++)
                    {
                        var b = rects[j];
                        var overlapX = Math.Min(a.R, b.R) - Math.Max(a.L, b.L);
                        var overlapY = Math.Min(a.B, b.B) - Math.Max(a.T, b.T);
                        if (overlapX > 0.5 && overlapY > 0.5)
                            return $"FAIL overlap {a.Name}/{b.Name} ({detail})";
                    }
                }
                return "OK " + detail;
            }
            catch (Exception ex)
            {
                return "FAIL harness-error " + ex.GetType().Name;
            }
        }
        // Bar metrics (item 3): single center line, unified button size,
        // transparent speed idle. TextBlock centers use their bounds.
        internal string Test_BarMetrics()
        {
            try
            {
                FrameworkElement[] items =
                [
                    TrackButton, PlayButton, PositionText, SeekSlider,
                    DurationText, EyeButton, RateButton,
                ];
                var centers = new List<string>();
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var el in items)
                {
                    var origin = el.TransformToVisual(PlayerBarBorder)
                        .TransformPoint(new Windows.Foundation.Point(0, 0));
                    var cy = origin.Y + el.ActualHeight / 2;
                    lo = Math.Min(lo, cy);
                    hi = Math.Max(hi, cy);
                    centers.Add($"{el.Name}={cy:F2}");
                }
                var spread = hi - lo;
                var detail = $"spread={spread:F2} " + string.Join(" ", centers);
                if (spread > 1.0) return $"FAIL center-line {detail}";
                Button[] buttons = [TrackButton, PlayButton, EyeButton, RateButton];
                foreach (var b in buttons)
                {
                    if (Math.Abs(b.ActualWidth - 44) > 0.01 || Math.Abs(b.ActualHeight - 36) > 0.01)
                        return $"FAIL button-size {b.Name}={b.ActualWidth:F1}x{b.ActualHeight:F1} [{detail}]";
                }
                var bg = RateButton.Background as Microsoft.UI.Xaml.Media.SolidColorBrush;
                if (bg is null || bg.Color != Microsoft.UI.Colors.Transparent)
                    return $"FAIL speed-idle-bg {(bg is null ? "not-solid" : bg.Color.ToString())} [{detail}]";
                var barH = PlayerBarBorder.ActualHeight;
                // 50.0 standard (36 content + 6+6 padding + border); 42.0 in
                // the narrow tier (padding 6,2). Both keep every guarantee.
                if (barH < 39.5 || barH > 52.5)
                    return $"FAIL bar-height {barH:F1} [{detail}]";
                return "OK " + detail + $" barH={barH:F1}";
            }
            catch (Exception ex)
            {
                return "FAIL harness-error " + ex.GetType().Name;
            }
        }
        private ToggleButton PresetAt(int i) => i switch
        {
            0 => SpeedPreset025,
            1 => SpeedPreset05,
            2 => SpeedPreset1,
            3 => SpeedPreset15,
            _ => SpeedPreset125,
        };
        // Track-tip bounds for the layout matrix: right edges of every
        // control plus content-root and tip widths, all in TrackTip space.
        // Codec desired width included to prove full visibility.
        internal string Test_TipBounds()
        {
            try
            {
                // Settle synchronously so the read reflects THIS track: run
                // layout, apply the artist fit against the real content
                // width, then re-run layout until the cap takes (bounded:
                // MaxWidth changes need an arrange pass to land in
                // ActualWidth, and one UpdateLayout is not always enough
                // inside a freshly opened popup).
                var layoutPasses = 0;
                TipContentRoot.UpdateLayout();
                ApplyArtistMax(TipContentRoot.ActualWidth);
                for (var i = 0; i < 5; i++)
                {
                    layoutPasses++;
                    TipContentRoot.UpdateLayout();
                    if (TrackArtistText.ActualWidth
                        <= TrackArtistText.MaxWidth + 1.0)
                        break;
                }
                TrackCodecText.Measure(new Windows.Foundation.Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                TrackArtistText.Measure(new Windows.Foundation.Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                TipOpenFileButton.Measure(new Windows.Foundation.Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                TipExplorerButton.Measure(new Windows.Foundation.Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                // True artist desired width, unconstrained (MaxWidth clamps
                // DesiredSize, so measure with it lifted, then restore).
                var savedMax = TrackArtistText.MaxWidth;
                TrackArtistText.MaxWidth = double.PositiveInfinity;
                TrackArtistText.Measure(new Windows.Foundation.Size(
                    double.PositiveInfinity, double.PositiveInfinity));
                var artistTrue = TrackArtistText.DesiredSize.Width;
                TrackArtistText.MaxWidth = savedMax;
                string Box(FrameworkElement el)
                {
                    var o = el.TransformToVisual(TrackTip)
                        .TransformPoint(new Windows.Foundation.Point(0, 0));
                    return $"{o.X:F1}-{o.X + el.ActualWidth:F1}";
                }
                var rootO = TipContentRoot.TransformToVisual(TrackTip)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                var volThumb = FindThumb(TipVolumeSlider);
                string vol = "none";
                if (volThumb is not null)
                {
                    var vo = volThumb.TransformToVisual(TrackTip)
                        .TransformPoint(new Windows.Foundation.Point(0, 0));
                    vol = $"{vo.X:F1}-{vo.X + volThumb.ActualWidth:F1}";
                }
                return $"tipW={TrackTip.ActualWidth:F1} "
                    + $"tipOpen={TrackTip.IsOpen} vol={TipVolumeSlider.Value:F0} "
                    + $"maxW={TrackArtistText.MaxWidth:F0} lastMax={_lastArtistMax:F0} "
                    + $"passes={layoutPasses} "
                    + $"root=[{rootO.X:F1}-{rootO.X + TipContentRoot.ActualWidth:F1}] "
                    + $"title=[{Box(TrackTitleText)}] artist=[{Box(TrackArtistText)}] "
                    + $"codec=[{Box(TrackCodecText)}] codecDesired={TrackCodecText.DesiredSize.Width:F1} "
                    + $"artistDesired={TrackArtistText.DesiredSize.Width:F1} "
                    + $"artistTrue={artistTrue:F1} "
                    + $"openDesired={TipOpenFileButton.DesiredSize.Width:F1} "
                    + $"explorerDesired={TipExplorerButton.DesiredSize.Width:F1} "
                    + $"slider=[{Box(TipVolumeSlider)}] volThumb=[{vol}] "
                    + $"open=[{Box(TipOpenFileButton)}] explorer=[{Box(TipExplorerButton)}] "
                    + $"state=[{Box(TipVolumeStateButton)}]";
            }
            catch (Exception ex)
            {
                return "FAIL harness-error " + ex.GetType().Name;
            }
        }
        // Seek-thumb geometry for the thumb matrix: slider Value/Maximum plus
        // track origin/width and live thumb-center x, all in PlayerBarBorder
        // space. Finds the VISIBLE bar slider by AutomationId (there is
        // exactly one) and its Thumb template part by type.
        internal string Test_ThumbGeometry()
        {
            try
            {
                var origin = SeekSlider.TransformToVisual(PlayerBarBorder)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                var trackLeft = origin.X;
                var trackWidth = SeekSlider.ActualWidth;
                var thumb = FindThumb(SeekSlider);
                var thumbX = double.NaN;
                var thumbW = double.NaN;
                if (thumb is not null)
                {
                    var to = thumb.TransformToVisual(PlayerBarBorder)
                        .TransformPoint(new Windows.Foundation.Point(0, 0));
                    thumbW = thumb.ActualWidth;
                    thumbX = to.X + thumbW / 2;
                }
                return $"value={SeekSlider.Value:F1} max={SeekSlider.Maximum:F1} "
                    + $"trackLeft={trackLeft:F1} trackWidth={trackWidth:F1} thumbX={thumbX:F1} thumbW={thumbW:F1}";
            }
            catch (Exception ex)
            {
                return "FAIL harness-error " + ex.GetType().Name;
            }
        }
        private static Microsoft.UI.Xaml.Controls.Primitives.Thumb? FindThumb(DependencyObject root)
        {
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is Microsoft.UI.Xaml.Controls.Primitives.Thumb thumb) return thumb;
                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
            }
            return null;
        }
#endif
    }
}

