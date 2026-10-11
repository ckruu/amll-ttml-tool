// --seek-drag <file> [reportPath] harness: per-frame verification of the
// seek display during a CONTINUOUS 1px drag.
//
// winapp ui drag can only press/move/release, so it cannot answer the real
// question: does the time text track the pointer WHILE the thumb is moving,
// and in true milliseconds? Here the APP samples every rendered frame
// (pointer X via GetCursorPos, the displayed text, the thumb's Value, and the
// expected value derived from the slider's own geometry) while an external
// driver (scripts/seek-drag-test.ps1) performs the gesture. Per-frame
// sampling cannot be done fast enough from outside the process, so it lives
// here.
//
// Pass criteria:
//   * text changes on >= 95% of frames where the pointer moved
//   * the displayed ms equals the thumb's ms within 1ms, on every frame
//   * distinct ms values are not stuck (well over a handful across 3s)
//   * |shown - expected-from-pointer| stays small (no multi-frame lag)
//   * after release, the displayed value equals the thumb's final value
//
// Self-closing (target mode). DEBUG-only.
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Media;

namespace AmllTtmlTool.WinUI.Services;

internal static class SeekDragCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "seek-drag-report.txt");

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private sealed record Frame(
        long Qpc, int PointerX, string Text, double Thumb, int ShownMs, int ExpectedMs);

    public static async Task RunTargetAsync(Window window, AudioEngine audio, string file, string? reportPath)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        // 180s: plenty for an external driver to press, drag for 3s and release,
        // but the run can never wedge the machine if that driver never shows.
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(180), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Environment.Exit(3);
        });
        try
        {
            Write(report, "=== SEEK DRAG (continuous, per-frame) ===");
            await OnUiAsync(window, () => audio.SilentMode = true);
            await audio.LoadAsync(file);
            var durationMs = await OnUiAsync(window, () => audio.Duration.TotalMilliseconds);
            Write(report, $"file={System.IO.Path.GetFileName(file)} duration={durationMs:F3}ms");

            // Control discovery must happen on the UI thread (this method runs
            // on a pool thread: touching Window.Content from here is
            // RPC_E_WRONG_THREAD). The Rendering handler below also runs on the
            // UI thread, so reading Text/Value there is safe.
            var controls = await OnUiAsync(window, () =>
            {
                var page = FindShellPage(window);
                return (Slider: SeekSliderOf(page), Label: PositionTextOf(page));
            });
            var slider = controls.Slider;
            var label = controls.Label;
            if (slider is null || label is null)
            {
                Write(report, "SETUP-FAILED: SeekSlider/PositionText not found");
                return;
            }

            // Contract with the driver: the app reports READY, then the driver
            // creates a "go" file, presses, drags, releases, and creates a
            // "done" file. Recording is armed only between those two markers so
            // the press and release themselves are not counted as the drag.
            var goPath = Path.Combine(Path.GetTempPath(), "seek-drag-go");
            var donePath = Path.Combine(Path.GetTempPath(), "seek-drag-done");
            File.Delete(goPath);
            File.Delete(donePath);
            Write(report, "READY");
            while (!File.Exists(goPath) && !watchdogCts.IsCancellationRequested)
                await Task.Delay(50);
            if (watchdogCts.IsCancellationRequested) return;

            var frames = new List<Frame>(8192);
            void Handler(object? sender, object? e)
            {
                GetCursorPos(out var p);
                frames.Add(new Frame(
                    System.Diagnostics.Stopwatch.GetTimestamp(),
                    p.X, label.Text, slider.Value,
                    MsFromText(label.Text), ExpectedMs(slider, p.X, durationMs)));
            }

            await OnUiAsync(window, () => CompositionTarget.Rendering += Handler);
            Write(report, "ARMED");
            while (!File.Exists(donePath)) await Task.Delay(50);
            await OnUiAsync(window, () =>
            {
                CompositionTarget.Rendering -= Handler;
                audio.Pause();
            });
            Write(report, $"frames={frames.Count}");

            // Read straight off the controls captured before the drag: no page
            // re-discovery between the release and the check, or a navigation
            // could swap the elements out from under the measurement. On the UI
            // thread, because this method itself runs on a pool thread.
            var release = await OnUiAsync(window, () => (label.Text, slider.Value));
            var fails = Analyse(report, frames);

            var releaseMs = MsFromText(release.Text);
            var releaseOk = Math.Abs(release.Value - releaseMs) <= 1;
            Write(report, $"  release text={release.Text} thumb={release.Value:F3}ms -> {(releaseOk ? "ok" : "FAIL")}");
            if (!releaseOk) fails++;

            Write(report, fails == 0 ? "seek-drag | OK" : $"seek-drag | FAIL fails={fails}");
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

    // Expected value from the pointer geometry alone, independent of any
    // throttling or rounding inside the app.
    private static int ExpectedMs(
        Microsoft.UI.Xaml.Controls.Slider slider, int pointerX, double durationMs)
    {
        if (!(slider.ActualWidth > 0) || !(durationMs > 0)) return 0;
        var origin = slider.TransformToVisual(null)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        var rel = (pointerX - origin.X) / slider.ActualWidth;
        return (int)Math.Clamp(rel * durationMs, 0, durationMs);
    }

    private static int MsFromText(string text)
    {
        var m = Regex.Match(text, @"(\d+):(\d+)\.(\d+)");
        return m.Success
            ? int.Parse(m.Groups[1].Value) * 60000
                + int.Parse(m.Groups[2].Value) * 1000
                + int.Parse(m.Groups[3].Value)
            : 0;
    }

    // Returns the failure count for this run.
    private static int Analyse(string report, List<Frame> frames)
    {
        var fails = 0;
        if (frames.Count < 30)
        {
            Write(report, "  FAIL too few frames recorded (was the driver running?)");
            return 1;
        }

        var moved = 0;
        var changedOnMove = 0;
        var msDiffOk = 0;
        var distinct = new HashSet<int>();
        var worstLag = 0;
        foreach (var (f, i) in frames.Select((f, i) => (f, i)))
        {
            if (i > 0 && f.PointerX != frames[i - 1].PointerX)
            {
                moved++;
                if (f.ShownMs != frames[i - 1].ShownMs) changedOnMove++;
            }
            if (Math.Abs(f.Thumb - f.ShownMs) <= 1) msDiffOk++;
            distinct.Add(f.ShownMs);
            worstLag = Math.Max(worstLag, Math.Abs(f.ShownMs - f.ExpectedMs));
        }
        var pctMoved = moved > 0 ? 100.0 * changedOnMove / moved : 0;
        var pctMsOk = 100.0 * msDiffOk / frames.Count;

        Write(report, $"  frames={frames.Count} pointer-moved={moved} text-changed-on-move={changedOnMove} ({pctMoved:F1}%)");
        Write(report, $"  distinct-ms-values={distinct.Count}");
        Write(report, $"  ms-equals-thumb={pctMsOk:F1}% of frames (want 100%)");
        Write(report, $"  worst |shown - expected-from-pointer| = {worstLag}ms");

        if (pctMoved < 95)
        {
            Write(report, $"  FAIL text tracked the pointer on only {pctMoved:F1}% of moved frames (want >= 95)");
            fails++;
        }
        if (pctMsOk < 100)
        {
            Write(report, $"  FAIL displayed ms diverged from the thumb on {100 - pctMsOk:F1}% of frames (> 1ms)");
            fails++;
        }
        if (distinct.Count < 60)
        {
            Write(report, $"  FAIL only {distinct.Count} distinct ms values - display looks stuck");
            fails++;
        }
        if (worstLag > 500)
        {
            Write(report, $"  FAIL display lags the pointer by {worstLag}ms");
            fails++;
        }
        return fails;
    }

    private static Microsoft.UI.Xaml.Controls.Slider? SeekSliderOf(AmllTtmlTool.WinUI.Views.ShellPage? page)
    {
        if (page is null) return null;
        var f = typeof(AmllTtmlTool.WinUI.Views.ShellPage).GetField("SeekSlider",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        return f?.GetValue(page) as Microsoft.UI.Xaml.Controls.Slider;
    }

    private static Microsoft.UI.Xaml.Controls.TextBlock? PositionTextOf(AmllTtmlTool.WinUI.Views.ShellPage? page)
    {
        if (page is null) return null;
        var f = typeof(AmllTtmlTool.WinUI.Views.ShellPage).GetField("PositionText",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        return f?.GetValue(page) as Microsoft.UI.Xaml.Controls.TextBlock;
    }

    private static AmllTtmlTool.WinUI.Views.ShellPage? FindShellPage(Window window)
    {
        if (window.Content is AmllTtmlTool.WinUI.Views.ShellPage direct) return direct;
        var q = new Queue<DependencyObject>();
        if (window.Content is DependencyObject root) q.Enqueue(root);
        while (q.Count > 0)
        {
            var c = q.Dequeue();
            if (c is AmllTtmlTool.WinUI.Views.ShellPage p) return p;
            if (c is Microsoft.UI.Xaml.Controls.Frame fr && fr.Content is DependencyObject fc) q.Enqueue(fc);
            var n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(c);
            for (var i = 0; i < n; i++) q.Enqueue(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(c, i));
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
