#if DEBUG
// --input-hold <file>: interactive target for scripts/input.ps1 real-input
// drivers (Step 0). Imports through FileIntake.ImportFileAsync — the SAME
// funnel as picker/drop — enables the --input-trace log, then serves
// requests from the driver until stop.req (seek-drag target-mode precedent):
//   pixel-<id>.req -> Test_PixelSelectionAsync -> pixel-<id>.res (one line)
//   sel-<id>.req   -> service sets snapshot       -> sel-<id>.res (one line)
//   stop.req       -> input-hold | OK + Finish + close
// Request files are empty (id in the name); the app deletes each .req after
// answering. Polling (500ms) backs the watcher so loaded VMs never stall.
// DEBUG-only.
using Microsoft.UI.Xaml;
using AmllTtmlTool.Core.Lyrics;

namespace AmllTtmlTool.WinUI.Services;

internal static class InputHoldCheck
{
    public static string DefaultReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "input-hold-report.txt");

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
            try { action(); tcs.TrySetResult(); } catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        }
        return tcs.Task;
    }

    private static Task<T> OnUiAsync<T>(Window window, Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T>();
        if (!window.DispatcherQueue.TryEnqueue(async () =>
        {
            try { tcs.TrySetResult(await func()); } catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        }
        return tcs.Task;
    }

    public static async Task RunHoldAsync(Window window, Views.ShellPage page,
        FileIntake intake, LyricStore store, string file, string? report)
    {
        report ??= DefaultReportFile;
        var dir = System.IO.Path.GetDirectoryName(report);
        if (string.IsNullOrEmpty(dir)) dir = System.IO.Path.GetTempPath();
        try
        {
            var holdMinEnv = System.Environment.GetEnvironmentVariable("AMLL_HOLD_MINUTES");
            var holdMin = int.TryParse(holdMinEnv, out var hm) && hm >= 1 ? hm : 120;
            var storage = await Windows.Storage.StorageFile.GetFileFromPathAsync(file);
            if (HarnessHost.InputTracePath is string tp)
                await OnUiAsync(window, () => page.Test_SetInputTrace(tp));
            await intake.ImportFileAsync(storage);
            var lines = 0;
            await OnUiAsync(window, () => lines = store.Document.Lines.Count);
            Write(report, $"input-hold | ready lines={lines} file={System.IO.Path.GetFileName(file)} dir={dir}");
            var deadline = DateTime.UtcNow.AddMinutes(holdMin);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                string[] reqs;
                try { reqs = System.IO.Directory.GetFiles(dir, "*.req"); }
                catch { continue; }
                foreach (var req in reqs)
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(req);
                    try { System.IO.File.Delete(req); } catch { continue; }
                    if (name == "stop")
                    {
                        Write(report, "input-hold | OK");
                        return;
                    }
                    try
                    {
                        if (name.StartsWith("pixel-"))
                        {
                            var res = await OnUiAsync(window, () => page.Test_PixelSelectionAsync());
                            Write(report, $"res {name} [{res}]");
                            System.IO.File.WriteAllText(
                                System.IO.Path.Combine(dir, name + ".res"), res + "\n");
                        }
                        else if (name.StartsWith("sel-"))
                        {
                            var sl = ""; var sw = "";
                            await OnUiAsync(window, () =>
                            {
                                sl = page.Test_ServiceLineIdx();
                                sw = page.Test_SelectedWordIdx();
                            });
                            var res = $"svcL=[{sl}] svcW=[{sw}]";
                            Write(report, $"res {name} {res}");
                            System.IO.File.WriteAllText(
                                System.IO.Path.Combine(dir, name + ".res"), res + "\n");
                        }
                    }
                    catch (Exception ex)
                    {
                        var err = $"HARNESS-ERROR {ex.GetType().Name}: {ex.Message}";
                        Write(report, $"res {name} {err}");
                        try
                        {
                            System.IO.File.WriteAllText(
                                System.IO.Path.Combine(dir, name + ".res"), err + "\n");
                        }
                        catch { }
                    }
                }
            }
            Write(report, "input-hold | hold-expired (AMLL_HOLD_MINUTES) | OK");
        }
        catch (Exception ex)
        {
            Write(report, "input-hold | HOLD-ERROR " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
#endif
