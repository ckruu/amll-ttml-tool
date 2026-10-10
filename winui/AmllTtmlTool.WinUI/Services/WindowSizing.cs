using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace AmllTtmlTool.WinUI.Services;

public static class WindowSizing
{
    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT Reserved;
        public POINT MaxSize;
        public POINT MaxPosition;
        public POINT MinTrackSize;
        public POINT MaxTrackSize;
    }

    private delegate nint SubclassProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(nint hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(nint hWnd);

    /// <summary>
    /// Real DPI for the window. <see cref="XamlRoot.RasterizationScale"/> is null
    /// in a constructor and stale after AppWindow.Move, so title-bar inset math
    /// must not rely on it.
    /// </summary>
    public static double GetScale(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private static int _minWidthLogical = 560;
    private static int _minHeightLogical = 480;
    private static SubclassProc? _proc;

    public static void SetMinSize(Window window, int minWidthLogical, int minHeightLogical)
    {
        _minWidthLogical = minWidthLogical;
        _minHeightLogical = minHeightLogical;
        var hwnd = WindowNative.GetWindowHandle(window);
        _proc = OnSubclass;
        SetWindowSubclass(hwnd, _proc, 0, 0);
    }

    private static nint OnSubclass(nint hWnd, uint msg, nint wParam, nint lParam, nuint uId, nuint data)
    {
        if (msg == WM_GETMINMAXINFO && lParam != nint.Zero)
        {
            var dpi = GetDpiForWindow(hWnd);
            if (dpi <= 0) dpi = 96;
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.MinTrackSize = new POINT
            {
                X = Math.Max(info.MinTrackSize.X, _minWidthLogical * dpi / 96),
                Y = Math.Max(info.MinTrackSize.Y, _minHeightLogical * dpi / 96),
            };
            Marshal.StructureToPtr(info, lParam, false);
            return nint.Zero;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
