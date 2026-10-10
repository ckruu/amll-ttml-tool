using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AmllTtmlTool.WinUI.Controls
{
    public sealed partial class SyncCommandBar : UserControl
    {
        public SyncCommandBar()
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
            TierWide.AttachTo(scope, 1480);
            TierMedium.AttachTo(scope, 1180);
            TierNarrow.AttachTo(scope, 960);
            TierTight.AttachTo(scope, 740);
            TierTiny.AttachTo(scope, 1);
        }

        private void BindWidthTiers() => BindWidthScope(RootGrid);
    }
}
