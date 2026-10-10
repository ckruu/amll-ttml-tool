using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AmllTtmlTool.WinUI.Controls
{
    public sealed partial class PreviewCommandBar : UserControl
    {
        public PreviewCommandBar()
        {
            this.InitializeComponent();
            BindWidthTiers();
        }

        /// <summary>
        /// The ribbon reacts to the space it is actually given, never to the
        /// window's width. x:Bind cannot be compiled inside a StateTrigger, so
        /// the scope is wired in code instead.
        /// </summary>
        internal void BindWidthScope(FrameworkElement scope)
        {
            TierWide.AttachTo(scope, 2200);
            TierMedium.AttachTo(scope, 1700);
            TierNarrow.AttachTo(scope, 1180);
            TierTight.AttachTo(scope, 700);
            TierTiny.AttachTo(scope, 1);
        }

        private void BindWidthTiers() => BindWidthScope(RootGrid);
    }
}
