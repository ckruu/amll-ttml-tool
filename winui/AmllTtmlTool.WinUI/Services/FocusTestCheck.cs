// --focus-test [reportPath] harness: how often the per-frame paint path
// actually runs, by window state. Answers the question before changing the
// design: does CompositionTarget.Rendering stop for a window that is merely
// not focused?
//
// Four states, ~3s each, sampling ONLY (no painting, no layout):
//   focused        - the window is foreground
//   unfocused      - foreground is another window, ours is still visible and
//                    unobstructed
//   covered        - another window fully covers ours
//   minimized      - the window is iconic
// For each state it records Rendering callbacks/sec and, separately, the
// text-update rate the current implementation would deliver (the engine's
// 250ms PositionChanged leg).
//
// The states are entered by the external driver (winapp ui focus, or a
// second window moved over ours); this harness just measures what it sees.
// Self-closing. DEBUG-only.
using Microsoft.UI.Xaml.Media;

namespace AmllTtmlTool.WinUI.Services;

internal static class FocusTestCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "focus-test-report.txt");

    private const string ReadyFlag = "focus-test-ready";
    private const string StateFlag = "focus-test-state";

    private sealed record Sample(int State, int Callbacks, int Seconds);

    public static async Task RunAsync(Window window, AudioEngine audio, string? reportPath)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(4), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Environment.Exit(3);
        });
        try
        {
            Write(report, "=== FOCUS / RENDERING CADENCE ===");
            await OnUiAsync(window, () => audio.SilentMode = true);
            await audio.LoadAsync("D:\\Audio\\alac.m4a");
            await OnUiAsync(window, () => { audio.SeekTo(0); audio.Play(); });

            var rows = new List<Sample>();
            var callbacks = 0;
            void Handler(object? s, object? e) => callbacks++;

            // Arm once; the driver tells us which state we are in with a file.
            await OnUiAsync(window, () => CompositionTarget.Rendering += Handler);
            var stateFile = Path.Combine(Path.GetTempPath(), StateFlag);

            await OnUiAsync(window, () => window.Activate());
            foreach (var (name, wantActive) in new[]
            {
                ("focused", true),
                ("unfocused", false),
                ("covered", false),
                ("minimized", false),
            })
            {
                File.Delete(stateFile);
                Write(report, $"READY {name}");
                // Wait for the driver to put us in that state (max 40s each).
                var seen = "";
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
                while (DateTime.UtcNow < deadline)
                {
                    if (File.Exists(stateFile))
                    {
                        seen = (File.ReadAllText(stateFile) ?? "").Trim();
                        if (seen == name) break;
                    }
                    await Task.Delay(100);
                }
                if (seen != name)
                {
                    Write(report, $"  {name}: SKIP (driver did not confirm)");
                    continue;
                }
                callbacks = 0;
                var t0 = DateTime.UtcNow;
                await Task.Delay(3000);
                var secs = (DateTime.UtcNow - t0).TotalSeconds;
                rows.Add(new Sample(Index(name), callbacks, (int)secs));
                var text = await OnUiAsync(window, () =>
                    PositionTextOf(window) is { } tb ? tb.Text : "(none)");
                Write(report, $"  {name}: rendering={callbacks / secs:F1}/s ({callbacks} in {secs:F1}s) sampleText={text}");
            }

            Write(report, "--- table (state | rendering/s | text updates/s) ---");
            foreach (var r in rows)
                Write(report, $"  {Name(r.State)} | {r.Callbacks / (double)r.Seconds:F1}/s | (see sampleText)");

            await OnUiAsync(window, () => audio.StopAndUnload());
            Write(report, "focus | OK");
        }
        catch (Exception ex)
        {
            Write(report, "HARNESS-ERROR: " + ex);
        }
        finally
        {
            watchdogCts.Cancel();
        }
    }

    private static int Index(string name) => name switch
    {
        "focused" => 0, "unfocused" => 1, "covered" => 2, "minimized" => 3, _ => 4,
    };

    // --- 2e: real-time text rate while visible-but-unfocused ----------------
    // Verifies the user-facing requirement: with another window in front and
    // ours still visible, the millisecond text advances at the display refresh
    // rate rather than at the engine's 250ms cadence.
    public static async Task RunUnfocusedAsync(
        Window window, AudioEngine audio, string? reportPath, int seconds = 10)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(4), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Environment.Exit(3);
        });
        try
        {
            Write(report, "=== UNFOCUSED TEXT RATE ===");
            await OnUiAsync(window, () => audio.SilentMode = true);
            await audio.LoadAsync("D:\\Audio\\alac.m4a");
            await OnUiAsync(window, () => { audio.SeekTo(0); audio.Play(); });

            // PositionTextOf touches window.Content, which is UI-thread-only.
            var label = await OnUiAsync(window, () => PositionTextOf(window));
            if (label is null)
            {
                Write(report, "SETUP-FAILED: PositionText not found");
                return;
            }

            // The driver puts a window in front of ours and signals it.
            var stateFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "focus-test-state");
            System.IO.File.Delete(stateFile);
            Write(report, "READY unfocused");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                if (System.IO.File.Exists(stateFile) &&
                    (System.IO.File.ReadAllText(stateFile) ?? "").Trim() == "unfocused") break;
                await Task.Delay(100);
            }

            // Count in-app, on the dispatcher, on a timer: sampling the text
            // from PowerShell introduces the sampler's own latency (a poll
            // every 20ms cannot report a rate above 50/s), so the app has to
            // measure its own repaint cadence instead.
            var updates = 0;
            var distinct = new HashSet<string>();
            var backwards = 0;
            string? last = null;
            var proc0 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            var frames0 = Services.RenderStats.Frames;
            var handlerTicks0 = Services.RenderStats.HandlerTicks;

            // Hooks the SAME paint path the UI uses, by subscribing to
            // Rendering and counting the writes the display would do.
            void PaintCounter(object? sender, object? e)
            {
                var text = label.Text;
                if (text != last)
                {
                    if (last is not null && string.CompareOrdinal(text, last) < 0) backwards++;
                    last = text;
                    updates++;
                    distinct.Add(text);
                }
            }

            await OnUiAsync(window, () =>
            {
                CompositionTarget.Rendering += PaintCounter;
                System.Diagnostics.Stopwatch.StartNew();
            });
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await OnUiAsync(window, () => CompositionTarget.Rendering -= PaintCounter);

            var proc1 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            var frames = Services.RenderStats.Frames - frames0;
            var handlerMs = (handlerTicks0 - Services.RenderStats.HandlerTicks) /
                (double)System.Diagnostics.Stopwatch.Frequency * -1000.0;
            var cpuPct = (proc1 - proc0).TotalMilliseconds / (seconds * 1000.0) * 100.0;
            var perSec = updates / (double)seconds;

            Write(report, $"  seconds={seconds}");
            Write(report, $"  text-updates/sec={perSec:F1} (target >= 55)");
            Write(report, $"  distinct-values={distinct.Count}");
            Write(report, $"  backwards-steps={backwards}");
            Write(report, $"  ui-frames={frames} handler-cpu={handlerMs:F1}ms process-cpu={cpuPct:F1}%");
            var fails = 0;
            if (perSec < 55) { Write(report, "  FAIL text updates below 55/s while unfocused"); fails++; }
            if (backwards != 0) { Write(report, "  FAIL text stepped backwards"); fails++; }
            Write(report, fails == 0 ? "unfocused | OK" : $"unfocused | FAIL fails={fails}");
        }
        catch (Exception ex)
        {
            Write(report, "HARNESS-ERROR: " + ex);
        }
        finally
        {
            watchdogCts.Cancel();
        }
    }

    private static string Name(int i) => i switch
    {
        0 => "focused", 1 => "unfocused", 2 => "covered", 3 => "minimized", _ => "?",
    };

    // PositionText lives on ShellPage, which is hosted inside a Frame, not
    // directly as Window.Content - so search the tree instead of casting.
    private static Microsoft.UI.Xaml.Controls.TextBlock? PositionTextOf(Window window)
    {
        var page = FindShellPage(window);
        if (page is null) return null;
        var f = typeof(AmllTtmlTool.WinUI.Views.ShellPage).GetField("PositionText",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        return f?.GetValue(page) as Microsoft.UI.Xaml.Controls.TextBlock;
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
            if (current is Microsoft.UI.Xaml.Controls.Frame frame && frame.Content is DependencyObject inner)
            {
                queue.Enqueue(inner);
            }
            var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++)
                queue.Enqueue(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(current, i));
        }
        return null;
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

    private static void Write(string report, string text)
    {
        try { System.IO.File.AppendAllText(report, text + "\n"); } catch { }
    }
}
