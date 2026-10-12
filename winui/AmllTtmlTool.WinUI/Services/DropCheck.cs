// --drop-test harness: structural drag-and-drop verification. Real OLE
// drags cannot be synthesized headless, so this asserts everything that
// can be checked in-process (hit-testability, handler-side facts, overlay
// geometry, shared extension lists, live classifier behavior) and leaves
// the per-region manual matrix to the report. Self-closing, DEBUG-only.
using AmllTtmlTool.Core.Lyrics;
using Microsoft.UI.Xaml.Media;

namespace AmllTtmlTool.WinUI.Services;

internal static class DropCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drop-test-report.txt");

    public static async Task RunAsync(Window window, string? reportPath)
    {
        var report = string.IsNullOrWhiteSpace(reportPath) ? DefaultReportFile : reportPath!;
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(5), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                try { System.IO.File.AppendAllText(report, "WATCHDOG: drop-test exceeded 5 minutes, exiting.\n"); } catch { }
                Environment.Exit(3);
            }
        });
        try
        {
            Write(report, "=== DROP TEST ===");
            Write(report, $"binary={Environment.ProcessPath ?? "?"}");
            try { Write(report, $"buildUtc={System.IO.File.GetLastWriteTimeUtc(Environment.ProcessPath!).ToString("u")}"); }
            catch (Exception ex) { Write(report, $"buildUtc=? ({ex.GetType().Name})"); }
            Write(report, $"relaunch=$env:AMLL_SELF_CHECK = \"0\"; & \"{Environment.ProcessPath}\"");
            await OnUiAsync(window, () => Write(report, Check(window)));
        }
        finally
        {
            watchdogCts.Cancel();
        }
    }

    private static string Check(Window window)
    {
        var lines = new System.Text.StringBuilder();
        var fails = new List<string>();
        void CheckLine(bool ok, string label, string detail)
        {
            lines.AppendLine($"  {(ok ? "PASS" : "VERIFY-FAIL")} {label}: {detail}");
            if (!ok) fails.Add(label);
        }
        if (window.Content is not DependencyObject content)
        {
            lines.AppendLine("  VERIFY-FAIL window-content: Window.Content is not a DependencyObject");
            return lines.ToString();
        }
        var root = FindByName(content, "RootGrid") as Microsoft.UI.Xaml.Controls.Grid;
        CheckLine(root is not null, "root-found", root is null ? "RootGrid missing" : root.GetType().Name);
        if (root is not null)
        {
            CheckLine(root.AllowDrop, "root-allowdrop", $"AllowDrop={root.AllowDrop}");
            CheckLine(root.Background is not null,
                "root-hittestable",
                root.Background is null
                    ? "Background is null: gaps between children are NOT hit-testable, drops fall through"
                    : $"Background={root.Background.GetType().Name}: whole window is hit-testable");
        }
        var overlay = FindByName(content, "DropOverlay") as FrameworkElement;
        CheckLine(overlay is not null, "overlay-found", overlay is null ? "DropOverlay missing" : "present");
        if (overlay is not null)
        {
            var rowSpan = Microsoft.UI.Xaml.Controls.Grid.GetRowSpan(overlay);
            CheckLine(rowSpan >= 5, "overlay-covers-window", $"Grid.RowSpan={rowSpan} (window has 5 rows)");
            CheckLine(!overlay.IsHitTestVisible, "overlay-nonintercepting",
                $"IsHitTestVisible={overlay.IsHitTestVisible} (must be false: overlay must never steal drops or cause DragLeave flicker)");
            CheckLine(overlay.Visibility == Visibility.Collapsed, "overlay-hidden-at-rest",
                $"Visibility={overlay.Visibility}");
        }
        var hint = FindByName(content, "DropHintText") as Microsoft.UI.Xaml.Controls.TextBlock;
        CheckLine(hint is not null, "hint-found", hint is null ? "DropHintText missing" : $"default=\"{hint.Text}\"");
        // The classifier runs on the shared LyricStore lists at runtime.
        var cases = new (string[] Names, string Want)[]
        {
            (["song.ttml"], "Lyrics"),
            (["a.flac"], "Audio"),
            (["a.mp3", "b.lrc"], "Both"),
            (["x.xyz"], "Unsupported"),
            ([], "None"),
        };
        foreach (var (names, want) in cases)
        {
            var got = AmllTtmlTool.Core.DropFiles.Classify(
                names, LyricStore.AudioExtensions, LyricStore.LyricExtensions).ToString();
            CheckLine(got == want, $"classify-{want.ToLower()}",
                $"[{string.Join(",", names)}] => {got}");
        }
        CheckLine(LyricStore.AudioExtensions.Count > 0 && LyricStore.LyricExtensions.Count > 0,
            "shared-lists",
            $"audio={LyricStore.AudioExtensions.Count} lyric={LyricStore.LyricExtensions.Count} " +
            $"(picker filter, drop caption and drop dispatch all read these sets; ec3 covered={LyricStore.AudioExtensions.Contains("ec3")})");
        var title = FindByName(content, "TitleBarArea") as FrameworkElement;
        CheckLine(title is not null, "titlebar-found",
            title is null
                ? "TitleBarArea missing"
                : "present; OS caption-region drops cannot be verified headless — see manual matrix (title bar row)");
        lines.AppendLine(fails.Count == 0
            ? "DROP-RESULT: PASS (structural; behavior matrix is manual - see report)"
            : $"DROP-RESULT: VERIFY-FAIL [{string.Join("; ", fails)}]");
        // Machine verdict for HarnessHost.Finish (last non-empty line must
        // end with OK): without this the suite always reports FAIL.
        lines.AppendLine(fails.Count == 0 ? "drop-test | OK" : "drop-test | FAIL");
        return lines.ToString().TrimEnd();
    }

    private static void Write(string report, string text)
    {
        try { System.IO.File.AppendAllText(report, text + "\n"); } catch { }
        System.Diagnostics.Debug.WriteLine(text);
    }

    private static DependencyObject? FindByName(DependencyObject root, string name)
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
}
