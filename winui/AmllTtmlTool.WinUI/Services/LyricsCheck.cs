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
}
