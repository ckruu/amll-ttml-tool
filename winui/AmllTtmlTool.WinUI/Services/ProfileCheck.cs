#if DEBUG
// --profile <file> [report]: Step-1 realization + pipeline profiler.
// Imports the staged doc through the real funnel, then measures with
// Stopwatch (DEBUG-only probes, off in release): per-card realization
// stages, command-to-first-frame pipeline, scroll/maximize costs, and
// realized element counts. Writes median/p95/max per stage. A MEASUREMENT
// (always OK on completion), not a gate.
using Microsoft.UI.Xaml;

namespace AmllTtmlTool.WinUI.Services;

internal static class ProfileCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "profile-report.txt");

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

    private static string Stats(string name, List<double> vals)
    {
        if (vals.Count == 0) return $"{name}: n=0";
        var s = vals.OrderBy(v => v).ToList();
        static string F(double v) =>
            v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        return $"{name}: n={s.Count} median={F(s[s.Count / 2])} " +
            $"p95={F(s[(int)(s.Count * 0.95)])} max={F(s[^1])}";
    }

    // Probe numbers are invariant-formatted (C1 rule); accept current-culture
    // too so a missed spot degrades to a value instead of an exception.
    private static bool ParseMs(string s, out double v)
    {
        if (double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out v)) return true;
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture, out v);
    }

    public static async Task RunAsync(Window window, Views.ShellPage page,
        FileIntake intake, Core.Lyrics.LyricStore store, string file, string? report)
    {
        report ??= DefaultReportFile;
        var lines = new List<string> { $"profile utc={DateTime.UtcNow:u}" };
        void Out(string text)
        {
            lines.Add(text);
            try { System.IO.File.AppendAllText(report, text + "\n"); } catch { }
        }
        try
        {
            var storage = await Windows.Storage.StorageFile.GetFileFromPathAsync(file);
            await intake.ImportFileAsync(storage);
            var n = await OnUiAsync(window, () => store.Document.Lines.Count);
            var words = await OnUiAsync(window, () =>
                store.Document.Lines.Select(l => l.Words.Count).ToList());
            var avgW = words.Count > 0 ? words.Average() : 0;
            Out($"doc lines={n} avgWordsPerLine={avgW:F1}");
            Out(await OnUiAsync(window, () => page.Test_ProfileElementCounts()));
            // 1. Realization sweep: every line into view (forces prepares).
            await OnUiAsync(window, () =>
            {
                ProfileTimers.Reset();
                Controls.LyricWrapPanel.ProfileReset();
            });
            var sweep = new List<double>();
            for (var i = 0; i < n; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var idx = i;
                await OnUiAsync(window, () => page.Test_ScrollLineIntoView(idx));
                sw.Stop();
                sweep.Add(sw.Elapsed.TotalMilliseconds);
            }
            Out(Stats("realize.scrollStep", sweep));
            Out(await OnUiAsync(window, () => page.Test_ProfileElementCounts()));
            var panel = await OnUiAsync(window, () =>
                $"panel measurePerCall={Controls.LyricWrapPanel.ProfileMeasureMs / Math.Max(1, Controls.LyricWrapPanel.ProfileMeasureCount):F3}ms " +
                $"x{Controls.LyricWrapPanel.ProfileMeasureCount} " +
                $"arrangePerCall={Controls.LyricWrapPanel.ProfileArrangeMs / Math.Max(1, Controls.LyricWrapPanel.ProfileArrangeCount):F3}ms " +
                $"x{Controls.LyricWrapPanel.ProfileArrangeCount}");
            Out(panel);
            foreach (var row in (await OnUiAsync(window, () => ProfileTimers.Table()))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Out(row);
            // 2. Command pipeline (5 reps each; dup/delete undo to restore).
            // First raw sample echoed for auditability.
            var rawEchoed = false;
            foreach (var kind in new[] { "move-up", "move-down", "dup", "delete" })
            {
                var cmd = new List<double>();
                var lay = new List<double>();
                var frm = new List<double>();
                for (var k = 0; k < 5; k++)
                {
                    var raw = await OnUiAsync(window, () => page.Test_ProfileOpOnce(kind));
                    if (!rawEchoed) { Out($"raw profile-op sample: [{raw}]"); rawEchoed = true; }
                    foreach (var p in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var kv = p.Split('=');
                        if (kv.Length != 2 || !ParseMs(kv[1], out var v)) continue;
                        if (kv[0] == "cmd") cmd.Add(v);
                        else if (kv[0] == "layout") lay.Add(v);
                        else if (kv[0] == "frame" && v >= 0) frm.Add(v);
                    }
                    await Task.Delay(150);
                }
                Out(Stats($"pipe.{kind}.cmd", cmd));
                Out(Stats($"pipe.{kind}.layout", lay));
                Out(Stats($"pipe.{kind}.frame", frm));
            }
            // 3. Scroll variants.
            var intoView = new List<double>();
            var rawScrollEchoed = false;
            for (var i = 0; i < n; i += 4)
            {
                var idx = i;
                var r = await OnUiAsync(window, () => page.Test_ProfileScrollOnce("into-view", idx));
                if (!rawScrollEchoed) { Out($"raw scroll sample: [{r}]"); rawScrollEchoed = true; }
                if (r.StartsWith("op=") && ParseMs(r[3..], out var v) && v >= 0) intoView.Add(v);
            }
            Out(Stats("scroll.into-view", intoView));
            async Task<List<double>> RepeatScroll(string kind, int reps, int arg)
            {
                var vals = new List<double>();
                for (var k = 0; k < reps; k++)
                {
                    var r = await OnUiAsync(window, () => page.Test_ProfileScrollOnce(kind, arg));
                    if (k == 0) Out($"raw {kind} sample: [{r}]");
                    if (r.StartsWith("op=") && ParseMs(r[3..], out var v) && v >= 0)
                        vals.Add(v);
                    await Task.Delay(40);
                }
                return vals;
            }
            Out(Stats("scroll.linedown", await RepeatScroll("linedown", 20, 0)));
            Out(Stats("scroll.thumbsnap", await RepeatScroll("thumbsnap", 6, 44)));
            Out(Stats("scroll.pagedown", await RepeatScroll("pagedown", 6, 0)));
            // 4. Maximize/restore x2.
            for (var k = 0; k < 2; k++)
                Out($"maxrestore.{k} " + await OnUiAsync(window, () => page.Test_ProfileMaxRestore()));
            Out("profile | OK");
        }
        catch (Exception ex)
        {
            Out("HARNESS-ERROR: " + ex);
            Out("profile | FAIL");
        }
    }
}
#endif
