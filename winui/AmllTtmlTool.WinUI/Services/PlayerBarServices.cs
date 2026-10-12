// Player-bar test seams. The track tip's "Open file" and "View in
// Explorer" actions go through these small interfaces so test mode never
// opens a real native dialog (which would hang a run) or a real Explorer
// window. Stubs record their calls to a log file the UI driver reads.
using AmllTtmlTool.Core.Lyrics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AmllTtmlTool.WinUI.Services;

public interface IFilePickerService
{
    /// <summary>Picks an audio file; null when the user cancels.</summary>
    Task<string?> PickOpenAudioAsync();
}

public interface IShellService
{
    /// <summary>Opens Explorer with the file selected.</summary>
    void ShowInExplorer(string path);
}

public sealed class FilePickerService(Window window) : IFilePickerService
{
    public async Task<string?> PickOpenAudioAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
        picker.ViewMode = PickerViewMode.List;
        foreach (var ext in LyricStore.AudioExtensions)
            picker.FileTypeFilter.Add("." + ext);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}

public sealed class ShellService : IShellService
{
    public void ShowInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch { }
    }
}

#if DEBUG
// Test doubles, selected when HarnessHost.IsTest. The picker returns the
// AMLL_PICKER_STUB path (or null when unset, i.e. "user cancelled"); every
// call is appended to a log the ps1 driver asserts against.
public sealed class TestFilePickerService : IFilePickerService
{
    public static string LogFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "playerbar-stub-calls.log");

    public Task<string?> PickOpenAudioAsync()
    {
        var stub = Environment.GetEnvironmentVariable("AMLL_PICKER_STUB");
        try
        {
            System.IO.File.AppendAllText(LogFile,
                $"picker|{(string.IsNullOrEmpty(stub) ? "<cancel>" : stub)}\n");
        }
        catch { }
        return Task.FromResult(string.IsNullOrEmpty(stub) ? null : stub);
    }
}

public sealed class TestShellService : IShellService
{
    public void ShowInExplorer(string path)
    {
        try
        {
            System.IO.File.AppendAllText(TestFilePickerService.LogFile,
                $"explorer|{path}\n");
        }
        catch { }
    }
}
#endif
