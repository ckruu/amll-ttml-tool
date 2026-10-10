namespace AmllTtmlTool.WinUI.Services;

// A failed ffmpeg/ffprobe child process, with everything needed to
// diagnose it: exit code, full stderr, and the exact command line.
// Callers must surface these (see AudioErrorText) instead of keeping
// only the first stderr line.
public sealed class FfmpegProcessException : InvalidOperationException
{
    public string Exe { get; }
    public string Args { get; }
    public int ExitCode { get; }
    public string Stderr { get; }

    public FfmpegProcessException(string message, string exe, string args, int exitCode, string stderr)
        : base(message)
    {
        Exe = exe;
        Args = args;
        ExitCode = exitCode;
        Stderr = stderr;
    }
}
