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
        Log.Info($"ShellFlow {typeof(App).Assembly.GetName().Version} starting on Windows {Environment.OSVersion.Version}{(Elevation.IsElevated ? " as administrator" : string.Empty)}.");

        // WebView2 cannot share a data folder between elevated and non-elevated processes running at the same time.
        TerminalWebEnvironment.Configure(Elevation.IsElevated ? paths.WebViewDataDirectory + "-Admin" : paths.WebViewDataDirectory);
        KeepScriptAssociationCurrent();
        var startup = Core.StartupArguments.Parse(Program.Arguments);
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

    /// <summary>
    /// If .bat/.cmd files open with ShellFlow, make sure the verb points at this executable (a portable copy may have
    /// moved); a verb pointing at a missing file would stop batch files from running at all.
    /// </summary>
    private static void KeepScriptAssociationCurrent()
    {
        try
        {
            if (ScriptAssociation.IsEnabled() && Environment.ProcessPath is { } exe)
            {
                ScriptAssociation.EnsureVerb(exe);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Warn($"Could not refresh the .bat/.cmd association: {ex.Message}");
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);

        // Keep the app (and every other running shell) alive when one UI operation fails.
        e.Handled = true;
        _window?.ShowError($"Something went wrong: {e.Exception.Message}. Details were written to the log.");
    }
}
