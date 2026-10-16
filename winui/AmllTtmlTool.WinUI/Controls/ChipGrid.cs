using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace AmllTtmlTool.WinUI.Controls;

// Step-2 lightweight word chip: a plain Grid whose content is a single
// TextBlock (main [+ newline + roman] Runs, no TextBox template), built
// from the model in code and pooled with its card. Children order is
// fixed: 0 = text TextBlock, 1 = selection outline Border,
// 2 = selection wash Border (Canvas.ZIndex -1, behind the text),
// 3 = drop-mark Border. Tag holds the word id.
public sealed class ChipGrid : Grid
{
    // Callbacks so the peer reflects live model/selection state.
    public Func<string>? GetLabel { get; set; }
    public Func<bool>? GetSelected { get; set; }
    public Action? InvokeSelect { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new ChipPeer(this);

    private sealed class ChipPeer(ChipGrid owner)
        : FrameworkElementAutomationPeer(owner), IInvokeProvider, ISelectionItemProvider
    {
        protected override string GetClassNameCore() => "Chip";
        protected override string GetNameCore() =>
            owner.GetLabel?.Invoke()
            ?? Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(owner);
        protected override object? GetPatternCore(PatternInterface patternInterface)
        {
            if (patternInterface is PatternInterface.Invoke or PatternInterface.SelectionItem)
                return this;
            return base.GetPatternCore(patternInterface);
        }

        void IInvokeProvider.Invoke() => owner.InvokeSelect?.Invoke();
        bool ISelectionItemProvider.IsSelected => owner.GetSelected?.Invoke() ?? false;
        void ISelectionItemProvider.Select() => owner.InvokeSelect?.Invoke();
        void ISelectionItemProvider.AddToSelection() => owner.InvokeSelect?.Invoke();
        void ISelectionItemProvider.RemoveFromSelection() { }
        IRawElementProviderSimple? ISelectionItemProvider.SelectionContainer => null;
    }
}
