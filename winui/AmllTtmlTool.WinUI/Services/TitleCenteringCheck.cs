// Layout-test harness: self-running title-centering measurement, kept behind
// --layout-test / AMLL_SELF_CHECK for the ribbon/separator/animation items.
// Writes %TEMP%\title-center-check.txt and closes the window itself.
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Automation;
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
                try
                {
                    var secondaryLine = await VerifySecondaryFieldsAsync(window, w);
                    if (!string.IsNullOrEmpty(secondaryLine)) WriteLine(secondaryLine);
                }
                catch (Exception ex)
                {
                    WriteLine($"---- client width {w}: SECONDARY-ERROR {ex.GetType().Name}: {ex.Message} ----");
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

    private static async Task<string> VerifySecondaryFieldsAsync(Window window, int w)
    {
        FrameworkElement? editBar = null;
        var editBarActive = false;
        await OnUiAsync(window, () =>
        {
            if (window.Content is DependencyObject content)
                editBar = FindByName(content, "EditBar") as FrameworkElement;
            editBarActive = editBar is not null && editBar.Visibility == Visibility.Visible;
        });
        if (!editBarActive)
            return $"  secondary w={w}: skipped (EditBar not active)";
        var sb = new StringBuilder();
        var origTrans = false;
        var origRoman = false;
        await OnUiAsync(window, () =>
        {
            origTrans = AppSettings.ShowTranslation;
            origRoman = AppSettings.ShowRomanization;
        });
        try
        {
            FrameworkElement bar = editBar!;
            async Task PumpAsync()
            {
                await Task.Delay(150);
                await OnUiAsync(window, () => { });
            }
            async Task SetTogglesAsync(bool trans, bool roman)
            {
                await OnUiAsync(window, () =>
                {
                    AppSettings.ShowTranslation = trans;
                    AppSettings.ShowRomanization = roman;
                    AppSettings.NotifyDisplayChanged();
                });
                await PumpAsync();
            }
            async Task<(string Group, string Btn, string TransRow, string RomanRow)> ReadStateAsync()
            {
                var snap = ("?", "?", "?", "?");
                await OnUiAsync(window, () =>
                {
                    snap = (
                        Vis(FindByName(bar, "SecondaryGroup") as FrameworkElement),
                        Vis(FindByName(bar, "SecondaryOverflowButton") as FrameworkElement),
                        RowVis(FindByName(bar, "TransLabel") as FrameworkElement, FindByName(bar, "TransBox") as FrameworkElement),
                        RowVis(FindByName(bar, "RomanLabel") as FrameworkElement, FindByName(bar, "RomanBox") as FrameworkElement));
                });
                return snap;
            }
            // Opens the secondary flyout when its button is up, reads the two
            // fields, and closes it. Returns opened=false when hidden.
            async Task<(bool Opened, string Trans, string Roman, bool SyncT, bool SyncR, string Edge)> ReadFlyoutAsync()
            {
                (bool Opened, string Trans, string Roman, bool SyncT, bool SyncR, string Edge) snap = (false, "?", "?", false, false, "");
                await OnUiAsync(window, () =>
                {
                    var button = FindByName(bar, "SecondaryOverflowButton") as Button;
                    if (button is null || button.Visibility != Visibility.Visible || button.ActualWidth <= 0.5)
                        return;
                    if (button.Flyout is Flyout flyout && flyout.Content is FrameworkElement)
                    {
                        try { flyout.ShowAt(button); snap = (true, "?", "?", false, false, ""); }
                        catch { snap = (false, "showfail", "?", false, false, ""); }
                    }
                });
                if (!snap.Opened) return snap;
                await Task.Delay(250);
                await OnUiAsync(window, () =>
                {
                    try
                    {
                        var button = FindByName(bar, "SecondaryOverflowButton") as Button;
                        var root = (button?.Flyout as Flyout)?.Content as FrameworkElement;
                        if (root is null) { snap = (true, "lost", "?", false, false, ""); return; }
                        var transBox = FindByAutomationName(root, "Translation");
                        var romanBox = FindByAutomationName(root, "Romanization");
                        var mainTrans = FindByName(bar, "TransBox") as TextBox;
                        var mainRoman = FindByName(bar, "RomanBox") as TextBox;
                        var edge = transBox is not null && romanBox is not null && Shown(transBox) && Shown(romanBox)
                            ? Edges(transBox, romanBox, root) : "";
                        snap = (true, Field(transBox), Field(romanBox),
                            TextEq(transBox, mainTrans), TextEq(romanBox, mainRoman), edge);
                    }
                    finally
                    {
                        try { ((FindByName(bar, "SecondaryOverflowButton") as Button)?.Flyout as Flyout)?.Hide(); }
                        catch { }
                    }
                });
                await Task.Delay(120);
                return snap;
            }
            // Same as ReadFlyoutAsync but leaves the flyout open for live
            // toggle-while-open transitions; caller must CloseFlyoutAsync.
            async Task<(bool Opened, string Trans, string Roman)> OpenHeldAsync()
            {
                (bool Opened, string Trans, string Roman) snap = (false, "?", "?");
                await OnUiAsync(window, () =>
                {
                    var button = FindByName(bar, "SecondaryOverflowButton") as Button;
                    if (button?.Flyout is Flyout flyout && button.Visibility == Visibility.Visible)
                    {
                        try { flyout.ShowAt(button); snap.Opened = true; }
                        catch { }
                    }
                });
                if (!snap.Opened) return snap;
                await Task.Delay(250);
                snap = (true, await HeldFieldAsync("Translation"), await HeldFieldAsync("Romanization"));
                return snap;
            }
            async Task<string> HeldFieldAsync(string automationName)
            {
                var result = "?";
                await OnUiAsync(window, () =>
                {
                    var button = FindByName(bar, "SecondaryOverflowButton") as Button;
                    var root = (button?.Flyout as Flyout)?.Content as FrameworkElement;
                    result = root is null ? "lost" : Field(FindByAutomationName(root, automationName));
                });
                return result;
            }
            async Task CloseFlyoutAsync()
            {
                await OnUiAsync(window, () =>
                {
                    try { ((FindByName(bar, "SecondaryOverflowButton") as Button)?.Flyout as Flyout)?.Hide(); }
                    catch { }
                });
                await Task.Delay(120);
            }
            async Task<string> ReadMainEdgesAsync()
            {
                var result = "mainedges=hidden";
                await OnUiAsync(window, () =>
                {
                    var transBox = FindByName(bar, "TransBox") as FrameworkElement;
                    var romanBox = FindByName(bar, "RomanBox") as FrameworkElement;
                    result = transBox is not null && romanBox is not null && Shown(transBox) && Shown(romanBox)
                        ? "mainedges " + Edges(transBox, romanBox, bar)
                        : "mainedges=hidden";
                });
                return result;
            }

            var wide = w >= 1152; // Edit ribbon Wide tier threshold (window px)
            var failures = new List<string>();
            void Expect(bool condition, string label)
            {
                if (!condition) failures.Add(label);
            }

            await SetTogglesAsync(false, false);
            var s0 = await ReadStateAsync();
            var f0 = await ReadFlyoutAsync();
            Expect(s0.Group == "C", "both-off group not collapsed");
            Expect(s0.Btn == "C", "both-off overflow button not hidden");
            if (f0.Opened) Expect(false, "both-off flyout should not open");
            sb.Append($"off/off group={s0.Group} btn={s0.Btn} flyout={(f0.Opened ? $"trans={f0.Trans} roman={f0.Roman} {f0.Edge}" : "hidden")}");

            await SetTogglesAsync(true, false);
            var s1 = await ReadStateAsync();
            var f1 = await ReadFlyoutAsync();
            Expect(s1.TransRow == "V" && s1.RomanRow == "C", "trans-only main rows wrong");
            Expect(s1.Group == (wide ? "V" : "C") && s1.Btn == (wide ? "C" : "V"), "trans-only group/button wrong");
            if (!wide)
            {
                Expect(f1.Opened && f1.Trans == "V" && f1.Roman != "V", "trans-only flyout wrong");
                Expect(f1.SyncT, "trans-only flyout text out of sync");
            }
            sb.Append($" | trans-only group={s1.Group} btn={s1.Btn} flyout={(f1.Opened ? $"trans={f1.Trans} roman={f1.Roman} syncT={f1.SyncT} {f1.Edge}" : "hidden")}");

            // Live transitions with the flyout held open.
            var held = await OpenHeldAsync();
            if (!wide)
            {
                Expect(held.Opened && held.Trans == "V" && held.Roman != "V", "held-open trans-only wrong");
                await SetTogglesAsync(true, true);
                var h2t = await HeldFieldAsync("Translation");
                var h2r = await HeldFieldAsync("Romanization");
                Expect(h2t == "V" && h2r == "V", "roman-on-while-open did not appear");
                await SetTogglesAsync(false, false);
                var h3t = await HeldFieldAsync("Translation");
                var h3r = await HeldFieldAsync("Romanization");
                Expect(h3t != "V" && h3r != "V", "off-while-open did not disappear");
                await CloseFlyoutAsync();
                sb.Append($" | held trans={held.Trans} both-on={h2t}/{h2r} both-off={h3t}/{h3r}");
            }
            else
            {
                if (held.Opened) await CloseFlyoutAsync();
            }

            await SetTogglesAsync(true, true);
            var s4 = await ReadStateAsync();
            var f4 = await ReadFlyoutAsync();
            var m4 = await ReadMainEdgesAsync();
            Expect(s4.TransRow == "V" && s4.RomanRow == "V", "both-on main rows wrong");
            Expect(s4.Group == (wide ? "V" : "C") && s4.Btn == (wide ? "C" : "V"), "both-on group/button wrong");
            Expect(!m4.Contains("ALIGN-FAIL"), "main field edges misaligned");
            Expect(!f4.Edge.Contains("ALIGN-FAIL"), "flyout field edges misaligned");
            if (!wide)
            {
                Expect(f4.Opened && f4.Trans == "V" && f4.Roman == "V", "both-on flyout wrong");
                Expect(f4.SyncT && f4.SyncR, "both-on flyout text out of sync");
            }
            sb.Append($" | both-on group={s4.Group} btn={s4.Btn} {m4} flyout={(f4.Opened ? $"trans={f4.Trans} roman={f4.Roman} sync={f4.SyncT}&{f4.SyncR} {f4.Edge}" : "hidden")}");

            sb.Insert(0, failures.Count == 0
                ? $"  VERIFY-PASS secondary w={w}:"
                : $"  VERIFY-FAIL secondary w={w} [{string.Join("; ", failures)}]:");
        }
        finally
        {
            await OnUiAsync(window, () =>
            {
                try
                {
                    AppSettings.ShowTranslation = origTrans;
                    AppSettings.ShowRomanization = origRoman;
                    AppSettings.NotifyDisplayChanged();
                }
                catch { }
            });
        }
        return sb.ToString();
    }

    private static string Vis(FrameworkElement? fe) =>
        fe is null ? "?" : fe.Visibility == Visibility.Visible && fe.ActualWidth > 0.5 ? "V" : "C";

    private static string RowVis(FrameworkElement? label, FrameworkElement? box) =>
        label is null || box is null ? "?"
        : label.Visibility == Visibility.Visible && box.Visibility == Visibility.Visible &&
            label.ActualWidth > 0.5 && box.ActualWidth > 0.5 ? "V" : "C";

    private static string Field(FrameworkElement? fe)
    {
        if (fe is null) return "absent";
        if (!Shown(fe)) return "C";
        return (fe as Control)?.IsEnabled == false ? "V/disabled" : "V";
    }

    private static bool Shown(FrameworkElement? fe)
    {
        if (fe is null || fe.ActualWidth <= 0.5) return false;
        var current = fe as DependencyObject;
        while (current is not null)
        {
            if (current is FrameworkElement ancestor && ancestor.Visibility != Visibility.Visible)
                return false;
            current = VisualTreeHelper.GetParent(current);
        }
        return true;
    }

    private static string Edges(FrameworkElement a, FrameworkElement b, FrameworkElement host)
    {
        var pa = a.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point(0, 0));
        var pb = b.TransformToVisual(host).TransformPoint(new Windows.Foundation.Point(0, 0));
        var dL = Math.Abs(pa.X - pb.X);
        var dR = Math.Abs(pa.X + a.ActualWidth - pb.X - b.ActualWidth);
        var dH = Math.Abs(a.ActualHeight - b.ActualHeight);
        var verdict = dL > 1 || dR > 1 || dH > 1 ? "ALIGN-FAIL" : "align-ok";
        return $"dL={dL:F1} dR={dR:F1} dH={dH:F1} {verdict}";
    }

    private static bool TextEq(FrameworkElement? fly, FrameworkElement? main) =>
        (fly as TextBox)?.Text == (main as TextBox)?.Text;

    private static FrameworkElement? FindByAutomationName(DependencyObject root, string name)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is FrameworkElement fe &&
                string.Equals(AutomationProperties.GetName(fe), name, StringComparison.Ordinal))
                return fe;
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++) queue.Enqueue(VisualTreeHelper.GetChild(current, i));
        }
        return null;
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
