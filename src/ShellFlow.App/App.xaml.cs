using Microsoft.UI.Xaml;
using ShellFlow.App.Services;
using ShellFlow.Core.Persistence;
using ShellFlow.TerminalHost;

namespace ShellFlow.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Fatal unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>Native handle of the main window, needed by Win32-backed pickers.</summary>
    public static IntPtr WindowHandle { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var paths = AppPaths.Default;
        Log.Initialize(paths.LogsDirectory);
        Log.Info($"ShellFlow {typeof(App).Assembly.GetName().Version} starting on Windows {Environment.OSVersion.Version}.");

        TerminalWebEnvironment.Configure(paths.WebViewDataDirectory);
        var startup = Core.StartupArguments.Parse(Environment.GetCommandLineArgs().Skip(1).ToList());
        var services = AppServices.Create(paths, startup);

        _window = new MainWindow(services);
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _window.Activate();

        // Later launches (Start menu, Win+R, "Open ShellFlow here") arrive here instead of opening a second window.
        var window = _window;
        Program.Instance?.Listen(forwarded =>
        {
            var request = Core.StartupArguments.Parse(forwarded);
            window.DispatcherQueue.TryEnqueue(() => window.HandleLaunch(request));
        });
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);

        // Keep the app (and every other running shell) alive when one UI operation fails.
        e.Handled = true;
        _window?.ShowError($"Something went wrong: {e.Exception.Message}. Details were written to the log.");
    }
}
