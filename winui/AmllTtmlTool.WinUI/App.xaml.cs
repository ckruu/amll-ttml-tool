using Microsoft.UI.Xaml.Navigation;
using AmllTtmlTool.WinUI.Services;

namespace AmllTtmlTool.WinUI
{
    public partial class App : Application
    {
        private Window window = Window.Current;

        public Window MainWindow => window;
#if DEBUG
        private static int FirstChanceLines;
#endif

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
#if DEBUG
            // Diagnostic net: FirstChance fires at throw time with full
            // fidelity, before any WinRT marshalling strips the details.
            // XAML failures get the full ToString; everything else gets one
            // line. The handler itself must NEVER throw (and never touch
            // anything that can throw, e.g. FileInfo.Length on a missing
            // file): a throwing FirstChance handler recurses into itself
            // and stack-overflows the process. A plain counter caps output.
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                try
                {
                    if (System.Threading.Interlocked.Increment(ref FirstChanceLines) > 400) return;
                    var ex = e.Exception;
                    if (ex is null) return;
                    var log = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(), "playerbar-xaml-error.log");
                    var isXaml = ex is Microsoft.UI.Xaml.Markup.XamlParseException
                        || (ex.Message?.Contains("XAML") ?? false)
                        || (ex.Message?.Contains("Resource with the Name/Key") ?? false);
                    if (isXaml)
                    {
                        System.IO.File.AppendAllText(log,
                            $"FIRSTCHANCE {DateTime.UtcNow:u}: {ex}\nHResult=0x{ex.HResult:X8}\n");
                    }
                    else
                    {
                        var firstLine = ex.Message?.Split('\n')[0] ?? "";
                        System.IO.File.AppendAllText(log,
                            $"firstchance: {ex.GetType().FullName}: {firstLine}\n");
                    }
                }
                catch { }
            };
#endif
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
                    try
                    {
                        Services.HarnessHost.WriteLine(
                            $"HARNESS-HRESULT: 0x{e.Exception?.HResult ?? 0:X8}");
                    }
                    catch { }
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
