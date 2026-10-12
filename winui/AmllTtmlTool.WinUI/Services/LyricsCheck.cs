// --load-lyrics <file> harness: passive UI target for the Step-5 lyrics
// driver. Imports through FileIntake.ImportFileAsync — the SAME funnel as
// picker, drop, startup file and paste — then sits open until the driver
// finishes or the watchdog closes it. Model/render facts are exposed via
// ShellPage Test_ hooks (in-process asserts stay in the driver script).
// Self-closing is wrong here (driver needs a live window); DEBUG-only.
using Microsoft.UI.Xaml;
using AmllTtmlTool.Core.Lyrics;

namespace AmllTtmlTool.WinUI.Services;

internal static class LyricsCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "load-lyrics-report.txt");

    private static void Write(string report, string text)
    {
        try { System.IO.File.AppendAllText(report, text + "\n"); } catch { }
        System.Diagnostics.Debug.WriteLine(text);
    }

    public static async Task RunHoldAsync(Window window, FileIntake intake, LyricStore store, string file)
    {
        using var watchdogCts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMinutes(10), watchdogCts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Environment.Exit(3);
        });
        try
        {
            var storage = await Windows.Storage.StorageFile.GetFileFromPathAsync(file);
            await intake.ImportFileAsync(storage);
            var lines = 0;
            await OnUiAsync(window, () => lines = store.Document.Lines.Count);
            try
            {
                System.IO.File.AppendAllText(DefaultReportFile,
                    $"load-lyrics | ready lines={lines} file={System.IO.Path.GetFileName(file)}\n");
            }
            catch { }
            System.Diagnostics.Debug.WriteLine($"LOAD-LYRICS READY file={file} lines={lines}");
            await Task.Delay(TimeSpan.FromMinutes(9));
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(DefaultReportFile,
                    $"load-lyrics | HOLD-ERROR {ex.GetType().Name}: {ex.Message}\n");
            }
            catch { }
            System.Diagnostics.Debug.WriteLine("LOAD-LYRICS-ERROR " + ex);
        }
    }

    private static Task OnUiAsync(Window window, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); tcs.TrySetResult(); } catch (Exception ex) { tcs.TrySetException(ex); }
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
            try { tcs.TrySetResult(func()); } catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        }
        return tcs.Task;
    }

    // Self-driving verification: --lyrics-test <file> [report]. Imports
    // through the real funnel, then asserts model facts, the 8-combo
    // visibility matrix, chip-width equality, error-case doc preservation
    // and perf budgets. Self-closes. DEBUG-only.
    public static async Task RunAsync(
        Window window, Views.ShellPage page, LyricStore store, FileIntake intake,
        string lyricFile, string? report)
    {
        report ??= DefaultReportFile;
        var fails = 0;
        void Check(string name, bool cond, string detail)
        {
            Write(report, $"  {(cond ? "ok" : "FAIL")} {name} {detail}");
            if (!cond) fails++;
        }
        Write(report, $"run lyrics-test file='{lyricFile}'");
        try
        {
            var errors = new List<string>();
            intake.ErrorRecorder = (title, message, details) =>
                errors.Add($"{title}: {message}");
            var storage = await Windows.Storage.StorageFile.GetFileFromPathAsync(lyricFile);
            await intake.ImportFileAsync(storage);
            intake.ErrorRecorder = null;
            Check("import-clean", errors.Count == 0, string.Join("; ", errors));
            var wantTitle = System.IO.Path.GetFileName(lyricFile);
            var title = await OnUiAsync(window, () => page.Test_LyricFileName());
            Check("title", title == wantTitle, $"title='{title}'");
            var lines = await OnUiAsync(window, () => page.Test_LyricLineCount());
            Check("lines-loaded", lines > 0, $"lines={lines}");

            // 8-combo visibility matrix on realized lines (skip unrealized).
            // Capped: universal per-row behavior, no need to walk thousands.
            var matrixLines = Math.Min(lines, 25);
            string[] toggles = ["trans", "roman", "wordroman"];
            for (var combo = 0; combo < 8; combo++)
            {
                await OnUiAsync(window, () =>
                {
                    page.Test_SetToggle("trans", (combo & 1) != 0);
                    page.Test_SetToggle("roman", (combo & 2) != 0);
                    page.Test_SetToggle("wordroman", (combo & 4) != 0);
                });
                await Task.Delay(450); // hide-collapses land at 160ms + glide
                for (var li = 0; li < matrixLines; li++)
                {
                    for (var w = 0; w < 3; w++)
                    {
                        var vis = await OnUiAsync(window, () => page.Test_RowVisibility(li, w));
                        if (vis < 0) continue; // unrealized: snaps on realize
                        var want = ((combo >> w) & 1) != 0 ? 0 : 1; // Visible=0
                        if (vis != want)
                            Check($"combo{combo}-line{li}-row{w}", false, $"vis={vis} want={want}");
                    }
                }
                Write(report, $"  note combo{combo} checked");
            }
            Check("combos", true, "matrix done (failures above if any)");

            // Chip widths: the two boxes share width within 1px (capped:
            // universal per-chip behavior).
            var widthFails = 0;
            var widthLines = Math.Min(lines, 25);
            for (var li = 0; li < widthLines; li++)
            {
                var words = await OnUiAsync(window, () => page.Test_LyricWordCount(li));
                for (var wi = 0; wi < words; wi++)
                {
                    var box = await OnUiAsync(window, () => page.Test_ChipWidths(li, wi));
                    if (box.StartsWith("unrealized") || box.StartsWith("no")) continue;
                    var parts = box.Split(',');
                    if (parts.Length != 2
                        || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var tw)
                        || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var rw)
                        || Math.Abs(tw - rw) > 1.0)
                    {
                        widthFails++;
                        Write(report, $"  FAIL chip-width line{li} word{wi} [{box}]");
                    }
                }
            }
            Check("chip-widths", widthFails == 0, $"bad={widthFails}");

            // Error cases through the pure funnel: doc must be preserved.
            var beforeLines = lines;
            var beforeTitle = title;
            var errorFails = 0;
            void ExpectThrow(string name, Action action)
            {
                try
                {
                    action();
                    errorFails++;
                    Write(report, $"  FAIL error-{name}: no exception");
                }
                catch (Core.Lyrics.Import.LyricImportException ex)
                {
                    Write(report, $"  ok error-{name}: {ex.Message.Split('\n')[0]}");
                }
                catch (Exception ex)
                {
                    errorFails++;
                    Write(report, $"  FAIL error-{name}: wrong exception {ex.GetType().Name}");
                }
            }
            ExpectThrow("malformed", () => LyricStore.ImporterFor("x.ttml")
                .Import("<tt><body><div><p begin=", "x.ttml"));
            ExpectThrow("unsupported", () => LyricStore.ImporterFor("x.qrc"));
            var afterLines = await OnUiAsync(window, () => page.Test_LyricLineCount());
            var afterTitle = await OnUiAsync(window, () => page.Test_LyricFileName());
            if (afterLines != beforeLines || afterTitle != beforeTitle)
            {
                errorFails++;
                Write(report, $"  FAIL error-preserved: lines {beforeLines}->{afterLines} title '{beforeTitle}'->'{afterTitle}'");
            }
            Check("error-cases", errorFails == 0, "doc preserved, typed exceptions");

            // Perf: toggle UI-thread budgets (flip twice each: ends restored).
            // Median of 3 rounds per toggle: a single congested UI thread
            // (loaded machine) must not flake the budget.
            var worstToggle = 0L;
            foreach (var t in toggles)
            {
                var samples = new List<long>();
                for (var round = 0; round < 3; round++)
                {
                    samples.Add(await OnUiAsync(window, () => page.Test_FlipToggle(t)));
                    samples.Add(await OnUiAsync(window, () => page.Test_FlipToggle(t)));
                }
                samples.Sort();
                var med = samples[samples.Count / 2];
                worstToggle = Math.Max(worstToggle, med);
                Write(report, $"  note toggle-{t} medianMs={med} samples={string.Join("/", samples)}");
            }
            Check("toggle-budget", worstToggle <= 100, $"worst={worstToggle}ms");

            // Frame stats during a word-roman toggle (most animated elements).
            var flatStats = await await OnUiAsync(window, () => page.Test_FrameStats("wordroman", 1500));
            var realized = await OnUiAsync(window, () => page.Test_RealizedLineCount());
            Write(report, $"  note frames [{flatStats}] realized={realized}");
            Check("frame-sampled", flatStats.StartsWith("frames=") && !flatStats.StartsWith("frames=0 ")
                && !flatStats.StartsWith("frames=1 "), flatStats);

            // Virtualization walk (meaningful on large docs only).
            var topLines = await OnUiAsync(window, () => page.Test_RealizedLineCount());
            var topBoxes = await OnUiAsync(window, () => page.Test_RealizedTextBoxCount());
            await OnUiAsync(window, () => page.Test_ScrollLineIntoView(lines - 1));
            await Task.Delay(400);
            var bottomLines = await OnUiAsync(window, () => page.Test_RealizedLineCount());
            var bottomBoxes = await OnUiAsync(window, () => page.Test_RealizedTextBoxCount());
            Write(report, $"  note realized top={topLines}/{topBoxes} bottom={bottomLines}/{bottomBoxes}");
            if (lines > 50)
            {
                var okVirt = bottomLines < lines && bottomLines <= Math.Max(30, topLines * 3)
                    && bottomBoxes <= Math.Max(300, topBoxes * 3);
                Check("virtualized", okVirt, $"total={lines}");
            }

            Write(report, fails == 0 ? "lyrics-test | OK" : $"lyrics-test | FAIL {fails}");
        }
        catch (Exception ex)
        {
            Write(report, "HARNESS-ERROR: " + ex);
        }
    }
}
