using Microsoft.UI.Xaml.Navigation;
using AmllTtmlTool.WinUI.Services;

namespace AmllTtmlTool.WinUI
{
    public partial class App : Application
    {
        private Window window = Window.Current;

        public Window MainWindow => window;

        public App()
        {
#if DEBUG
            // FIRST thing, before the window exists: capture the harness
            // mode (env vars are authoritative), write the .started marker
            // the runner polls for, and arm the watchdog that guarantees a
            // hung run dies on its own.
            Services.HarnessHost.Capture();
#endif
            this.InitializeComponent();
            this.UnhandledException += OnUnhandledException;
            // Process-level crash capture: XAML unhandled exceptions don't
            // cover background-thread throws or faulted tasks. All three
            // write exception + stack + UTC timestamp to the same log in the
            // app's local data folder, in Debug AND Release.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                CrashLog.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                CrashLog.Write("TaskScheduler.UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }

        private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                CrashLog.Write("Application.UnhandledException", e.Exception);
            }
            catch { }
#if DEBUG
            // In test mode an unhandled exception must end the run NOW with
            // a report, not leave the runner polling forever.
            try
            {
                if (Services.HarnessHost.IsTest)
                {
                    Services.HarnessHost.WriteLine("HARNESS-ERROR: " + e.Exception);
                }
            }
            catch { }
#endif
            try { e.Handled = true; } catch { }
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
#if DEBUG
            // selftest mode: marker was written before the window existed.
            // Open, wait 2s, report PASS, close — no human involvement.
            if (Services.HarnessHost.Mode == "selftest")
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    Services.HarnessHost.WriteLine("selftest | window opened, 2s wait done | OK");
                    Services.HarnessHost.Finish();
                    window.DispatcherQueue.TryEnqueue(() => window.Close());
                });
            }
#endif
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }
    }
}
