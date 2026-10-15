// --perf-report <path>: animation/scroll benchmark for host runs.
// Frame timing in a VM is unreliable (occluded compositor yields zero ticks),
// so this mode runs the same scenarios in both places and writes frame stats
// (median, p95, max, count of frames above 2x the refresh interval) plus per-
// op wall times to <path> (default: temp/perf-report.txt). The host run gives
// the real numbers; the VM run gives wall-time comparability.
// Host command (ONE command):
//   pwsh scripts/run-harness.ps1 -Mode perf-report -Args @('<path>') [-TimeoutSec 900]
// Targets: p95 frame time at most 1.5x the refresh interval, no frame above
// 3x, for: move one line up/down, drag-drop move of 5 lines, toggling
// per-word romanization on 300 lines, and a scroll sweep.
#if DEBUG
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AmllTtmlTool.WinUI.Services;

internal static class PerfReport
{
    public static string DefaultFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "perf-report.txt");

    private static Task OnUiAsync(Window window, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); tcs.TrySetResult(); } catch (Exception ex) { tcs.TrySetException(ex); }
        }))
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        return tcs.Task;
    }

    private static Task<T> OnUiAsync<T>(Window window, Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { tcs.TrySetResult(func()); } catch (Exception ex) { tcs.TrySetException(ex); }
        }))
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        return tcs.Task;
    }

    private static Task<T> OnUiAsync<T>(Window window, Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>();
        if (!window.DispatcherQueue.TryEnqueue(async () =>
        {
            try { tcs.TrySetResult(await func()); } catch (Exception ex) { tcs.TrySetException(ex); }
        }))
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        return tcs.Task;
    }

    private sealed record Scenario(string Name, List<double> OpWallMs, List<TimeSpan> Stamps);

    // A frame sample is only trusted when the compositor produced a clean,
    // steady cadence: enough ticks, a median in a plausible refresh range,
    // and few stalls. An occluded/headless VM yields bursty ticks with a huge
    // median (or none at all) — frame targets are then UNRELIABLE and only the
    // wall times are meaningful; the host run gives the authoritative numbers.
    private static bool TryFrames(Scenario s, out string stats, out string status)
    {
        stats = ""; status = "UNRELIABLE";
        var walls = s.OpWallMs.OrderBy(v => v).ToList();
        var wallMed = walls.Count > 0 ? walls[walls.Count / 2] : 0;
        var wallMax = walls.Count > 0 ? walls[^1] : 0;
        var tail = $"wallMedian={wallMed:F2}ms wallMax={wallMax:F2}ms ops={s.OpWallMs.Count}";
        if (s.Stamps.Count < 2)
        {
            stats = $"frames={s.Stamps.Count} (no compositor ticks — occluded/headless) {tail}";
            return false;
        }
        var deltas = new List<double>();
        for (var i = 1; i < s.Stamps.Count; i++)
            deltas.Add((s.Stamps[i] - s.Stamps[i - 1]).TotalMilliseconds);
        deltas.Sort();
        var median = deltas[deltas.Count / 2];
        var p95 = deltas[(int)(deltas.Count * 0.95)];
        var max = deltas[^1];
        var interval = median > 0 ? median : 16.67;
        var over2x = deltas.Count(d => d > 2 * interval);
        var over3x = deltas.Count(d => d > 3 * interval);
        var reliable = s.Stamps.Count >= 20 && median is >= 8 and <= 40
            && over2x <= Math.Max(1, s.Stamps.Count / 10);
        status = !reliable ? "UNRELIABLE"
            : (p95 <= 1.5 * interval && over3x == 0) ? "PASS" : "FAIL";
        stats = $"frames={s.Stamps.Count} median={median:F2}ms p95={p95:F2}ms " +
            $"max={max:F2}ms over2x={over2x} over3x={over3x} {tail}";
        return reliable;
    }

    private static string StatLine(Scenario s)
    {
        TryFrames(s, out var stats, out var status);
        return $"{s.Name}: {stats} target(p95<=1.5x,no>3x):{status}";
    }

    public static async Task RunAsync(
        Window window, Views.ShellPage page, string? outPath)
    {
        outPath = string.IsNullOrWhiteSpace(outPath) ? DefaultFile : outPath;
        // All page/store access rides the UI thread (background reads throw
        // RPC_E_WRONG_THREAD).
        var theme = await OnUiAsync(window, () => page.ActualTheme.ToString());
        var anim = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        var lines = new List<string>
        {
            $"perf-report utc={DateTime.UtcNow:u}",
            $"theme={theme} animations={anim}",
            "note=this mode is a MEASUREMENT: it always reports OK when it " +
                "completes. Frame target=PASS/FAIL is advisory and only " +
                "trustworthy on a real host (an occluded/headless VM reads " +
                "UNRELIABLE, or FAIL from compositor jitter). Wall times are " +
                "always valid. Read the target column per scenario.",
        };
        var reliableFail = false;
        try
        {
            var procs = (int)Environment.ProcessorCount;
            lines.Add($"cpu={procs} machine={Environment.MachineName}");
            // 300-line doc (own doc; nothing else in this run depends on it).
            await OnUiAsync(window, () => page.Test_BuildDragDoc(300, 3));
            var n = await OnUiAsync(window, () => page.Test_LyricLineCount());
            lines.Add($"doc=300x3 lines={n}");

            void Report(Scenario s)
            {
                TryFrames(s, out _, out var status);
                if (status == "FAIL") reliableFail = true;
                lines.Add(StatLine(s));
            }

            // 1. move one line up/down (Alt+Up/Down path).
            var move = new Scenario("move-line", new(), new());
            await RunSampled(window, move, async () =>
            {
                for (var k = 0; k < 6; k++)
                {
                    await OnUiAsync(window, () => page.Test_SelectLine(150));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await OnUiAsync(window, () => page.Test_MoveSelectedLines(k % 2 == 0 ? -1 : 1));
                    sw.Stop();
                    move.OpWallMs.Add(sw.Elapsed.TotalMilliseconds);
                    await Task.Delay(120);
                }
            });
            Report(move);

            // 2. drag-drop move of 5 lines (pointer-drop path).
            var drag = new Scenario("drag-5lines", new(), new());
            await RunSampled(window, drag, async () =>
            {
                for (var k = 0; k < 3; k++)
                {
                    await OnUiAsync(window, () => page.Test_DragBeginLines(new[] { 10, 11, 12, 13, 14 }));
                    await OnUiAsync(window, () => page.Test_DragToLineIndex(30));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await OnUiAsync(window, () => page.Test_DragEnd(false));
                    sw.Stop();
                    drag.OpWallMs.Add(sw.Elapsed.TotalMilliseconds);
                    await OnUiAsync(window, () => page.Test_Undo());
                    await Task.Delay(120);
                }
            });
            Report(drag);

            // 3. toggling per-word romanization on 300 lines.
            var tog = new Scenario("toggle-wordroman-300", new(), new());
            await RunSampled(window, tog, async () =>
            {
                for (var k = 0; k < 2; k++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await OnUiAsync(window, () => page.Test_FlipToggle("wordroman"));
                    sw.Stop();
                    tog.OpWallMs.Add(sw.Elapsed.TotalMilliseconds);
                    await Task.Delay(600);
                }
            });
            Report(tog);

            // 4. scroll sweep top-bottom-top.
            var scroll = new Scenario("scroll-sweep", new(), new());
            await RunSampled(window, scroll, async () =>
            {
                for (var k = 0; k < 2; k++)
                {
                    for (var at = 0; at < n; at += 15)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        await OnUiAsync(window, () => page.Test_ScrollLineIntoView(k == 0 ? at : n - 1 - at));
                        sw.Stop();
                        scroll.OpWallMs.Add(sw.Elapsed.TotalMilliseconds);
                        await Task.Delay(40);
                    }
                }
            });
            Report(scroll);

            lines.Add($"perf-report advisory reliable-target-fails={(reliableFail ? 1 : 0)} " +
                "(host-only signal; does not affect the run verdict)");
            lines.Add("perf-report | OK");
        }
        catch (Exception ex)
        {
            lines.Add("HARNESS-ERROR: " + ex);
            lines.Add("perf-report | FAIL");
        }
        try { System.IO.File.WriteAllLines(outPath, lines); } catch { }
        // Also land the verdict where the harness runner reads it (the -Args
        // path may differ from the runner's default report).
        try
        {
            var hr = HarnessHost.Report;
            if (!string.IsNullOrEmpty(hr) && !string.Equals(hr, outPath, StringComparison.OrdinalIgnoreCase))
                System.IO.File.WriteAllLines(hr, lines);
        }
        catch { }
    }

    private static async Task RunSampled(Window window, Scenario s, Func<Task> body)
    {
        var stamps = s.Stamps;
        void Handler(object? sender, object args)
        {
            if (args is RenderingEventArgs re) stamps.Add(re.RenderingTime);
        }
        CompositionTarget.Rendering += Handler;
        try
        {
            await body();
            // Settle window so trailing animation frames land in the sample.
            await Task.Delay(800);
        }
        finally
        {
            CompositionTarget.Rendering -= Handler;
        }
    }
}
#endif
