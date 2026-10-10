// Crash log writer: exception + stack + UTC timestamp, appended to one
// file in the app's local data folder (no capabilities needed, works in
// Debug AND Release). Best-effort everywhere: logging must never throw.
namespace AmllTtmlTool.WinUI.Services;

public static class CrashLog
{
    public static string? LastFilePath { get; private set; }

    public static string FilePath
    {
        get
        {
            if (LastFilePath is not null) return LastFilePath;
            // Packaged identity: the real per-app LocalState folder.
            try
            {
                var dir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                LastFilePath = System.IO.Path.Combine(dir, "amll-crash.log");
                return LastFilePath;
            }
            catch { }
            // Unpackaged (this app): ApplicationData has no identity, so use
            // the app's own folder under %LOCALAPPDATA% instead.
            try
            {
                var dir = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                    "AmllTtmlTool");
                System.IO.Directory.CreateDirectory(dir);
                LastFilePath = System.IO.Path.Combine(dir, "amll-crash.log");
            }
            catch
            {
                LastFilePath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), "amll-crash.log");
            }
            return LastFilePath;
        }
    }

    public static void Write(string source, Exception? ex)
    {
        try
        {
            var entry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z] {source}\n" +
                $"{ex?.ToString() ?? "(no exception object)"}\n---\n";
            System.IO.File.AppendAllText(FilePath, entry);
        }
        catch { }
    }

    public static long LineCount()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath)) return 0;
            var n = 0L;
            using var r = new System.IO.StreamReader(FilePath);
            while (r.ReadLine() is not null) n++;
            return n;
        }
        catch { return -1; }
    }
}
