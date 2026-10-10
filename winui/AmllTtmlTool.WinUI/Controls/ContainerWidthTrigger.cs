using Microsoft.UI.Xaml;

namespace AmllTtmlTool.WinUI.Controls;

/// <summary>
/// A <see cref="StateTriggerBase"/> that fires on a container's own laid-out
/// width rather than the window's. <see cref="AdaptiveTrigger"/> measures the
/// window, so a ribbon inside a grid column, a split pane or a dynamic title
/// bar can never react to the space it actually has.
/// </summary>
/// <remarks>
/// The XAML compiler cannot compile <c>x:Bind</c> inside a StateTrigger, so
/// <see cref="Target"/> is assigned from the owning control's code-behind via
/// <see cref="AttachTo"/>.
/// </remarks>
public sealed class ContainerWidthTrigger : StateTriggerBase
{
    private FrameworkElement? _target;
    private bool _active;

    /// <summary>Whether this trigger last reported as active.</summary>
    internal bool IsTriggerActive => _active;

    /// <summary>The element whose ActualWidth decides the state.</summary>
    public FrameworkElement? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value)) return;
            if (_target is not null) _target.SizeChanged -= OnTargetSizeChanged;
            _target = value;
            if (_target is null)
            {
                SetActive(false);
                return;
            }
            _target.SizeChanged += OnTargetSizeChanged;
            UpdateActivity();
        }
    }

    /// <summary>Minimum width (in effective pixels) for this trigger to fire.</summary>
    public double MinWidth { get; set; }

    /// <summary>
    /// Binds this trigger to <paramref name="scope"/> and a threshold.
    /// </summary>
    /// <remarks>
    /// <paramref name="scope"/> must be an element whose width is *constrained*
    /// (a star row/column), not one that sizes to content. A content-sized
    /// element's ActualWidth only ever grows, so every threshold fires at once.
    /// </remarks>
    public void AttachTo(FrameworkElement scope, double minWidth)
    {
        MinWidth = minWidth;
        Target = scope;
    }

    private void OnTargetSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateActivity();

    private void UpdateActivity()
    {
        var active = _target is not null && MinWidth > 0 && _target.ActualWidth >= MinWidth;
        _active = active;
        SetActive(active);
    }
}
