using System.Text;

namespace AmllTtmlTool.WinUI.Services;

// Structured audio-error text. The whole point: an exception must never
// render as an empty pair of parentheses again. Every field falls back to
// an explicit marker, and the full chain (types, messages, HRESULTs,
// ffmpeg stderr/exit code) travels together.
public static class AudioErrorText
{
    public static string MessageOf(Exception ex)
    {
        var msg = ex.Message;
        if (!string.IsNullOrWhiteSpace(msg)) return msg.Trim();
        return $"{ex.GetType().Name} (no message; HResult=0x{ex.HResult:X8})";
    }

    public static string Chain(Exception ex)
    {
        var sb = new StringBuilder();
        var depth = 0;
        for (var cur = ex; cur is not null && depth < 8; cur = cur.InnerException, depth++)
            sb.AppendLine($"- {cur.GetType().Name}: {MessageOf(cur)} (HResult=0x{cur.HResult:X8})");
        return sb.ToString().TrimEnd();
    }

    public static string FullReport(Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Chain(ex));
        var proc = FindFfmpegFailure(ex);
        if (proc is not null)
        {
            sb.AppendLine($"ffmpeg exe: {proc.Exe}");
            sb.AppendLine($"ffmpeg args: {proc.Args}");
            sb.AppendLine($"ffmpeg exit code: {proc.ExitCode}");
            sb.AppendLine("ffmpeg stderr:");
            sb.AppendLine(string.IsNullOrWhiteSpace(proc.Stderr) ? "<stderr empty>" : proc.Stderr.Trim());
        }
        return sb.ToString().TrimEnd();
    }

    private static FfmpegProcessException? FindFfmpegFailure(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
            if (cur is FfmpegProcessException proc) return proc;
        return null;
    }
}
