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

            const string file = "D:\\Audio\\alac.m4a";
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
            Write(report, $"minimize | {(fails == 0 ? "OK" : "FAIL")} fails={fails}");
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
