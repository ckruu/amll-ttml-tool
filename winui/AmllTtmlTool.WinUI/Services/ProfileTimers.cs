#if DEBUG
// Step-1 stage profiler: named Stopwatch accumulators, DEBUG-only.
// Bodies are empty in release (call sites are #if-guarded anyway, so the
// release binary cannot observe or pay for profiling).
namespace AmllTtmlTool.WinUI.Services;

internal static class ProfileTimers
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<double>> Samples = new();

    public static double Time(System.Action a)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        a();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    public static void Add(string stage, double ms)
    {
        lock (Gate)
        {
            if (!Samples.TryGetValue(stage, out var list))
                Samples[stage] = list = new List<double>();
            list.Add(ms);
        }
    }

    public static void Reset()
    {
        lock (Gate) { Samples.Clear(); }
    }

    public static string Table()
    {
        var sb = new System.Text.StringBuilder();
        lock (Gate)
        {
            foreach (var (stage, list) in Samples.OrderBy(kv => kv.Key))
            {
                if (list.Count == 0) continue;
                var s = list.OrderBy(v => v).ToList();
                static string F(double v) =>
                    v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                sb.AppendLine($" PS {stage} n={s.Count} " +
                    $"median={F(s[s.Count / 2])} " +
                    $"p95={F(s[(int)(s.Count * 0.95)])} " +
                    $"max={F(s[^1])}");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
#endif
