using System.Runtime.InteropServices;
using AmllTtmlTool.Core.Lyrics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AmllTtmlTool.WinUI.Services;

public static class FileDialogs
{
    public static nint HwndFor(Window window) => WindowNative.GetWindowHandle(window);

    private static void InitPicker(object picker, Window window) =>
        InitializeWithWindow.Initialize(picker, HwndFor(window));

    public static async Task<StorageFile?> PickOpenLyricAsync(Window window)
    {
        var picker = new FileOpenPicker();
        InitPicker(picker, window);
        picker.ViewMode = PickerViewMode.List;
        foreach (var ext in LyricStore.LyricExtensions)
            picker.FileTypeFilter.Add("." + ext);
        return await picker.PickSingleFileAsync();
    }

    public static async Task<StorageFile?> PickOpenAudioAsync(Window window)
    {
        var picker = new FileOpenPicker();
        InitPicker(picker, window);
        picker.ViewMode = PickerViewMode.List;
        foreach (var ext in LyricStore.AudioExtensions)
            picker.FileTypeFilter.Add("." + ext);
        return await picker.PickSingleFileAsync();
    }

    public static async Task<StorageFile?> PickSaveTtmlAsync(Window window, string suggestedName, bool appleFlavor)
    {
        return await PickSaveFileAsync(window, appleFlavor
            ? System.IO.Path.GetFileNameWithoutExtension(suggestedName) + ".apple"
            : System.IO.Path.GetFileNameWithoutExtension(suggestedName));
    }

    public static async Task<StorageFile?> PickSaveFileAsync(Window window, string suggestedName)
    {
        var picker = new FileSavePicker();
        InitPicker(picker, window);
        picker.SuggestedFileName = suggestedName;
        picker.FileTypeChoices.Add("TTML lyrics", [".ttml"]);
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        return await picker.PickSaveFileAsync();
    }

    public static void CopyText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    public static async Task<string?> GetClipboardTextAsync()
    {
        var view = Clipboard.GetContent();
        if (!view.Contains(StandardDataFormats.Text)) return null;
        try
        {
            return await view.GetTextAsync();
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static async Task ShowErrorAsync(UIElement anchor, string title, string message, string? details = null)
    {
        UIElement content;
        if (string.IsNullOrWhiteSpace(details))
        {
            content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        }
        else
        {
            // Readable sentence up front, full technical cause one click away.
            content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new Expander
                    {
                        Header = "Details",
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Content = new ScrollViewer
                        {
                            Content = new TextBlock
                            {
                                Text = details,
                                TextWrapping = TextWrapping.Wrap,
                                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                                FontSize = 12,
                            },
                            MaxHeight = 320,
                        },
                    },
                },
            };
        }
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            CloseButtonText = "OK",
            XamlRoot = anchor.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    public static async Task<bool> ConfirmAsync(UIElement anchor, string title, string message, string confirmLabel)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirmLabel,
            CloseButtonText = "Cancel",
            XamlRoot = anchor.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
