namespace AmllTtmlTool.WinUI.Controls
{
    public sealed partial class EditCommandBar : UserControl
    {
        public event Action? NewLineRequested;

        public EditCommandBar()
        {
            this.InitializeComponent();
        }

        private void OnNewLineClick(object sender, RoutedEventArgs e) =>
            NewLineRequested?.Invoke();
    }
}
