// Layout-test harness: self-running title-centering measurement, kept behind
// --layout-test / AMLL_SELF_CHECK for the ribbon/separator/animation items.
// Writes %TEMP%\title-center-check.txt and closes the window itself.
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using System.Text;
using Windows.Graphics;

namespace AmllTtmlTool.WinUI.Services;

internal sealed record TitleProbe(
    FrameworkElement TitleBarArea,
    FrameworkElement CenterBlock,
    FrameworkElement Nav,
    FrameworkElement RightFull,
    FrameworkElement RightCompact,
    Func<string?> CurrentState);

internal static class TitleCenteringCheck
{
    public static readonly int[] Widths =
        [1400, 1116, 1100, 1050, 1000, 900, 850, 800, 750, 700, 672, 640, 600];

    public static string ReportFile { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "title-center-check.txt");

    public static async Task RunAsync(Window window, TitleProbe probe)
    {
        WriteLine("=== TITLE CENTER BASELINE ===");
        try
        {
            AppWindow? appWindow = null;
            double scale = 1;
            await OnUiAsync(window, () =>
            {
                appWindow = window.AppWindow;
                scale = WindowSizing.GetScale(window);
            });
            if (appWindow is null) { WriteLine("HARNESS-ERROR: no AppWindow"); return; }
            WriteLine($"scale={scale:F3}");
            FrameworkElement? leftCluster = null;
            await OnUiAsync(window, () =>
            {
                foreach (var child in probe.TitleBarArea is Grid g ? g.Children : Enumerable.Empty<UIElement>())
                {
                    if (child is FrameworkElement fe && Grid.GetColumn(fe) == 1) { leftCluster = fe; break; }
                }
            });
            if (leftCluster is null) { WriteLine("HARNESS-ERROR: left cluster not found"); return; }
            foreach (var w in Widths)
            {
                try
                {
                    await OnUiAsync(window, () =>
                        appWindow.Resize(new SizeInt32(
                            (int)Math.Round(w * scale),
                            (int)Math.Round(700 * scale))));
                }
                catch (Exception ex)
                {
                    WriteLine($"---- client width {w}: RESIZE-ERROR {ex.GetType().Name}: {ex.Message} ----");
                    continue;
                }
                await Task.Delay(350);
                await OnUiAsync(window, () => { });
                await OnUiAsync(window, () => { });
                string line = "";
                try
                {
                    await OnUiAsync(window, () => { line = Measure(probe, leftCluster!, appWindow, scale, w); });
                }
                catch (Exception ex)
                {
                    line = $"---- client width {w}: MEASURE-ERROR {ex.GetType().Name}: {ex.Message} ----";
                }
                WriteLine(line);
            }
        }
        catch (Exception ex)
        {
            WriteLine("HARNESS-ERROR: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void WriteLine(string text)
    {
        try { System.IO.File.AppendAllText(ReportFile, text + "\n"); } catch { }
        System.Diagnostics.Debug.WriteLine(text);
    }

    private static string Measure(TitleProbe p, FrameworkElement leftCluster, AppWindow appWindow, double scale, int w)
    {
        var sb = new StringBuilder();
        try
        {
            var area = p.TitleBarArea;
            var areaW = area.ActualWidth;
            var leftRight = RectOf(leftCluster, area).Right;
            var rightPanel = p.RightFull.Visibility == Visibility.Visible ? p.RightFull : p.RightCompact;
            var rightRect = RectOf(rightPanel, area);
            var unitRect = RectOf(p.CenterBlock, area);
            var navRect = RectOf(p.Nav, area);
            var rightInset = appWindow.TitleBar.RightInset / scale;
            var leftInset = appWindow.TitleBar.LeftInset / scale;

            var windowCenter = areaW / 2;
            var gapCenter = (leftRight + rightRect.Left) / 2;
            var unitCenter = unitRect.Left + unitRect.Width / 2;
            var navCenter = navRect.Left + navRect.Width / 2;

            double PartWidth(string name)
            {
                var el = FindByName(p.Nav, name) as FrameworkElement;
                return el is null ? double.NaN : el.ActualWidth;
            }
            var pillsW = VisualTreeHelper.GetChildrenCount(p.CenterBlock) > 0
                ? (VisualTreeHelper.GetChild(p.CenterBlock, 0) as FrameworkElement)?.ActualWidth ?? double.NaN
                : double.NaN;

            sb.AppendLine($"---- client width {w} (area {areaW:F1}, state {p.CurrentState() ?? "?"}) ----");
            sb.AppendLine($"  insets L={leftInset:F1} R={rightInset:F1}");
            sb.AppendLine($"  leftClusterRight={leftRight:F1} rightClusterLeft={rightRect.Left:F1} rightClusterRight={rightRect.Right:F1}");
            sb.AppendLine($"  unit L={unitRect.Left:F1} W={unitRect.Width:F1} center={unitCenter:F1}");
            var navCtrl = p.Nav as Microsoft.UI.Xaml.Controls.NavigationView;
            var navWidthProp = navCtrl is not null
                ? (double.IsNaN(navCtrl.Width) ? "NaN" : navCtrl.Width.ToString("F0"))
                : "?";
            sb.AppendLine($"  navWidthProp={navWidthProp}");
            sb.AppendLine($"  nav  L={navRect.Left:F1} W={navRect.Width:F1} center={navCenter:F1}");
            sb.AppendLine($"  pillsW={pillsW:F1} itemsHost={PartWidth("TopNavMenuItemsHost"):F1} overflowBtn={PartWidth("TopNavOverflowButton"):F1} customContent={PartWidth("PaneCustomContentOnTopPane"):F1}");
            sb.AppendLine($"  offWindow={unitCenter - windowCenter:+0.0;-0.0} offGap={unitCenter - gapCenter:+0.0;-0.0} offNavVsUnit={navCenter - unitCenter:+0.0;-0.0}");
            sb.AppendLine($"  clearL={unitRect.Left - leftRight:F1} clearR={rightRect.Left - unitRect.Right:F1} clearCaption={areaW - rightInset - rightRect.Right:F1}");
            if (w == 1400 || w == 750)
            {
                sb.AppendLine("  nav subtree (name/type=W/vis):");
                var queue = new Queue<DependencyObject>();
                queue.Enqueue(p.Nav);
                int seen = 0;
                while (queue.Count > 0 && seen < 120)
                {
                    var current = queue.Dequeue();
                    seen++;
                    if (current is FrameworkElement fe && (fe.ActualWidth > 1 || !string.IsNullOrEmpty(fe.Name)))
                    {
                        var extra = "";
                        if (fe.MinWidth > 0.5) extra += $" minW={fe.MinWidth:F0}";
                        if (!double.IsNaN(fe.Width)) extra += $" W={fe.Width:F0}";
                        sb.AppendLine($"    {fe.Name}/{fe.GetType().Name}={fe.ActualWidth:F1}/{fe.Visibility.ToString()[0]}{extra}");
                    }
                    var count = VisualTreeHelper.GetChildrenCount(current);
                    for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"---- client width {w}: MEASURE-ERROR {ex.Message} ----");
        }
        return sb.ToString();
    }

    private struct RectD { public double Left; public double Width; public double Right; }

    private static RectD RectOf(FrameworkElement element, FrameworkElement host)
    {
        var origin = element.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point(0, 0));
        return new RectD { Left = origin.X, Width = element.ActualWidth, Right = origin.X + element.ActualWidth };
    }

    private static DependencyObject? FindByName(DependencyObject root, string name)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is FrameworkElement fe && fe.Name == name) return current;
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
        }
        return null;
    }

    private static Task OnUiAsync(Window window, Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }))
        {
            tcs.TrySetException(new InvalidOperationException("TryEnqueue failed"));
        }
        return tcs.Task;
    }
}
