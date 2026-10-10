// Layout-test harness: self-running title-centering measurement, kept behind
// --layout-test / AMLL_SELF_CHECK for the ribbon/separator/animation items.
// Writes %TEMP%\title-center-check.txt and closes the window itself.
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
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
        [1400, 1116, 1100, 1050, 1000, 900, 850, 800, 760, 750, 720, 700, 680, 672, 640, 600];

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
                    await OnUiAsync(window, () => { line = Measure(probe, leftCluster!, appWindow, scale, w, window); });
                }
                catch (Exception ex)
                {
                    line = $"---- client width {w}: MEASURE-ERROR {ex.GetType().Name}: {ex.Message} ----";
                }
                WriteLine(line);
                try
                {
                    var overflowLine = await VerifyOverflowMenuAsync(window, probe);
                    if (!string.IsNullOrEmpty(overflowLine)) WriteLine(overflowLine);
                }
                catch (Exception ex)
                {
                    WriteLine($"---- client width {w}: OVERFLOW-ERROR {ex.GetType().Name}: {ex.Message} ----");
                }
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

    private static string Measure(TitleProbe p, FrameworkElement leftCluster, AppWindow appWindow, double scale, int w, Window window)
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
            sb.Append(VerifyCenterStatus(p, window));
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

    private static string VerifyCenterStatus(TitleProbe p, Window window)
    {
        var sb = new StringBuilder();
        try
        {
            var panel = FindByName(p.CenterBlock, "MasterTogglePanel") as FrameworkElement;
            var overflow = FindByName(p.CenterBlock, "CenterToggleOverflowButton") as Button;
            var roman = FindByName(p.CenterBlock, "RomanToggle") as Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;
            var trans = FindByName(p.CenterBlock, "TransToggle") as Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;
            var wordRoman = FindByName(p.CenterBlock, "WordRomanToggle") as Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;
            var edit = FindByName(p.Nav, "EditNavItem") as NavigationViewItem;
            var sync = FindByName(p.Nav, "SyncNavItem") as NavigationViewItem;
            var preview = FindByName(p.Nav, "PreviewNavItem") as NavigationViewItem;
            var navOverflow = FindByName(p.Nav, "TopNavOverflowButton") as FrameworkElement;
            if (panel is null || overflow is null || roman is null || trans is null || wordRoman is null ||
                edit is null || sync is null || preview is null || navOverflow is null)
            {
                sb.AppendLine("  VERIFY-FAIL center control lookup missed one or more named parts");
                return sb.ToString();
            }

            var overflowActive = overflow.Visibility == Visibility.Visible && overflow.ActualWidth > 0.5;
            var navOverflowActive = navOverflow.Visibility == Visibility.Visible && navOverflow.ActualWidth > 0.5;
            sb.AppendLine($"  centerParts panel={panel.Visibility.ToString()[0]}/{panel.ActualWidth:F1} overflow={overflow.Visibility.ToString()[0]}/{overflow.ActualWidth:F1} roman={roman.Visibility.ToString()[0]}/{roman.ActualWidth:F1} trans={trans.Visibility.ToString()[0]}/{trans.ActualWidth:F1} word={wordRoman.Visibility.ToString()[0]}/{wordRoman.ActualWidth:F1}");
            string Status(FrameworkElement element, bool inOverflow)
            {
                var inBar = IsEffectivelyVisible(element, p.CenterBlock) && element.ActualWidth > 0.5;
                if (inBar && !inOverflow) return "bar";
                if (!inBar && inOverflow) return "overflow";
                return "MISSING";
            }
            var romanStatus = Status(roman, overflowActive);
            var transStatus = Status(trans, overflowActive);
            var wordStatus = Status(wordRoman, overflowActive);
            var editStatus = Status(edit, false);
            var syncStatus = Status(sync, false);
            var previewStatus = Status(preview, false);
            sb.AppendLine($"  centerControls roman={romanStatus} trans={transStatus} word={wordStatus} edit={editStatus} sync={syncStatus} preview={previewStatus} overflow={(overflowActive ? "active" : "idle")} navOverflow={(navOverflowActive ? "ACTIVE" : "idle")}");
            if (navOverflowActive)
            {
                sb.AppendLine("  VERIFY-FAIL NavigationView moved tabs into its internal overflow");
                return sb.ToString();
            }
            if (!overflowActive)
            {
                if (romanStatus == "bar" && transStatus == "bar" && wordStatus == "bar" &&
                    editStatus == "bar" && syncStatus == "bar" && previewStatus == "bar")
                    sb.AppendLine("  VERIFY-PASS all six center controls are visible in the bar");
                else
                    sb.AppendLine("  VERIFY-FAIL a center control is missing while the custom overflow is idle");
                return sb.ToString();
            }
            if (romanStatus != "overflow" || transStatus != "overflow" || wordStatus != "overflow" ||
                editStatus != "bar" || syncStatus != "bar" || previewStatus != "bar")
            {
                sb.AppendLine("  VERIFY-FAIL pills did not move cleanly into overflow, or a tab left the bar");
                return sb.ToString();
            }

            var origin = overflow.TransformToVisual(p.CenterBlock)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var left = origin.X;
            var top = origin.Y;
            var right = left + overflow.ActualWidth;
            var bottom = top + overflow.ActualHeight;
            var clipped = left < -0.5 || top < -0.5 ||
                right > p.CenterBlock.ActualWidth + 0.5 || bottom > p.CenterBlock.ActualHeight + 0.5;
            var center = new Windows.Foundation.Point(left + overflow.ActualWidth / 2, top + overflow.ActualHeight / 2);
            UIElement? topHit = null;
            UIElement? chromeHit = null;
            if (window.Content is UIElement content)
            {
                var contentOrigin = overflow.TransformToVisual(content)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                var contentCenter = new Windows.Foundation.Point(
                    contentOrigin.X + overflow.ActualWidth / 2,
                    contentOrigin.Y + overflow.ActualHeight / 2);
                foreach (var hit in VisualTreeHelper.FindElementsInHostCoordinates(contentCenter, content))
                {
                    topHit = hit;
                    break;
                }
                var chromePoint = new Windows.Foundation.Point(contentOrigin.X + 3, contentOrigin.Y + 3);
                foreach (var hit in VisualTreeHelper.FindElementsInHostCoordinates(chromePoint, content))
                {
                    chromeHit = hit;
                    break;
                }
            }
            var topOk = topHit is not null && IsWithin(topHit, overflow);
            var chromeOk = chromeHit is not null && IsWithin(chromeHit, overflow);
            sb.AppendLine($"  overflowButton W={overflow.ActualWidth:F1} H={overflow.ActualHeight:F1} opacity={overflow.Opacity:F2} clipped={clipped} top={Describe(topHit)} chrome={Describe(chromeHit)}");
            if (overflow.ActualWidth < 1 || overflow.ActualHeight < 1 || overflow.Opacity < 0.99 || clipped || !topOk || !chromeOk)
                sb.AppendLine("  VERIFY-FAIL custom overflow button is not visible, unclipped, and top-hit-testable");
            else
                sb.AppendLine("  VERIFY-PASS custom overflow button is visible, unclipped, and top-hit-testable");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  VERIFY-FAIL center status exception {ex.GetType().Name}: {ex.Message}");
        }
        return sb.ToString();
    }

    private static async Task<string> VerifyOverflowMenuAsync(Window window, TitleProbe p)
    {
        var active = false;
        await OnUiAsync(window, () =>
        {
            var overflow = FindByName(p.CenterBlock, "CenterToggleOverflowButton") as Button;
            var menu = overflow?.Flyout as MenuFlyout;
            active = overflow is not null && overflow.Visibility == Visibility.Visible && overflow.ActualWidth > 0.5;
            if (!active) return;
            if (overflow is null || menu is null)
                throw new InvalidOperationException("overflow active without named button/menu");
            try { menu.Hide(); } catch { }
            menu.ShowAt(overflow);
        });
        if (!active) return string.Empty;
        await Task.Delay(300);
        var result = string.Empty;
        await OnUiAsync(window, () =>
        {
            try
            {
                var overflow = FindByName(p.CenterBlock, "CenterToggleOverflowButton") as Button;
                var menu = overflow?.Flyout as MenuFlyout;
                var roman = FindByName(p.CenterBlock, "RomanToggle") as Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;
                var trans = FindByName(p.CenterBlock, "TransToggle") as Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;
                var wordRoman = FindByName(p.CenterBlock, "WordRomanToggle") as Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;
                if (overflow is null || menu is null || roman is null || trans is null || wordRoman is null)
                {
                    result = "  VERIFY-FAIL overflow menu lookup missed one or more named parts";
                    return;
                }
                var items = new List<ToggleMenuFlyoutItem>();
                foreach (var item in menu.Items)
                    if (item is ToggleMenuFlyoutItem toggleItem) items.Add(toggleItem);
                ToggleMenuFlyoutItem? Find(string tag)
                {
                    foreach (var item in items)
                        if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal)) return item;
                    return null;
                }
                var romanItem = Find("RomanToggle");
                var transItem = Find("TransToggle");
                var wordItem = Find("WordRomanToggle");
                var enabled = 0;
                foreach (var item in items) if (item.IsEnabled) enabled++;
                var synced = romanItem is not null && transItem is not null && wordItem is not null &&
                    romanItem.IsChecked == (roman.IsChecked == true) &&
                    transItem.IsChecked == (trans.IsChecked == true) &&
                    wordItem.IsChecked == (wordRoman.IsChecked == true);
                if (items.Count == 3 && enabled == 3 && synced &&
                    !string.IsNullOrEmpty(romanItem!.Text) &&
                    !string.IsNullOrEmpty(transItem!.Text) &&
                    !string.IsNullOrEmpty(wordItem!.Text))
                    result = $"  VERIFY-PASS overflow menu items=3 enabled=3 synced=True roman={romanItem.IsChecked} trans={transItem.IsChecked} word={wordItem.IsChecked}";
                else
                    result = $"  VERIFY-FAIL overflow menu items={items.Count} enabled={enabled} synced={synced}";
            }
            finally
            {
                try
                {
                    var overflow = FindByName(p.CenterBlock, "CenterToggleOverflowButton") as Button;
                    (overflow?.Flyout as MenuFlyout)?.Hide();
                }
                catch { }
            }
        });
        await Task.Delay(120);
        return result;
    }

    private static bool IsEffectivelyVisible(FrameworkElement element, FrameworkElement root)
    {
        var current = element;
        while (current is not null)
        {
            if (current.Visibility != Visibility.Visible) return false;
            if (ReferenceEquals(current, root)) return true;
            current = VisualTreeHelper.GetParent(current) as FrameworkElement;
        }
        return false;
    }

    private static bool IsWithin(DependencyObject child, DependencyObject parent)    {
        var current = child;
        while (current is not null)
        {
            if (ReferenceEquals(current, parent)) return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static string Describe(UIElement? element)
    {
        if (element is null) return "null";
        var chain = new List<string>();
        var current = element as DependencyObject;
        for (var depth = 0; current is not null && depth < 6; depth++)
        {
            chain.Add(current is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name)
                ? fe.Name + "/" + current.GetType().Name
                : current.GetType().Name);
            current = VisualTreeHelper.GetParent(current);
        }
        return string.Join(">", chain);
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
