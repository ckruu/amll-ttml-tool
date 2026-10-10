// --seek-test <folder> [reportPath] harness: engine-level seek verification
// for every file (rates x playing/paused, end-of-track count, rapid seeks,
// seek during rate change). --seek-ui-target <file> loads one file paused
// at 0 and stays open for the winapp-ui driver (real clicks/drags/keys).
// Self-closing (test mode) or watchdog-closed (target mode). DEBUG-only.
namespace AmllTtmlTool.WinUI.Services;

internal static class SeekTestCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "seek-test-report.txt");

    private static readonly double[] Rates = [0.5, 1.0, 2.0];
    private static readonly double[] Fracs = [0.25, 0.5, 0.75];

    private static DateTime CrashLogStart()
    {
        try
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amll-ttml-tool-crash.log");
            return System.IO.File.Exists(log) ? System.IO.File.GetLastWriteTimeUtc(log) : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }

    private static void AppendCrashLogTail(string report, DateTime sinceUtc)
    {
        try
        {
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amll-ttml-tool-crash.log");
            if (!System.IO.File.Exists(log)) return;
            if (System.IO.File.GetLastWriteTimeUtc(log) <= sinceUtc) return;
            var text = System.IO.File.ReadAllText(log);
            var tail = text.Length > 2000 ? text[^2000..] : text;
            Write(report, "CRASH-LOG (written during this run):\n" + tail.TrimEnd());
        }
        catch { }
    }

    public static async Task RunAsync(Window window, AudioEngine audio, string folder, string? reportPath)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(20), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                try { System.IO.File.AppendAllText(report, "WATCHDOG: seek-test exceeded 20 minutes, exiting.\n"); } catch { }
                Environment.Exit(3);
            }
        });
        var crashSince = CrashLogStart();
        try
        {
            Write(report, "=== SEEK TEST ===");
            Write(report, $"binary={Environment.ProcessPath ?? "?"}");
            try { Write(report, $"buildUtc={System.IO.File.GetLastWriteTimeUtc(Environment.ProcessPath!).ToString("u")}"); }
            catch (Exception ex) { Write(report, $"buildUtc=? ({ex.GetType().Name})"); }
            Write(report, $"relaunch=$env:AMLL_SELF_CHECK = \"0\"; & \"{Environment.ProcessPath}\"");
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                Write(report, $"FOLDER-ERROR: not found: {folder}");
                return;
            }
            var files = Directory.GetFiles(folder)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Write(report, $"files={files.Length}");
            var table = new List<string>();
            foreach (var file in files)
            {
                string row;
                try { row = await TestFileAsync(window, audio, file); }
                catch (Exception ex) { row = $"file={Name(file)} | HARNESS-ERROR {AudioErrorText.MessageOf(ex)}"; }
                Write(report, row);
                table.Add(row.Split('\n').Last());
            }
            Write(report, "=== TABLE (file | seek matrix | result) ===");
            foreach (var r in table) Write(report, r);
        }
        finally
        {
            AppendCrashLogTail(report, crashSince);
            watchdogCts.Cancel();
        }
    }

    // Self-driving UI target: loads one file paused at 0, then exercises
    // the REAL UI seek path (focused-slider keyboard path and the
    // press/live/release scrub handlers on the real SeekSlider), reads the
    // real PositionText, writes results, and CLOSES the window itself.
    // Nothing to babysit: 90s watchdog, auto-exit, report file.
    public static async Task RunTargetAsync(Window window, AudioEngine audio, string file, string? reportPath)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(90), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Environment.Exit(3);
        });
        var crashSince = CrashLogStart();
        try
        {
            Write(report, "=== SEEK UI TARGET (self-driving, real handlers) ===");
            await OnUiAsync(window, () => audio.SetVolume(0));
            await audio.LoadAsync(file);
            await OnUiAsync(window, () => { audio.SeekTo(0); audio.Pause(); });
            var dur = audio.Duration.TotalSeconds;
            if (!(dur > 1.0))
            {
                Write(report, $"target setup FAILED: duration too short ({dur:F2}s)");
                return;
            }
            AmllTtmlTool.WinUI.Views.ShellPage? page = null;
            Microsoft.UI.Xaml.Controls.Slider? slider = null;
            Microsoft.UI.Xaml.Controls.TextBlock? positionText = null;
            await OnUiAsync(window, () =>
            {
                page = FindShellPage(window);
                slider = FindByName(window.Content as DependencyObject, "SeekSlider") as Microsoft.UI.Xaml.Controls.Slider;
                positionText = FindByName(window.Content as DependencyObject, "PositionText") as Microsoft.UI.Xaml.Controls.TextBlock;
            });
            if (page is null || slider is null || positionText is null)
            {
                Write(report, $"target setup FAILED: page={page is not null} slider={slider is not null} positionText={positionText is not null}");
                return;
            }
            // Keyboard path: real focus + real Value changes (fires the real
            // ValueChanged handler, the same path arrow keys take).
            foreach (var frac in new[] { 0.25, 0.5, 0.75 })
            {
                var target = dur * frac;
                var text = "";
                var pos = 0.0;
                await OnUiAsync(window, () =>
                {
                    slider.Focus(Microsoft.UI.Xaml.FocusState.Keyboard);
                    slider.Value = target;
                });
                await Task.Delay(500);
                await OnUiAsync(window, () => { text = positionText.Text; pos = audio.Position.TotalSeconds; });
                var ok = Math.Abs(pos - target) <= 0.05;
                Write(report, $"  {(ok ? "ok" : "FAIL")} keyboard seek {frac:P0} slider→{target:F3}s text=\"{text}\" pos={pos:F3}s");
            }
            // Drag path: real press/live/release handlers on the real slider.
            await OnUiAsync(window, () => page.BeginScrub());
            for (var i = 1; i <= 5; i++)
            {
                var v = dur * 0.25 + dur * 0.5 * i / 5.0;
                await OnUiAsync(window, () => { slider.Value = v; });
                await Task.Delay(120);
            }
            var dragText = "";
            var dragPos = 0.0;
            await OnUiAsync(window, () =>
            {
                page.EndScrub();
                audio.SeekTo(slider.Value);
            });
            await Task.Delay(500);
            await OnUiAsync(window, () => { dragText = positionText.Text; dragPos = audio.Position.TotalSeconds; });
            var dragOk = Math.Abs(dragPos - dur * 0.75) <= 0.05;
            Write(report, $"  {(dragOk ? "ok" : "FAIL")} drag 25%→75% text=\"{dragText}\" pos={dragPos:F3}s want={(dur * 0.75):F3}s");
            // Playing leg: release-state position keeps advancing.
            await OnUiAsync(window, () => { audio.SeekTo(dur * 0.5); audio.Play(); });
            await Task.Delay(500);
            var playMid = (IsPlaying: false, Pos: 0.0, Frame: 0L);
            await OnUiAsync(window, () => { playMid = (audio.IsPlaying, audio.Position.TotalSeconds, audio.DecodedFramePosition); });
            await Task.Delay(500);
            var playPos = 0.0;
            var playFrame = 0L;
            await OnUiAsync(window, () => { playPos = audio.Position.TotalSeconds; playFrame = audio.DecodedFramePosition; });
            var playErr = "";
            await OnUiAsync(window, () => { playErr = audio.LastOutputError; });
            var playOk = playPos >= dur * 0.5 && playPos <= dur * 0.5 + 1.5;
            Write(report, $"  {(playOk ? "ok" : "FAIL")} playing advance pos={playPos:F3}s from={(dur * 0.5):F3}s" +
                $" isPlaying={playMid.IsPlaying} frame@{playMid.Pos:F3}s={playMid.Frame} frame@end={playFrame} lasterr=\"{playErr}\"");
            await OnUiAsync(window, () => audio.StopAndUnload());
            Write(report, "target DONE");
        }
        catch (Exception ex)
        {
            Write(report, "target HARNESS-ERROR: " + AudioErrorText.FullReport(ex));
        }
        finally
        {
            AppendCrashLogTail(report, crashSince);
            watchdogCts.Cancel();
            var win = window;
            win.DispatcherQueue.TryEnqueue(() => { try { win.Close(); } catch { } });
        }
    }

    // Passive UI target for the EXTERNAL winapp-ui driver (real OS mouse
    // and keyboard injection): loads one file paused at 0 and sits still
    // until the driver finishes or the 90s watchdog closes it. Run:
    // winapp run ... -- --seek-ui-hold <file>, then drive with
    // winapp ui inspect/click/drag/get-value/screenshot, then Stop-Process.
    public static async Task RunHoldAsync(Window window, AudioEngine audio, string file)
    {
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(10), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Environment.Exit(3);
        });
        try
        {
            await OnUiAsync(window, () => audio.SetVolume(0));
            await audio.LoadAsync(file);
            await OnUiAsync(window, () => { audio.SeekTo(0); audio.Pause(); });
            System.Diagnostics.Debug.WriteLine($"SEEK-HOLD READY file={file}");
            await Task.Delay(TimeSpan.FromMinutes(9));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("SEEK-HOLD-ERROR " + AudioErrorText.FullReport(ex));
        }
    }

    private static AmllTtmlTool.WinUI.Views.ShellPage? FindShellPage(Window window)
    {
        if (window.Content is AmllTtmlTool.WinUI.Views.ShellPage direct) return direct;
        var queue = new Queue<DependencyObject>();
        if (window.Content is DependencyObject root) queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is AmllTtmlTool.WinUI.Views.ShellPage page) return page;
            if (current is Microsoft.UI.Xaml.Controls.Frame frame && frame.Content is DependencyObject fc)
                queue.Enqueue(fc);
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++) queue.Enqueue(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(current, i));
        }
        return null;
    }

    private static DependencyObject? FindByName(DependencyObject? root, string name)
    {
        if (root is null) return null;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is FrameworkElement fe && fe.Name == name) return current;
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++) queue.Enqueue(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(current, i));
        }
        return null;
    }

    private static async Task<string> TestFileAsync(Window window, AudioEngine audio, string file)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"---- file={Name(file)} ----");
        var fails = new List<string>();
        void Expect(bool ok, string label)
        {
            sb.AppendLine($"  {(ok ? "ok" : "FAIL")} {label}");
            if (!ok) fails.Add(label);
        }

        foreach (var rate in Rates)
        {
            foreach (var playing in new[] { true, false })
            {
                var mode = playing ? "playing" : "paused";
                await OnUiAsync(window, () => audio.SetVolume(0));
                await audio.LoadAsync(file);
                await OnUiAsync(window, () =>
                {
                    audio.SetPlaybackRate(rate);
                    if (playing) audio.Play();
                });
                var dur = audio.Duration.TotalSeconds;
                if (!(dur > 1.0))
                {
                    Expect(false, $"rate={rate} {mode}: duration too short ({dur:F2}s)");
                    await OnUiAsync(window, () => audio.StopAndUnload());
                    continue;
                }
                var pre = await OnUiAsync(window, () => audio.Position);
                foreach (var frac in Fracs)
                {
                    var target = dur * frac;
                    // Immediate read (spec window) + 200ms stability read.
                    var pos0 = await OnUiAsync(window, () => { audio.SeekTo(target); return audio.Position; });
                    var inWindow = pos0.TotalSeconds >= target - 0.05 && pos0.TotalSeconds <= target + 0.3;
                    Expect(inWindow, $"rate={rate} {mode} seek {frac:P0} immediate want={target:F3}s got={pos0.TotalSeconds:F3}s");
                    Expect(pos0 != pre, $"rate={rate} {mode} seek {frac:P0} moved (pre={pre.TotalSeconds:F3}s)");
                    await Task.Delay(200);
                    var pos1 = await OnUiAsync(window, () => audio.Position);
                    var upper = playing ? target + rate * 0.25 + 0.1 : target + 0.05;
                    var stable = pos1.TotalSeconds >= target - 0.05 && pos1.TotalSeconds <= upper && pos1 != pre;
                    Expect(stable, $"rate={rate} {mode} seek {frac:P0} stable+200ms got={pos1.TotalSeconds:F3}s");
                    pre = pos1;
                }
                if (!playing)
                    Expect(!await OnUiAsync(window, () => audio.IsPlaying), $"rate={rate} paused: stayed paused through seeks");
                await OnUiAsync(window, () => audio.StopAndUnload());
            }
        }

        // End-of-track fires exactly once.
        {
            await OnUiAsync(window, () => audio.SetVolume(0));
            await audio.LoadAsync(file);
            var dur = audio.Duration.TotalSeconds;
            var ended = 0;
            void OnEnded() => ended++;
            audio.TrackEnded += OnEnded;
            try
            {
                await OnUiAsync(window, () =>
                {
                    audio.SetPlaybackRate(1.0);
                    audio.SeekTo(Math.Max(0, dur - 0.1));
                    audio.Play();
                });
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(200);
                    if (!await OnUiAsync(window, () => audio.IsPlaying)) break;
                }
                await Task.Delay(500);
                Expect(ended == 1, $"end-of-track fired exactly once (got {ended})");
            }
            finally
            {
                audio.TrackEnded -= OnEnded;
                await OnUiAsync(window, () => audio.StopAndUnload());
            }
        }

        // 20 rapid seeks in ~1 second end at the last target.
        {
            await OnUiAsync(window, () => audio.SetVolume(0));
            await audio.LoadAsync(file);
            var dur = audio.Duration.TotalSeconds;
            await OnUiAsync(window, () => { audio.SetPlaybackRate(1.0); audio.Play(); });
            double last = 0;
            for (var i = 0; i < 20; i++)
            {
                last = dur * i / 19.0;
                var target = last;
                await OnUiAsync(window, () => audio.SeekTo(target));
                await Task.Delay(50);
            }
            var got = await OnUiAsync(window, () => audio.Position.TotalSeconds);
            Expect(Math.Abs(got - last) <= 0.05, $"20 rapid seeks end at last target want={last:F3}s got={got:F3}s");
            await OnUiAsync(window, () => audio.StopAndUnload());
        }

        // Seek during a speed change.
        {
            await OnUiAsync(window, () => audio.SetVolume(0));
            await audio.LoadAsync(file);
            var dur = audio.Duration.TotalSeconds;
            await OnUiAsync(window, () => { audio.SetPlaybackRate(1.0); audio.Play(); });
            await OnUiAsync(window, () => audio.SeekTo(dur * 0.5));
            await OnUiAsync(window, () => audio.SetPlaybackRate(2.0));
            var want = dur * 0.25;
            var got = await OnUiAsync(window, () => { audio.SeekTo(want); return audio.Position.TotalSeconds; });
            Expect(Math.Abs(got - want) <= 0.05, $"seek-during-rate-change want={want:F3}s got={got:F3}s");
            await OnUiAsync(window, () => audio.StopAndUnload());
        }

        sb.Append($"file={Name(file)} | {(fails.Count == 0 ? "OK" : "FAIL[" + string.Join("; ", fails) + "]")}");
        return sb.ToString();
    }

    private static string Name(string path) => Path.GetFileName(path);

    private static void Write(string report, string text)
    {
        try { System.IO.File.AppendAllText(report, text + "\n"); } catch { }
        System.Diagnostics.Debug.WriteLine(text);
    }

    private static Task OnUiAsync(Window window, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        }
        return tcs.Task;
    }

    private static Task<T> OnUiAsync<T>(Window window, Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        }
        return tcs.Task;
    }
}
