// --minimize-test [reportPath] harness: minimize/restore crash hunt.
// Drives its own window through minimize+restore while paused, playing
// (volume 0), and with seeks/rate-changes in flight, through all three
// OS paths (OverlappedPresenter, ShowWindow, WM_SYSCOMMAND SC_MINIMIZE —
// what the caption button sends), across width tiers (500/750/900/1200),
// 20 rounds. Between steps asserts: iconic state took, the dispatcher
// round-trips (UI thread alive), playback state survived, and the
// crash log did not grow. Self-closing. DEBUG-only.
using Microsoft.UI.Windowing;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace AmllTtmlTool.WinUI.Services;

internal static class MinimizeTestCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "minimize-test-report.txt");

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const nint SC_MINIMIZE = 0xF020;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    private static int _ran;

    // Test file for the 1b leg. Env override first (launchers are not trusted
    // to forward args), then D:\Audio\alac.m4a, the project's standard probe.
    private static string ProbeFile()
    {
        var envFile = Environment.GetEnvironmentVariable("AMLL_MINIMIZE_FILE");
        if (!string.IsNullOrWhiteSpace(envFile) && File.Exists(envFile)) return envFile;
        var preferred = @"D:\Audio\alac.m4a";
        return File.Exists(preferred) ? preferred : "D:\\Audio\\alac.m4a";
    }

    public static async Task RunAsync(Window window, AudioEngine audio, string? reportPath)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        var runId = Guid.NewGuid().ToString("N")[..8];
        // The dispatch site lives in Page.Loaded: if it ever fires twice,
        // the second run must not fight the first over the same window.
        if (System.Threading.Interlocked.Exchange(ref _ran, 1) != 0)
        {
            try { System.IO.File.AppendAllText(report, $"SKIP duplicate dispatch runId={runId}\n"); } catch { }
            return;
        }
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(15), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                try { System.IO.File.AppendAllText(report, "WATCHDOG: minimize-test exceeded 15 minutes, exiting.\n"); } catch { }
                Environment.Exit(3);
            }
        });
        try
        {
            Write(report, "=== MINIMIZE TEST ===");
            Write(report, $"runId={runId} pid={Environment.ProcessId}");
            Write(report, $"binary={Environment.ProcessPath ?? "?"}");
            try { Write(report, $"buildUtc={System.IO.File.GetLastWriteTimeUtc(Environment.ProcessPath!).ToString("u")}"); }
            catch (Exception ex) { Write(report, $"buildUtc=? ({ex.GetType().Name})"); }
            Write(report, $"relaunch=$env:AMLL_SELF_CHECK = \"0\"; & \"{Environment.ProcessPath}\"");
            var crashBase = CrashLog.LineCount();
            Write(report, $"crashlog={CrashLog.FilePath} base-lines={crashBase}");
            // Window-bound objects (AppWindow, HWND, DPI) must be read on
            // the UI thread: touching AppWindow from a pool thread throws
            // RPC_E_WRONG_THREAD.
            var init = await OnUiAsync(window, () => (
                Hwnd: WindowNative.GetWindowHandle(window),
                AppW: window.AppWindow,
                Scale: WindowSizing.GetScale(window)));
            if (!init.Ok || init.Value.AppW is null)
            {
                Write(report, "INIT-ERROR: could not read AppWindow on UI thread");
                Write(report, "minimize | FAIL");
                return;
            }
            var hwnd = init.Value.Hwnd;
            var appWindow = init.Value.AppW;
            var scale = init.Value.Scale;
            Write(report, $"hwnd=0x{hwnd:X} scale={scale:F3}");

            var file = ProbeFile();
            if (!File.Exists(file))
            {
                Write(report, $"FILE-ERROR: not found: {file}");
                return;
            }
            await OnUiAsync(window, () => audio.SetVolume(0));
            // Bare-window probes FIRST (no audio loaded, no resize): if the
            // window dies here, audio is innocent and the cause is in window
            // setup (subclass, title bar, backdrop).
            await ProbeOneAsync(window, report, hwnd, appWindow, "showwindow");
            await ProbeOneAsync(window, report, hwnd, appWindow, "syscommand");
            await ProbeOneAsync(window, report, hwnd, appWindow, "presenter-get");
            await ProbeOneAsync(window, report, hwnd, appWindow, "presenter-minimize");
            await audio.LoadAsync(file);
            var dur = audio.Duration.TotalSeconds;
            Write(report, $"loaded duration={dur:F3}s");

            await MinRestoreLegAsync(window, audio, report, hwnd, appWindow);
            await OnUiAsync(window, () =>
            {
                audio.Pause();
                audio.SetPlaybackRate(1.0);
                appWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)Math.Round(1200 * scale), appWindow.Size.Height));
            });

            var paths = new[] { "presenter", "showwindow", "syscommand" };
            var widths = new[] { 500, 750, 900, 1200 };
            var fails = 0;
            var rnd = new Random(7);
            for (var rep = 0; rep < 20; rep++)
            {
                var path = paths[rep % paths.Length];
                var width = widths[rep % widths.Length];
                var playing = rep % 2 == 0;
                // Keep seeks/rate-changes in flight across the minimize:
                // enqueue a burst, then minimize while the queue drains.
                var burstTarget = dur * rnd.NextDouble();
                var burstRate = new[] { 0.5, 1.0, 1.5, 2.0 }[rep % 4];
                Write(report, $"  rep={rep} burst-go");
                await OnUiAsync(window, () =>
                {
                    for (var k = 0; k < 5; k++) audio.SeekTo(burstTarget);
                    audio.SetPlaybackRate(burstRate);
                    if (playing) audio.Play(); else audio.Pause();
                });
                Write(report, $"  rep={rep} burst-done resize-go");
                await OnUiAsync(window, () =>
                {
                    var h = appWindow.Size.Height;
                    appWindow.Resize(new Windows.Graphics.SizeInt32(
                        (int)Math.Round(width * scale), h));
                });
                Write(report, $"  rep={rep} resized");
                await Task.Delay(300);
                fails += await MinRestoreCycleAsync(window, audio, report, hwnd, appWindow, path, rep, width, playing);
            }
            var crashNow = CrashLog.LineCount();
            var crashOk = crashNow == crashBase;
            if (!crashOk) fails++;
            Write(report, $"crashlog lines base={crashBase} now={crashNow} {(crashOk ? "OK" : "FAIL")}");
            await OnUiAsync(window, () =>
            {
                audio.Pause();
                audio.SetPlaybackRate(1.0);
            });
            Write(report, fails == 0 ? "minimize | OK" : $"minimize | FAIL fails={fails}");
        }
        finally
        {
            watchdogCts.Cancel();
        }
    }

    private static async Task ProbeOneAsync(Window window, string report,
        nint hwnd, AppWindow appWindow, string what)
    {
        Write(report, $"probe {what} minimize-go");
        var ok = await OnUiAsync(window, () =>
        {
            if (what == "showwindow") ShowWindow(hwnd, SW_MINIMIZE);
            else if (what == "syscommand") SendMessage(hwnd, WM_SYSCOMMAND, SC_MINIMIZE, nint.Zero);
            else if (what == "presenter-get")
            {
                var kind = appWindow.Presenter.Kind.ToString();
                try { System.IO.File.AppendAllText(report, $"probe presenter-kind={kind}\n"); } catch { }
            }
            else if (what == "presenter-minimize")
            {
                if (appWindow.Presenter is OverlappedPresenter p) p.Minimize();
                else throw new InvalidOperationException("no OverlappedPresenter");
            }
        });
        // The minimize call itself is synchronous; the iconic state lands
        // via the message pump right after (no UI-thread block here).
        await Task.Delay(500);
        Write(report, $"probe {what} minimized ok={ok} iconic={PollSync(() => IsIconic(hwnd))}");
        if (what == "presenter-get") return;
        // Restore from Win32, NOT through the UI dispatcher: while minimized
        // the XAML message loop is idle, so an enqueued restore may never
        // run. SW_RESTORE is the caption button's own path and always
        // works. The window can then pump again for the next steps.
        ShowWindow(hwnd, SW_RESTORE);
        if (what == "presenter-minimize" && appWindow.Presenter is OverlappedPresenter p)
        {
            // The presenter object must agree with reality: keep it in the
            // normal state so a later Minimize()/Restore() pair works.
            try { p.Restore(); } catch { }
        }
        await Task.Delay(300);
        Write(report, $"probe {what} restored iconic={PollSync(() => IsIconic(hwnd))}");
    }

    private static bool PollSync(Func<bool> cond)
    {
        try { return cond(); } catch { return false; }
    }

    // --- 1b: the reported crash. Five OverlappedPresenter.Minimize()
    // Restore() rounds, 2s apart, through all three states (playing at
    // volume 0, paused, narrow width with the center-toggle overflow leg
    // active), each round asserting: iconic state took, the UI dispatcher
    // round-trips while minimized (the process is alive and pumping),
    // playback state survived, and the crash log did not grow. A failing
    // round returns its failure count; the process being DEAD is visible as
    // an empty report tail plus a non-zero exit, which the runner reports.
    private static async Task<int> MinRestoreLegAsync(Window window, AudioEngine audio,
        string report, nint hwnd, AppWindow appWindow)
    {
        Write(report, "=== 1B LEG: minimize/restore x5, playing | paused | narrow-overflow ===");
        var fails = 0;
        var crashBase = CrashLog.LineCount();
        Write(report, $"crashlog base-lines={crashBase}");

        foreach (var (state, width) in new[]
        {
            ("playing", 1200),
            ("paused", 1200),
            ("narrow-overflow-playing", 500),
        })
        {
            // Narrow leg proves the overflow foot is really out: the tier code
            // only shows CenterToggleOverflowButton at level >= 3.
            string overflow = "?";
            await OnUiAsync(window, () =>
            {
                overflow = DescribeOverflow(window);
                appWindow.Resize(new Windows.Graphics.SizeInt32(
                    (int)Math.Round(width * WindowSizing.GetScale(window)), appWindow.Size.Height));
            });
            await Task.Delay(400);
            overflow = (await OnUiAsync(window, () => DescribeOverflow(window))).Value;
            var playing = state.StartsWith("playing", StringComparison.OrdinalIgnoreCase)
                || state.StartsWith("narrow", StringComparison.OrdinalIgnoreCase);
            await OnUiAsync(window, () =>
            {
                audio.SetVolume(0);
                if (playing) audio.Play(); else audio.Pause();
            });
            Write(report, $"  leg state={state} width={width} overflow={overflow} playing={playing}");
            if (width == 500 && overflow != "Visible")
                Write(report, $"  WARN: expected the overflow leg visible at {width}px, got {overflow}");

            for (var i = 1; i <= 5; i++)
            {
                var tag = $"leg={state} round={i}";
                var pre = (await OnUiAsync(window, () =>
                    (IsPlaying: audio.IsPlaying, Pos: audio.Position.TotalSeconds))).Value;
                var minOk = await OnUiAsync(window, () =>
                {
                    if (appWindow.Presenter is OverlappedPresenter p) p.Minimize();
                    else throw new InvalidOperationException("no OverlappedPresenter");
                });
                if (!minOk) { Write(report, $"  {tag} minimize-FAIL"); fails++; continue; }
                if (!await PollAsync(() => IsIconic(hwnd), 2000))
                {
                    Write(report, $"  {tag} not-iconic-FAIL"); fails++; continue;
                }
                // 2s minimized, exactly the reported crash window: the window
                // measures 0x0 here while the clock/SMTC/layout keep running.
                await Task.Delay(2000);
                if (!await RoundTripAsync(window, audio, 3000))
                {
                    Write(report, $"  {tag} minimized-roundtrip-FAIL"); fails++; continue;
                }
                var crashNow = CrashLog.LineCount();
                if (crashNow != crashBase)
                {
                    Write(report, $"  {tag} crash-log-grew base={crashBase} now={crashNow} FAIL");
                    fails++;
                }
                // Restore through Win32: a minimized window does not pump the
                // XAML queue, so an enqueued restore can never run.
                ShowWindow(hwnd, SW_RESTORE);
                if (appWindow.Presenter is OverlappedPresenter op)
                {
                    try { op.Restore(); } catch { }
                }
                if (!await PollAsync(() => !IsIconic(hwnd), 2000))
                {
                    Write(report, $"  {tag} still-iconic-FAIL"); fails++; continue;
                }
                await Task.Delay(2000);
                if (!await RoundTripAsync(window, audio, 3000))
                {
                    Write(report, $"  {tag} restored-roundtrip-FAIL"); fails++; continue;
                }
                var post = (await OnUiAsync(window, () =>
                    (IsPlaying: audio.IsPlaying, Pos: audio.Position.TotalSeconds))).Value;
                if (playing && !post.IsPlaying)
                {
                    Write(report, $"  {tag} playing-lost-FAIL pos={post.Pos:F3}s"); fails++; continue;
                }
                // Playing must actually advance across the round trip; a
                // frozen-at-the-old-position clock is a silent failure.
                if (playing && post.Pos <= pre.Pos + 0.05)
                {
                    Write(report, $"  {tag} position-frozen-FAIL pre={pre.Pos:F3}s post={post.Pos:F3}s");
                    fails++; continue;
                }
                Write(report, $"  {tag} OK playing={post.IsPlaying} pos={post.Pos:F3}s (pre={pre.Pos:F3}s)");
            }
        }
        var end = CrashLog.LineCount();
        Write(report, $"crashlog lines base={crashBase} now={end} {(end == crashBase ? "OK" : "FAIL")}");
        if (end != crashBase) fails++;
        // HarnessHost.Finish() reads the LAST line and requires it to end with
        // "OK"/"FAIL", so any trailing "fails=N" suffix turns a passing run
        // into a reported FAIL. Keep the convention: verdict word last.
        Write(report, fails == 0 ? "1b-leg | OK" : $"1b-leg | FAIL fails={fails}");
        return fails;
    }

    // Reports the visibility of the narrow-tier overflow button, i.e. whether
    // the window is actually in the state that used to break under minimize.
    private static string DescribeOverflow(Window window)
    {
        try
        {
            var page = window.Content as DependencyObject;
            var queue = new Queue<DependencyObject>();
            if (page is not null) queue.Enqueue(page);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is AmllTtmlTool.WinUI.Views.ShellPage shell
                    && TryOverflowVisibility(shell, out var vis)) return vis;
                var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(current);
                for (var i = 0; i < count; i++)
                    queue.Enqueue(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(current, i));
                if (current is AmllTtmlTool.WinUI.Views.ShellPage sp && TryOverflowVisibility(sp, out var d)) return d;
            }
        }
        catch { }
        return "unknown";
    }

    private static bool TryOverflowVisibility(
        AmllTtmlTool.WinUI.Views.ShellPage page, out string visibility)
    {
        visibility = "absent";
        var field = typeof(AmllTtmlTool.WinUI.Views.ShellPage)
            .GetField("CenterToggleOverflowButton",
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Public);
        if (field?.GetValue(page) is not FrameworkElement fe) return false;
        visibility = fe.Visibility.ToString();
        return true;
    }

    // Passive hold for the EXTERNAL winapp-ui driver: loads one file, plays
    // it at volume 0, and sits still until the 10-minute watchdog closes it
    // or the driver is done. The real caption Minimize button, real restore,
    // and real clocks all stay in play, so a crash from the OS path is
    // caught where the user actually saw it.
    public static async Task RunHoldAsync(Window window, AudioEngine audio, string file)
    {
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(10), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Environment.Exit(3);
        });
        try
        {
            if (!File.Exists(file)) return;
            await OnUiAsync(window, () => audio.SetVolume(0));
            await audio.LoadAsync(file);
            await OnUiAsync(window, () => audio.Play());
            System.Diagnostics.Debug.WriteLine($"MINIMIZE-HOLD READY file={file}");
            await Task.Delay(TimeSpan.FromMinutes(9));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("MINIMIZE-HOLD-ERROR " + ex);
        }
    }

    private static async Task<int> MinRestoreCycleAsync(Window window, AudioEngine audio,
        string report, nint hwnd, AppWindow appWindow, string path, int rep, int width, bool playing)
    {
        var tag = $"rep={rep} path={path} w={width} {(playing ? "playing" : "paused")}";
        Write(report, $"  {tag} start");
        // Minimize.
        var minOk = await OnUiAsync(window, () =>
        {
            try { System.IO.File.AppendAllText(report, $"  {tag} in-minimize-dispatch\n"); } catch { }
            if (path == "presenter")
            {
                if (appWindow.Presenter is OverlappedPresenter p) p.Minimize();
                else throw new InvalidOperationException("no OverlappedPresenter");
            }
            else if (path == "showwindow") ShowWindow(hwnd, SW_MINIMIZE);
            else SendMessage(hwnd, WM_SYSCOMMAND, SC_MINIMIZE, nint.Zero);
            try { System.IO.File.AppendAllText(report, $"  {tag} minimize-call-returned\n"); } catch { }
        });
        Write(report, $"  {tag} minimize-dispatched ok={minOk}");
        if (!minOk)
        {
            Write(report, $"  {tag} minimize-dispatch-FAIL");
            return 1;
        }
        if (!await PollAsync(() => IsIconic(hwnd), 2000))
        {
            Write(report, $"  {tag} not-iconic-FAIL");
            return 1;
        }
        Write(report, $"  {tag} iconic");
        // Alive while minimized: dispatcher round-trip + engine readable.
        if (!await RoundTripAsync(window, audio, 3000))
        {
            Write(report, $"  {tag} minimized-roundtrip-FAIL");
            return 1;
        }
        Write(report, $"  {tag} alive-while-minimized");
        await Task.Delay(300);
        // Restore through Win32, never via the UI dispatcher: a minimized
        // WinUI window does not pump the XAML queue, so an enqueued restore
        // can never run (this is what hung earlier runs).
        ShowWindow(hwnd, SW_RESTORE);
        if (appWindow.Presenter is OverlappedPresenter op)
        {
            try { op.Restore(); } catch { }
        }
        if (!await PollAsync(() => !IsIconic(hwnd), 2000))
        {
            Write(report, $"  {tag} still-iconic-FAIL");
            return 1;
        }
        Write(report, $"  {tag} restored");
        if (!await RoundTripAsync(window, audio, 3000))
        {
            Write(report, $"  {tag} restored-roundtrip-FAIL");
            return 1;
        }
        // Playback state survived the round trip.
        var state = await OnUiAsync(window, () => (audio.IsPlaying, audio.Position.TotalSeconds, audio.DecodedFramePosition));
        if (!state.Ok)
        {
            Write(report, $"  {tag} state-read-FAIL");
            return 1;
        }
        if (playing && !state.Value.IsPlaying)
        {
            Write(report, $"  {tag} playing-lost-FAIL pos={state.Value.TotalSeconds:F3}s");
            return 1;
        }
        if (rep % 5 == 0)
            Write(report, $"  {tag} OK pos={state.Value.TotalSeconds:F3}s frame={state.Value.DecodedFramePosition}");
        return 0;
    }

    private static async Task<bool> PollAsync(Func<bool> cond, int timeoutMs)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            try { if (cond()) return true; } catch { }
            await Task.Delay(50);
        }
        try { return cond(); } catch { return false; }
    }

    private static async Task<bool> RoundTripAsync(Window window, AudioEngine audio, int timeoutMs)
    {
        try
        {
            var tcs = new TaskCompletionSource<bool>();
            if (!window.DispatcherQueue.TryEnqueue(() =>
            {
                try { _ = audio.Position.TotalSeconds; _ = audio.IsPlaying; tcs.TrySetResult(true); }
                catch { tcs.TrySetResult(false); }
            }))
                return false;
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return done == tcs.Task && tcs.Task.Result;
        }
        catch { return false; }
    }

    private static async Task<(bool Ok, T Value)> OnUiAsync<T>(Window window, Func<T> func)
    {
        var tcs = new TaskCompletionSource<(bool, T)>();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { tcs.TrySetResult((true, func())); }
            catch { tcs.TrySetResult((false, default!)); }
        }))
        {
            return (false, default!);
        }
        return await tcs.Task;
    }

    private static async Task<bool> OnUiAsync(Window window, Action action)
    {
        var tcs = new TaskCompletionSource<bool>();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); tcs.TrySetResult(true); }
            catch { tcs.TrySetResult(false); }
        }))
        {
            return false;
        }
        return await tcs.Task;
    }

    private static void Write(string report, string text)
    {
        try { System.IO.File.AppendAllText(report, text + "\n"); } catch { }
        System.Diagnostics.Debug.WriteLine(text);
    }
}
