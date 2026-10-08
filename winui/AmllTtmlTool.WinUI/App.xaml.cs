using Microsoft.UI.Xaml.Navigation;

namespace AmllTtmlTool.WinUI
{
    public partial class App : Application
    {
        private Window window = Window.Current;

        public Window MainWindow => window;

        public App()
        {
            this.InitializeComponent();
            this.UnhandledException += OnUnhandledException;
        }

        private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                var log = Path.Combine(Path.GetTempPath(), "amll-ttml-tool-crash.log");
                var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n---\n";
                File.AppendAllText(log, entry);
                e.Handled = true;
            }
            catch { }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            window ??= new Window();

            if (window.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                window.Content = rootFrame;
            }

            _ = rootFrame.Navigate(typeof(Views.ShellPage), e.Arguments);
            window.Activate();
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }
    }
}
