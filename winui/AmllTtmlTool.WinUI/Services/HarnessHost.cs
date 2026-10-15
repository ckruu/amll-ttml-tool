#if DEBUG
// Test harness host. Capture() runs at the VERY START of startup, before
// any window exists: it resolves the mode from environment variables
// (authoritative — a launcher is not trusted to forward command-line
// arguments) or from --<mode> args, writes the .started marker the
// external runner polls for, and arms a dispatcher-INDEPENDENT watchdog
// (System.Threading.Timer) that fails a hung run by itself: it writes
// TIMEOUT and exits the process, so no run can ever block on a person
// closing a window. Every in-app test run MUST end with Finish().
using System.IO;
using System.Text;

namespace AmllTtmlTool.WinUI.Services;

internal static class HarnessHost
{
    public static string? Mode { get; private set; }
    public static string? Report { get; private set; }
    public static bool IsTest => Mode is not null;

    private static readonly System.Diagnostics.Stopwatch Clock =
        System.Diagnostics.Stopwatch.StartNew();
    private static readonly Timer Watchdog = new(_ => OnTimeout());
    private static readonly object Gate = new();
    private static int _finished;
    private static int _timeoutSec = 120;
    private static string? _captureError;

    private static void OnTimeout()
    {
        try
        {
            if (Report is not null)
            {
                File.AppendAllText(Report,
                    $"VERDICT: TIMEOUT after {_timeoutSec}s (watchdog deadline hit)\n");
            }
        }
        catch { }
        Environment.Exit(3);
    }

    // Per-mode watchdog defaults: comfortably above the worst observed
    // runtime of that harness (decode is the slow part), below any human
    // patience. -TimeoutSec from the runner overrides via env var.
    private static int DefaultTimeout(string mode) => mode switch
    {
        "audio-test" => 900,
        "seek-test" => 1200,
        "stretch-test" => 600,
        "clock-test" => 420,
        "minimize-test" => 900,
        "drop-test" => 180,
        "smtc-test" => 240,
        "layout-test" => 300,
        "selftest" => 60,
        "load-lyrics" => 600,
        "lyrics-test" => 600,
        "perf-report" => 900,
        _ => 180,
    };

    public static string[] ResolveArgs(string[] original)
    {
        if (!IsTest) return original;
        // The mode came from the environment: feed the same dispatch chain
        // by appending --<mode> [report]. When it came from args the array
        // is already right and is returned unchanged.
        var envMode = Environment.GetEnvironmentVariable("AMLL_HARNESS_MODE");
        if (string.IsNullOrWhiteSpace(envMode)) return original;
        var list = original.ToList();
        var flag = "--" + Mode!;
        if (!list.Contains(flag)) list.Add(flag);
        if (!string.IsNullOrWhiteSpace(Report) && list.Count >= 2 &&
            !list.Contains(Report!))
            list.Add(Report!);
        return list.ToArray();
    }


    public static void Capture()
    {
        try { CaptureCore(); }
        catch (Exception ex) { _captureError = ex.ToString(); }
    }

    private static void CaptureCore()
    {
        var args = Environment.GetCommandLineArgs();
        var mode = Environment.GetEnvironmentVariable("AMLL_HARNESS_MODE")?.Trim();
        string? fromArgs = null;
        if (string.IsNullOrWhiteSpace(mode))
        {
            foreach (var a in args)
            {
                if (a.StartsWith("--") && a.Length > 2 && !a.Contains('='))
                {
                    fromArgs = a[2..];
                    break;
                }
            }
            mode = fromArgs;
        }
        if (string.IsNullOrWhiteSpace(mode)) return;
        Mode = mode;

        var report = Environment.GetEnvironmentVariable("AMLL_HARNESS_REPORT");
        if (string.IsNullOrWhiteSpace(report) && fromArgs is not null)
        {
            var i = Array.IndexOf(args, "--" + fromArgs);
            // -ui-hold / -ui-target / file-first modes take a FILE (or other
            // non-report value) as their first positional argument, not a
            // report path. Treating it as one wrote the .started marker next
            // to the audio file — and worse, the watchdog and exit handler
            // appended VERDICT/DIED-CONTEXT lines INTO the audio file,
            // corrupting test data. Only the report modes derive a path from
            // args; everything else waits for the AMLL_HARNESS_REPORT env var
            // and otherwise defaults to temp.
            var firstPositional = (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
            var positionalIsReport = fromArgs is not (null
                or "seek-ui-hold" or "seek-ui-target" or "minimize-ui-hold"
                or "playerbar-ui-hold" or "playerbar-synth-hold" or "seek-drag"
                or "load-lyrics" or "lyrics-test")
                && firstPositional != "rate";
            if (positionalIsReport && i >= 0 && i + 1 < args.Length)
                report = args[i + 1];
        }
        Report = string.IsNullOrWhiteSpace(report)
            ? Path.Combine(Path.GetTempPath(), $"{Mode}-report.txt")
            // Defense in depth for the corruption we observed (report lines
            // appended into an .m4a): an audio-extension report falls back
            // to temp instead of destroying test data.
            : AmllTtmlTool.Core.Audio.AudioInput.GuardReportPath(report, Mode);
        string? timeoutEnv = Environment.GetEnvironmentVariable("AMLL_HARNESS_TIMEOUT");
        _timeoutSec = int.TryParse(timeoutEnv, out var t) && t > 0 ? t : DefaultTimeout(Mode);

        WriteMarker();
        Watchdog.Change(TimeSpan.FromSeconds(_timeoutSec), Timeout.InfiniteTimeSpan);

        // Belt-and-braces for the death we actually observed: a managed
        // exception on a WinRT/apartment thread exits the process with
        // 0xE0434352 without firing Application.UnhandledException or
        // AppDomain.UnhandledException. Subscribe to the process-level
        // exit and dump whatever the report holds, so the last harness
        // line before death is always visible.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Report is null) return;
                var lines = File.Exists(Report) ? File.ReadAllLines(Report) : Array.Empty<string>();
                var last = lines.Length > 0 ? lines[^1] : "<empty>";
                File.AppendAllText(Report,
                    $"DIED-CONTEXT: process exit at {DateTime.UtcNow:u}; " +
                    $"last-report-line: {last}\n");
            }
            catch { }
        };
    }

    private static void WriteMarker()
    {
        // Marker-write failures must be VISIBLE: a bare catch here is what
        // made an unwired run look like a silent hang with no evidence.
        try
        {
            var exe = Environment.ProcessPath ?? "?";
            var buildUtc = File.Exists(exe) ? File.GetLastWriteTimeUtc(exe).ToString("u") : "?";
            var sb = new StringBuilder();
            sb.AppendLine($"mode={Mode}");
            sb.AppendLine($"pid={Environment.ProcessId}");
            sb.AppendLine($"report={Report}");
            sb.AppendLine($"timeoutSec={_timeoutSec}");
            sb.AppendLine($"buildUtc={buildUtc}");
            sb.AppendLine($"startedUtc={DateTime.UtcNow:u}");
            if (_captureError is not null) sb.AppendLine($"captureError={_captureError}");
            File.WriteAllText(Report + ".started", sb.ToString());
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "harness-host-error.log"),
                    $"{DateTime.UtcNow:u} marker-write failed mode={Mode} report={Report}\n{ex}\n");
            }
            catch { }
        }
    }


    // Marks the run complete: stops the watchdog and writes the final
    // machine-readable verdict from the last line the harness wrote
    // (harnesses end with "<tag> | OK" or "<tag> | FAIL").
    public static void Finish()
    {
        lock (Gate)
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        }
        Watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            if (Report is null) return;
            var ok = false;
            if (File.Exists(Report))
            {
                var lines = File.ReadAllLines(Report)
                    .Where(l => l.Trim().Length > 0)
                    .ToArray();
                if (lines.Length > 0)
                {
                    var last = lines[^1].TrimEnd();
                    ok = last.EndsWith("OK", StringComparison.Ordinal)
                        && !last.EndsWith("FAIL", StringComparison.Ordinal);
                }
            }
            var verdict = ok
                ? $"VERDICT: PASS elapsed={Clock.Elapsed.TotalSeconds:F1}s"
                : "VERDICT: FAIL (last line was not an OK verdict)";
            File.AppendAllText(Report, verdict + "\n");
        }
        catch { }
    }

    public static void WriteLine(string text)
    {
        try { if (Report is not null) File.AppendAllText(Report, text + "\n"); } catch { }
    }
}
#endif
