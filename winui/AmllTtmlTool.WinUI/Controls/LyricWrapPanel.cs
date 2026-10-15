using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AmllTtmlTool.WinUI.Controls;

// Minimal horizontal flow panel for word chips (variable widths; the stock
// ItemsWrapGrid needs fixed item sizes, and no Toolkit package is available
// offline). Rows break on the available width; each romaji box stays glued
// under its own word because the whole chip unit is one child.
public sealed class LyricWrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double),
            typeof(LyricWrapPanel), new PropertyMetadata(0.0));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double),
            typeof(LyricWrapPanel), new PropertyMetadata(0.0));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
#if DEBUG
        var swm = System.Diagnostics.Stopwatch.StartNew();
#endif
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        double x = 0, rowHeight = 0, totalHeight = 0, widestRow = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(maxWidth, double.PositiveInfinity));
            var w = child.DesiredSize.Width;
            var h = child.DesiredSize.Height;
            if (x > 0 && x + w > maxWidth)
            {
                widestRow = Math.Max(widestRow, x - HorizontalSpacing);
                totalHeight += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }
            x += w + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, h);
        }
        if (x > 0)
        {
            widestRow = Math.Max(widestRow, x - HorizontalSpacing);
            totalHeight += rowHeight;
        }
        var result = new Size(Math.Min(widestRow, maxWidth), totalHeight);
#if DEBUG
        swm.Stop();
        ProfileMeasureMs += swm.Elapsed.TotalMilliseconds;
        ProfileMeasureCount++;
#endif
        return result;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
#if DEBUG
        var swa = System.Diagnostics.Stopwatch.StartNew();
#endif
        double x = 0, y = 0, rowHeight = 0;
        foreach (var child in Children)
        {
            var w = child.DesiredSize.Width;
            var h = child.DesiredSize.Height;
            if (x > 0 && x + w > finalSize.Width)
            {
                x = 0;
                y += rowHeight + VerticalSpacing;
                rowHeight = 0;
            }
            child.Arrange(new Rect(x, y, w, h));
            x += w + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, h);
        }
#if DEBUG
        swa.Stop();
        ProfileArrangeMs += swa.Elapsed.TotalMilliseconds;
        ProfileArrangeCount++;
#endif
        return finalSize;
    }

#if DEBUG
    // Step-1 accumulators (process-wide; reset per scenario).
    internal static long ProfileMeasureCount;
    internal static double ProfileMeasureMs;
    internal static long ProfileArrangeCount;
    internal static double ProfileArrangeMs;
    internal static void ProfileReset()
    {
        ProfileMeasureCount = 0; ProfileMeasureMs = 0;
        ProfileArrangeCount = 0; ProfileArrangeMs = 0;
    }
#endif
}
